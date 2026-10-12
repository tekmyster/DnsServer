using DnsServerCore;
using DnsServerCore.Dns;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

static class ApiChecks
{
    static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    public static async Task Run(bool serve)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-ipam-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int port = FreePort();
        int dnsPort = FreePort();
        string password = Guid.NewGuid().ToString("N") + "Aa!9";
        Environment.SetEnvironmentVariable("DNS_SERVER_ADMIN_PASSWORD", password);
        Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES", "127.0.0.1");
        Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_HTTP_PORT", port.ToString());
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_WWW_FOLDER_PATH", Path.Combine(repository, "DnsServerCore/www"));
        await using (var log = new LogManager(true, folder))
        using (var dns = new DnsServer(folder, Path.Combine(repository, "DnsServerCore/dohwww"), log))
        {
            dns.LoadConfigFile();
            dns.LocalEndPoints = [new IPEndPoint(IPAddress.Loopback, dnsPort)];
            dns.EnableCheckForUpdate = false;
            dns.SaveConfigFile(true);
        }
        await using var service = new DnsWebService(true, folder);
        try
        {
            await service.StartAsync(true);
            using var original = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
            int checks = 0;
            void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
            async Task<JsonElement> Call(HttpClient client, string path, params (string Key, string Value)[] parameters)
            {
                using var content = new FormUrlEncodedContent(parameters.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
                using var response = await client.PostAsync(path, content);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.Clone();
            }
            var denied = await Call(original, "/api/ipam/list");
            Check(denied.GetProperty("status").GetString() == "invalid-token", "Unauthenticated access was allowed");
            var login = await Call(original, "/api/user/login", ("user", "admin"), ("pass", password), ("includeInfo", "true"));
            Check(login.GetProperty("status").GetString() == "ok", "Admin authentication failed");
            // Login responses have a top-level token; keep this session open throughout validation.
            string token = login.GetProperty("token").GetString();
            original.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var inventory = await Call(original, "/api/ipam/list");
            Check(inventory.GetProperty("response").GetProperty("inventory").GetProperty("revision").GetInt64() == 0, "Unexpected initial inventory");
            var subnet = await Call(original, "/api/ipam/subnets/set", ("revision", "0"), ("prefix", "192.0.2.0/24"), ("name", "API lab"));
            Check(subnet.GetProperty("status").GetString() == "ok", "Subnet API failed: " + subnet);
            string subnetId = subnet.GetProperty("response").GetProperty("subnet").GetProperty("id").GetString();
            var assignment = await Call(original, "/api/ipam/assignments/set", ("revision", "1"), ("subnetId", subnetId), ("address", "192.0.2.10"), ("hostName", "host.example"));
            Check(assignment.GetProperty("status").GetString() == "ok", "Assignment API failed");
            var stale = await Call(original, "/api/ipam/assignments/set", ("revision", "1"), ("subnetId", subnetId), ("address", "192.0.2.11"));
            Check(stale.GetProperty("status").GetString() == "error", "Stale API write allowed");
            var next = await Call(original, "/api/ipam/addresses/nextAvailable", ("subnetId", subnetId));
            Check(next.GetProperty("response").GetProperty("address").GetString() == "192.0.2.1", "Available address API failed");
            var createdZone = await Call(original, "/api/zones/create", ("zone", "example"), ("type", "Primary"));
            Check(createdZone.GetProperty("status").GetString() == "ok", "Zone creation failed: " + createdZone);
            service.DnsServer.AuthZoneManager.SetRecords("example", [new TechnitiumLibrary.Net.Dns.ResourceRecords.DnsResourceRecord("host.example", TechnitiumLibrary.Net.Dns.ResourceRecords.DnsResourceRecordType.A, TechnitiumLibrary.Net.Dns.ResourceRecords.DnsClass.IN, 60, new TechnitiumLibrary.Net.Dns.ResourceRecords.DnsARecordData(IPAddress.Parse("192.0.2.10")))]);
            var linked = await Call(original, "/api/ipam/subnets/set", ("revision", "2"), ("id", subnetId), ("prefix", "192.0.2.0/24"), ("name", "API lab"), ("dnsZone", "example"));
            Check(linked.GetProperty("status").GetString() == "ok", "Zone linkage failed: " + linked);
            var inspect = await Call(original, "/api/ipam/subnets/inspect", ("subnetId", subnetId));
            Check(inspect.GetProperty("response").GetProperty("observations").GetArrayLength() == 1 && inspect.GetProperty("response").GetProperty("findings").GetArrayLength() == 0, "DNS reconciliation failed");
            var html = await original.GetStringAsync("/");
            Check(html.Contains("mainPanelTabPaneIpam") && (await original.GetStringAsync("/js/ipam.js")).Contains("refreshIpam"), "IPAM UI assets unavailable");
            // Independently authenticate and verify administrative access before releasing the original session.
            using var fresh = new HttpClient { BaseAddress = original.BaseAddress };
            var freshLogin = await Call(fresh, "/api/user/login", ("user", "admin"), ("pass", password), ("includeInfo", "true"));
            Check(freshLogin.GetProperty("status").GetString() == "ok", "Fresh session login failed");
            fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", freshLogin.GetProperty("token").GetString());
            var freshInventory = await Call(fresh, "/api/ipam/list");
            Check(freshInventory.GetProperty("response").GetProperty("inventory").GetProperty("revision").GetInt64() == 3, "Fresh session cannot read intended state");
            var freshWrite = await Call(fresh, "/api/ipam/views/set", ("revision", "3"), ("name", "Fresh session validated"));
            Check(freshWrite.GetProperty("status").GetString() == "ok", "Fresh session lacks administrative modify privilege");
            var dhcpSubnet = await Call(original, "/api/ipam/subnets/set", ("revision", "4"), ("prefix", "192.168.1.0/24"), ("name", "DHCP lab"), ("dhcpScope", "Default"));
            Check(dhcpSubnet.GetProperty("status").GetString() == "ok", "DHCP scope linkage failed: " + dhcpSubnet);
            string dhcpSubnetId = dhcpSubnet.GetProperty("response").GetProperty("subnet").GetProperty("id").GetString();
            var reserved = await Call(original, "/api/dhcp/scopes/addReservedLease", ("name", "Default"), ("hardwareAddress", "02-00-00-00-00-01"), ("ipAddress", "192.168.1.50"), ("hostName", "reserved.home"));
            Check(reserved.GetProperty("status").GetString() == "ok", "DHCP reservation setup failed");
            var dhcpInspect = await Call(original, "/api/ipam/subnets/inspect", ("subnetId", dhcpSubnetId));
            Check(dhcpInspect.GetProperty("response").GetProperty("observations").EnumerateArray().Any(o => o.GetProperty("source").GetString() == "DhcpReservation"), "DHCP reservation not reconciled");
            using var backupResponse = await original.GetAsync("/api/settings/backup?dnsSettings=true");
            byte[] backupBytes = await backupResponse.Content.ReadAsByteArrayAsync();
            using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(backupBytes)))
                Check(archive.GetEntry("ipam.json") is not null, "Backup omits IPAM inventory");
            var changedAfterBackup = await Call(original, "/api/ipam/views/set", ("revision", "5"), ("name", "Discard during restore"));
            Check(changedAfterBackup.GetProperty("status").GetString() == "ok", "Pre-restore mutation failed");
            using var upload = new MultipartFormDataContent();
            upload.Add(new StringContent("true"), "dnsSettings");
            upload.Add(new ByteArrayContent(backupBytes), "file", "ipam-lab.zip");
            using var restoredResponse = await original.PostAsync("/api/settings/restore", upload);
            using var restoredJson = JsonDocument.Parse(await restoredResponse.Content.ReadAsStringAsync());
            Check(restoredJson.RootElement.GetProperty("status").GetString() == "ok", "Settings restore failed: " + restoredJson.RootElement);
            var restoredInventory = await Call(fresh, "/api/ipam/list");
            Check(restoredInventory.GetProperty("response").GetProperty("inventory").GetProperty("revision").GetInt64() == 5, "Restored IPAM state not active");
            string readerPassword = Guid.NewGuid().ToString("N") + "Bb!8";
            var readerCreated = await Call(original, "/api/admin/users/create", ("user", "ipam-reader-test"), ("pass", readerPassword));
            Check(readerCreated.GetProperty("status").GetString() == "ok", "Permission test user setup failed");
            using var reader = new HttpClient { BaseAddress = original.BaseAddress };
            var readerLogin = await Call(reader, "/api/user/login", ("user", "ipam-reader-test"), ("pass", readerPassword), ("includeInfo", "true"));
            Check(readerLogin.GetProperty("status").GetString() == "ok", "Fresh test user authentication failed");
            reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerLogin.GetProperty("token").GetString());
            Check((await Call(reader, "/api/ipam/list")).GetProperty("status").GetString() == "error", "IPAM inventory leaked to user without Settings access");
            Check((await Call(reader, "/api/ipam/views/set", ("revision", "5"), ("name", "Forbidden"))).GetProperty("status").GetString() == "error", "Unauthorized IPAM modification allowed");
            Check((await Call(fresh, "/api/ipam/list")).GetProperty("status").GetString() == "ok", "Fresh admin access lost after lab changes");
            Console.WriteLine($"PASS: {checks} authenticated IPAM API checks; both sessions use loopback source; fresh read and modify verified.");
            if (serve)
            {
                string connectionFile = Path.Combine(AppContext.BaseDirectory, "lab-connection.json");
                File.WriteAllText(connectionFile, JsonSerializer.Serialize(new { url = original.BaseAddress.ToString(), user = "admin", password, configFolder = folder }));
                Console.WriteLine("Isolated UI lab ready; connection details saved under test output directory.");
                string stopFile = connectionFile + ".stop";
                try { while (!File.Exists(stopFile)) await Task.Delay(250); }
                finally { File.Delete(connectionFile); if (File.Exists(stopFile)) File.Delete(stopFile); }
            }
        }
        finally
        {
            await service.DisposeAsync();
            if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected lab directory");
            Directory.Delete(folder, true);
        }
    }
}
