using DnsServerCore;
using DnsServerCore.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

static class SyslogChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        string folder = Path.Combine(Path.GetTempPath(), "teqtanium-syslog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            await using (var sender = new SyslogForwarder(folder, () => "teqtanium-test.example")) {
                Check(!sender.Settings.Enabled && !sender.SendTest(), "Disabled forwarding must not submit a message.");
                await sender.ConfigureAsync(new(Enabled:true, Server:"127.0.0.1", Port:port), 0);
                Check(sender.SendTest(), "UDP test was not queued.");
                var first = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                string wire = new UTF8Encoding(false, true).GetString(first.Buffer);
                Check(Regex.IsMatch(wire, @"^<134>1 \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z teqtanium-test\.example teQtanium \d+ test - "), "Collector could not parse RFC 5424 header.");
                Check(wire.Contains("teQtanium syslog forwarding test"), "Test content missing.");
                sender.RecordSecurity(new DnsServerCore.Security.SecurityEvent("unicode",DateTime.UtcNow,"test","DNS","127.0.0.1","bad.example","A","Refused","Block",0,null,
                    Enumerable.Range(0,4).Select(i=>new DnsServerCore.Security.ThreatIndicator(i.ToString(),"Domain",new string('漢',253),"Block",new string('漢',64))).ToArray(),null,new string('漢',1024)));
                var security=await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));string securityWire=new UTF8Encoding(false,true).GetString(security.Buffer);
                using(var evidence=JsonDocument.Parse(securityWire[(securityWire.IndexOf(" - \uFEFF",StringComparison.Ordinal)+4)..]))
                    Check(security.Buffer.Length<=8192 && evidence.RootElement.GetProperty("detailsLimited").GetBoolean(),"Oversize security evidence was not valid bounded JSON.");
                await using (var logger = new LogManager(true, folder)) {
                    logger.LoggingType = LoggingType.None; logger.SyslogForwarder = sender;
                    logger.Write(new InvalidOperationException("collector severity check"));
                    var error = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(Encoding.UTF8.GetString(error.Buffer).StartsWith("<131>1 "), "System errors must use error severity.");
                    logger.Write(new string('é', 2000) + "\r\ninjection");
                    var unicode = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(unicode.Buffer.Length <= 8192 && new UTF8Encoding(false,true).GetString(unicode.Buffer).Contains('é'), "UTF-8 wire length or encoding invalid.");
                }
                bool stale = false; try { await sender.ConfigureAsync(sender.Settings, 0); } catch (InvalidOperationException) { stale = true; }
                Check(stale && sender.Settings.Revision == 1, "Stale revision changed forwarding.");
                bool invalid = false; try { await sender.ConfigureAsync(sender.Settings with { Server="http://collector/" }, 1); } catch (ArgumentException) { invalid=true; }
                Check(invalid && sender.Settings.Server == "127.0.0.1", "Invalid destination changed forwarding.");
                using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int tcpPort=((IPEndPoint)tcp.LocalEndpoint).Port;
                await sender.ConfigureAsync(sender.Settings with { Transport="Tcp", Port=tcpPort }, 1);
                var connection = tcp.AcceptTcpClientAsync();
                Check(sender.RecordSystem("first π\r\nsecond line"), "TCP event was not queued.");
                using var collector = await connection.WaitAsync(TimeSpan.FromSeconds(5));
                using var stream = collector.GetStream();
                async Task<string> ReadOctetFrame() {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var prefix = new StringBuilder(); byte[] one = new byte[1];
                    while (true) { await stream.ReadExactlyAsync(one, timeout.Token); if (one[0]==32) break; prefix.Append((char)one[0]); if(prefix.Length>5) throw new Exception("Invalid frame length."); }
                    int length=int.Parse(prefix.ToString()); Check(length>0 && length<=8192, "Invalid TCP frame length.");
                    byte[] payload=new byte[length]; await stream.ReadExactlyAsync(payload,timeout.Token); return new UTF8Encoding(false,true).GetString(payload);
                }
                string tcpWire=await ReadOctetFrame();
                Check(tcpWire.Contains("π\\r\\nsecond line") && !tcpWire.Contains('\n'), "Line injection or UTF-8 octet count failed.");
                sender.RecordSystem("persistent connection");
                Check((await ReadOctetFrame()).Contains("persistent connection"), "TCP connection was not reused.");
                await sender.ConfigureAsync(sender.Settings with { TcpFraming="LineDelimited" }, 2);
                var newConnection=tcp.AcceptTcpClientAsync(); sender.SendTest();
                using var lineClient=await newConnection.WaitAsync(TimeSpan.FromSeconds(5)); using var lineReader=new StreamReader(lineClient.GetStream(),new UTF8Encoding(false,true));
                Check((await lineReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Contains("syslog forwarding test"), "Line-delimited collector could not read message.");
                await sender.ConfigureAsync(sender.Settings with { Enabled=false }, 3);
                Check(sender.Health.State=="Disabled" && !sender.RecordSystem("disabled"), "Disabled sender accepted events.");
            }
            await using (var restored = new SyslogForwarder(folder,()=>"restored")) Check(restored.Settings.Revision==4 && !restored.Settings.Enabled, "Saved forwarding settings were lost.");
            using var unreachable = new TcpListener(IPAddress.Loopback,0); unreachable.Start();int closedPort=((IPEndPoint)unreachable.LocalEndpoint).Port;unreachable.Stop();
            await using (var outage = new SyslogForwarder(folder,()=>"outage")) {
                await outage.ConfigureAsync(outage.Settings with { Enabled=true,Transport="Tcp",Port=closedPort },4);
                var time=System.Diagnostics.Stopwatch.StartNew(); for(int i=0;i<5000;i++) outage.RecordSystem("outage event");
                Check(time.Elapsed<TimeSpan.FromSeconds(3), "Collector outage blocked the producer.");
                Check(outage.Health.Queued<=SyslogForwarder.QueueCapacity && outage.Health.Dropped>0, "Outage queue did not remain bounded.");
                var deadline=DateTime.UtcNow.AddSeconds(5); while(outage.Health.Failed==0 && DateTime.UtcNow<deadline) await Task.Delay(50);
                Check(outage.Health.Failed>0 && outage.Health.LastError.Length>0, "Transport failure was not visible.");
            }
            File.WriteAllText(Path.Combine(folder,"syslog.json"),"invalid");
            await using(var broken = new SyslogForwarder(folder,()=>"broken")) Check(!broken.Settings.Enabled && broken.Health.LastError.Length>0, "Corrupt forwarding settings must not prevent service startup.");
            if(Socket.OSSupportsIPv6)
            {
                string v6Folder=Path.Combine(folder,"v6");Directory.CreateDirectory(v6Folder);
                using var v6Udp=new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback,0));
                await using var v6Sender=new SyslogForwarder(v6Folder,()=>"ipv6");
                await v6Sender.ConfigureAsync(new(Enabled:true,Server:"::1",Port:((IPEndPoint)v6Udp.Client.LocalEndPoint!).Port),0);v6Sender.SendTest();
                Check(Encoding.UTF8.GetString((await v6Udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer).Contains("syslog forwarding test"),"IPv6 UDP collector failed");
                using var v6Tcp=new TcpListener(IPAddress.IPv6Loopback,0);v6Tcp.Start();
                await v6Sender.ConfigureAsync(v6Sender.Settings with{Transport="Tcp",Port=((IPEndPoint)v6Tcp.LocalEndpoint).Port},1);
                var accepted=v6Tcp.AcceptTcpClientAsync();v6Sender.SendTest();using var v6Client=await accepted.WaitAsync(TimeSpan.FromSeconds(5));
                byte[] data=new byte[512];int length=await v6Client.GetStream().ReadAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Check(length>0,"IPv6 TCP collector failed");
            }
            Console.WriteLine($"PASS: {checks} real UDP/TCP collector, framing, Unicode, persistence, severity, disabled-state and bounded-outage syslog checks.");
        } finally { Directory.Delete(folder,true); }
    }
}
