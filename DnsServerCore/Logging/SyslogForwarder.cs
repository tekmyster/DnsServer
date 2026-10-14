using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DnsServerCore.Security;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Logging;

public sealed record SyslogSettings(bool Enabled = false, string Server = "", int Port = 514,
    string Transport = "Udp", int Facility = 16, bool SystemEvents = true, bool SecurityEvents = true,
    bool DnsQueries = false, string TcpFraming = "OctetCounting", long Revision = 0);
public sealed record SyslogHealth(string State, long Queued, long Sent, long Dropped, long Failed,
    DateTime? LastSentUtc, string LastError);

/// <summary>Optional, bounded, best-effort forwarding. Producers never wait for the network.</summary>
public sealed class SyslogForwarder : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int QueueCapacity = 1024;
    readonly string _path;
    readonly Func<string> _hostname;
    readonly SemaphoreSlim _configure = new(1);
    readonly object _healthLock = new();
    SyslogSettings _settings = new();
    RunSession _run;
    long _sent, _dropped, _failed;
    DateTime? _lastSent;
    string _lastError = "";
    bool _disposed;

    public SyslogForwarder(string configFolder, Func<string> hostname)
    {
        _path = Path.Combine(configFolder, "syslog.json"); _hostname = hostname;
        try {
            if (File.Exists(_path)) {
                if (new FileInfo(_path).Length > 4096) throw new ArgumentException("Syslog settings are too large.");
                _settings = Validate(JsonSerializer.Deserialize<SyslogSettings>(File.ReadAllBytes(_path), JsonOptions) ?? new());
            }
        } catch { _lastError = "Saved syslog settings could not be loaded. Configure forwarding again."; }
        if (_settings.Enabled) _run = new RunSession(this, _settings);
    }
    public SyslogSettings Settings => Volatile.Read(ref _settings);
    public SyslogHealth Health {
        get {
            var run = Volatile.Read(ref _run);
            lock (_healthLock) return new(!Settings.Enabled ? "Disabled" : _lastError.Length > 0 ? "Error" : _lastSent is null ? "Waiting" : "Sending",
                run?.Pending ?? 0, Interlocked.Read(ref _sent), Interlocked.Read(ref _dropped), Interlocked.Read(ref _failed), _lastSent, _lastError);
        }
    }
    static SyslogSettings Validate(SyslogSettings settings)
    {
        string server = (settings.Server ?? "").Trim();
        if (server.Length > 253 || server.Any(char.IsControl) || server.Any(char.IsWhiteSpace) ||
            server.Length > 0 && !IPAddress.TryParse(server, out _) && Uri.CheckHostName(server) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a hostname or IP address without a scheme, path or port.");
        if (settings.Enabled && server.Length == 0) throw new ArgumentException("Enter the syslog server address before enabling forwarding.");
        if (settings.Port < 1 || settings.Port > 65535) throw new ArgumentException("Syslog port must be between 1 and 65535.");
        if (settings.Transport is not ("Udp" or "Tcp")) throw new ArgumentException("Choose UDP or TCP for syslog forwarding.");
        if (settings.TcpFraming is not ("OctetCounting" or "LineDelimited")) throw new ArgumentException("Choose octet counting or line delimited TCP framing.");
        if (settings.Facility < 0 || settings.Facility > 23) throw new ArgumentException("Choose a valid syslog facility.");
        if (settings.Enabled && !settings.SystemEvents && !settings.SecurityEvents && !settings.DnsQueries) throw new ArgumentException("Select at least one event type to forward.");
        if (settings.Revision < 0) throw new ArgumentException("Invalid syslog settings revision.");
        return settings with { Server = server };
    }
    public async Task ConfigureAsync(SyslogSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        settings = Validate(settings);
        await _configure.WaitAsync(cancellationToken);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Settings.Revision != expectedRevision) throw new InvalidOperationException("Syslog settings changed. Reload them before saving.");
            settings = settings with { Revision = checked(Settings.Revision + 1) };
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    file.Write(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions)); file.Flush(true);
                }
                File.Move(temp, _path, true);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            var previous = Interlocked.Exchange(ref _run, null);
            if (previous is not null) await previous.DisposeAsync();
            Volatile.Write(ref _settings, settings);
            lock (_healthLock) { _lastSent = null; _lastError = ""; }
            if (settings.Enabled) Volatile.Write(ref _run, new RunSession(this, settings));
        } finally { _configure.Release(); }
    }
    public bool RecordSystem(string message, int severity = 6) => Settings.SystemEvents && Queue("system", message, severity);
    public async Task RestoreAsync(Stream source, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[4097]; int count = 0;
        while (count < buffer.Length) { int read = await source.ReadAsync(buffer.AsMemory(count), cancellationToken); if (read == 0) break; count += read; }
        if (count > 4096) throw new ArgumentException("Syslog settings are too large.");
        var settings = JsonSerializer.Deserialize<SyslogSettings>(buffer.AsSpan(0, count), JsonOptions) ?? throw new ArgumentException("Enter syslog settings.");
        await ConfigureAsync(settings, Settings.Revision, cancellationToken);
    }
    public void RecordSecurity(SecurityEvent item)
    {
        if (!Settings.Enabled || !Settings.SecurityEvents) return;
        try {
        string message = JsonSerializer.Serialize(new { item.Id, item.Timestamp, item.Node, item.Service, item.ClientAddress, item.Query,
            item.QueryType, item.ResponseCode, item.Action, score = item.Behavior?.Score, reason = Limit(item.Reason, 512),
            device = item.Device is null ? null : new { scope = Limit(item.Device.Scope,128), clientIdentifier = Limit(item.Device.ClientIdentifier,128),
                hardwareAddress = Limit(item.Device.HardwareAddress,64), hostName = Limit(item.Device.HostName,253), site = Limit(item.Device.Site,64), item.Device.Vlan },
            indicators = (item.Matches ?? []).Take(4).Select(m => new { m.Kind, value = Limit(m.Value, 253), source = Limit(m.Source, 64) }),
            additionalMatches = item.AdditionalMatches + Math.Max(0, (item.Matches?.Length ?? 0) - 4) }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(message) > 7000) message = JsonSerializer.Serialize(new { item.Id, item.Timestamp, item.Service,
            item.ClientAddress, item.Query, item.Action, score = item.Behavior?.Score, detailsLimited = true }, JsonOptions);
        Queue("security", message, item.Action is "Block" or "DenyNewLease" ? 4 : 6, true);
        } catch { Interlocked.Increment(ref _dropped); }
    }
    public void RecordDnsQuery(IPEndPoint client, DnsTransportProtocol protocol, DnsDatagram request, DnsDatagram response)
    {
        if (!Settings.Enabled || !Settings.DnsQueries) return;
        try {
        var question = request?.Question.FirstOrDefault();
        Queue("dns", JsonSerializer.Serialize(new { clientAddress = client?.Address.ToString(), transport = protocol.ToString(),
            query = question?.Name, queryType = question?.Type.ToString(), responseCode = response?.RCODE.ToString() ?? "NoResponse" }, JsonOptions), response is null ? 3 : 6, true);
        } catch { Interlocked.Increment(ref _dropped); }
    }
    public bool SendTest() => Queue("test", "teQnatium syslog forwarding test", 6);
    bool Queue(string category, string message, int severity, bool structured = false)
    {
        try { return QueueCore(category, message, severity, structured); }
        catch { Interlocked.Increment(ref _dropped); return false; }
    }
    bool QueueCore(string category, string message, int severity, bool structured)
    {
        var run = Volatile.Read(ref _run);
        if (run is null) return false;
        // Normalize control characters so one event cannot inject another RFC 5424 message or line-delimited frame.
        message = (structured ? message : Limit(message, 2000)).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0");
        string host = new string((_hostname() ?? "-").Take(255).Select(c => c is >= '!' and <= '~' ? c : '_').ToArray());
        if (host.Length == 0) host = "-";
        string header = "<" + (run.Settings.Facility * 8 + Math.Clamp(severity, 0, 7)) + ">1 " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + " " + host + " teQnatium " + Environment.ProcessId + " " + category + " - \uFEFF";
        string payload = header + message;
        byte[] buffer = new byte[Math.Min(8192, Encoding.UTF8.GetByteCount(payload))];
        Encoding.UTF8.GetEncoder().Convert(payload.AsSpan(), buffer, true, out _, out int bytesUsed, out _);
        bool queued = run.Enqueue(bytesUsed == buffer.Length ? buffer : buffer[..bytesUsed]);
        if (!queued) Interlocked.Increment(ref _dropped);
        return queued;
    }
    static string Limit(string value, int count) => value is null ? "" : value.Length <= count ? value : value[..count];
    public async ValueTask DisposeAsync()
    {
        await _configure.WaitAsync();
        try { if (_disposed) return; _disposed = true; var run = Interlocked.Exchange(ref _run, null); if (run is not null) await run.DisposeAsync(); }
        finally { _configure.Release(); }
    }
    sealed class RunSession : IAsyncDisposable
    {
        readonly SyslogForwarder _owner;
        readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        readonly CancellationTokenSource _stop = new();
        readonly Task _worker;
        Socket _udp;
        TcpClient _tcp;
        long _pending;
        public SyslogSettings Settings { get; }
        public long Pending => Math.Max(0, Interlocked.Read(ref _pending));
        public RunSession(SyslogForwarder owner, SyslogSettings settings) { _owner = owner; Settings = settings; _worker = Task.Run(WorkAsync); }
        public bool Enqueue(byte[] bytes)
        { Interlocked.Increment(ref _pending); if (_queue.Writer.TryWrite(bytes)) return true; Interlocked.Decrement(ref _pending); return false; }
        async Task WorkAsync()
        {
            try {
                await foreach (byte[] bytes in _queue.Reader.ReadAllAsync(_stop.Token)) {
                    Interlocked.Decrement(ref _pending);
                    try {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        if (Settings.Transport == "Udp") {
                            if (_udp is null) {
                                var addresses = await System.Net.Dns.GetHostAddressesAsync(Settings.Server, timeout.Token);
                                var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
                                _udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp); _udp.Connect(new IPEndPoint(address, Settings.Port));
                            }
                            int sent = await _udp.SendAsync(bytes, SocketFlags.None, timeout.Token);
                            if (sent != bytes.Length) throw new IOException("Incomplete UDP submission.");
                        } else {
                            if (_tcp is null) { _tcp = new TcpClient(); await _tcp.ConnectAsync(Settings.Server, Settings.Port, timeout.Token); }
                            byte[] frame = Settings.TcpFraming == "OctetCounting" ? Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + " ").Concat(bytes).ToArray() : bytes.Concat(new byte[] { 10 }).ToArray();
                            await _tcp.GetStream().WriteAsync(frame, timeout.Token);
                        }
                        Interlocked.Increment(ref _owner._sent);
                        lock (_owner._healthLock) { _owner._lastSent = DateTime.UtcNow; _owner._lastError = ""; }
                    } catch (Exception) when (_stop.IsCancellationRequested) {
                        Interlocked.Increment(ref _owner._dropped); throw;
                    } catch (Exception) {
                        _tcp?.Dispose(); _tcp = null; _udp?.Dispose(); _udp = null;
                        Interlocked.Increment(ref _owner._failed); Interlocked.Increment(ref _owner._dropped);
                        lock (_owner._healthLock) _owner._lastError = "Could not send to the syslog server. Check its address, port and transport.";
                        await Task.Delay(1000, _stop.Token);
                    }
                }
            } catch (Exception) when (_stop.IsCancellationRequested) { }
            finally {
                _tcp?.Dispose(); _udp?.Dispose();
                while (_queue.Reader.TryRead(out _)) { Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _owner._dropped); }
            }
        }
        public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); _stop.Cancel(); await _worker; _stop.Dispose(); }
    }
}
