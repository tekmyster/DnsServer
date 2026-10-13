using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using DnsServerCore;
using DnsServerCore.Dns;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using DnsServerCore.Security;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

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
            service.DnsServer.AuthZoneManager.CreatePrimaryZone("restart-permission.example");
            var permissionManager = (DnsServerCore.Auth.AuthManager)typeof(DnsWebService).GetField("_authManager", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            Check(permissionManager.GetPermission(DnsServerCore.Auth.PermissionSection.Zones, "restart-permission.example") is null, "Missing-permission restart fixture was not established");
            typeof(DnsWebService).GetMethod("InspectAndFixZonePermissions", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, []);
            using (var postRepair = new HttpClient { BaseAddress = original.BaseAddress })
            {
                var postRepairLogin = await Call(postRepair, "/api/user/login", ("user", "admin"), ("pass", password));
                Check(postRepairLogin.GetProperty("status").GetString() == "ok", "Fresh login failed after restoring missing zone permission");
                postRepair.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", postRepairLogin.GetProperty("token").GetString());
                Check((await Call(postRepair, "/api/zones/records/add", ("domain", "probe.restart-permission.example"), ("type", "A"), ("ipAddress", "192.0.2.43"))).GetProperty("status").GetString() == "ok", "Fresh administrative session cannot modify the recovered zone");
            }
            Check((await Call(fresh, "/api/ddi/dhcp/state/get")).GetProperty("response").GetProperty("state").GetProperty("plans").GetArrayLength() == 0, "Unexpected distributed DHCP bootstrap state");
            Check((await Call(fresh, "/api/ddi/dhcp/prepare", ("state", "{}"))).GetProperty("status").GetString() == "error", "Ordinary admin session can prepare cluster ownership");
            Check((await Call(fresh, "/api/ipam/netbox/settings/get")).GetProperty("response").GetProperty("settings").GetProperty("enabled").GetBoolean() == false, "NetBox integration does not default disabled");
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
            Check((await Call(reader, "/api/ddi/dhcp/handoff", ("scope", "Default"))).GetProperty("status").GetString() == "error", "Unprivileged user can surrender DHCP ownership");
            Check((await Call(fresh, "/api/ipam/list")).GetProperty("status").GetString() == "ok", "Fresh admin access lost after lab changes");
            var securityGet = await Call(original, "/api/security/policy/get");
            Check(securityGet.GetProperty("status").GetString() == "ok" && !securityGet.GetProperty("response").GetProperty("policy").GetProperty("enabled").GetBoolean(), "Security default changes existing DNS behavior");
            Check((await Call(reader, "/api/security/policy/get")).GetProperty("status").GetString() == "error", "Security evidence leaked to unauthorized user");
            var securityPolicy = SecurityPolicy.Default with { Enabled = true, InspectAuthoritative = true, Indicators = [new("ip-rule", "Network", "203.0.113.0/24", "Block", "Local lab"), new("domain-rule", "Domain", "badname.example", "Block", "Local lab")] };
            string SerializePolicy(SecurityPolicy p) => JsonSerializer.Serialize(p, DdiSecurityManager.JsonOptions);
            var securitySet = await Call(original, "/api/security/policy/set", ("revision", "0"), ("policy", SerializePolicy(securityPolicy)));
            Check(securitySet.GetProperty("status").GetString() == "ok", "Security policy API failed: " + securitySet);
            var simulation = await Call(original, "/api/security/simulate", ("query", "ordinary.example"), ("addresses", "203.0.113.9"));
            Check(simulation.GetProperty("response").GetProperty("decision").GetProperty("action").GetString() == "Monitor", "Simulation ignores monitor mode");
            service.DnsServer.AuthZoneManager.SetRecords("example", [new DnsResourceRecord("badip.example", DnsResourceRecordType.A, DnsClass.IN, 60, new DnsARecordData(IPAddress.Parse("203.0.113.9")))]);
            service.DnsServer.AuthZoneManager.SetRecords("example", [new DnsResourceRecord("badname.example", DnsResourceRecordType.A, DnsClass.IN, 60, new DnsARecordData(IPAddress.Parse("192.0.2.11")))]);
            async Task<DnsDatagram> UdpQuery(string name)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                DnsDatagram request = new(31, false, DnsOpcode.StandardQuery, false, false, true, false, false, true, DnsResponseCode.NoError, [new DnsQuestionRecord(name, DnsResourceRecordType.A, DnsClass.IN)]);
                using MemoryStream wire = new(); request.WriteTo(wire);
                await udp.SendAsync(wire.ToArray(), new IPEndPoint(IPAddress.Loopback, dnsPort));
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                var result = await udp.ReceiveAsync(timeout.Token);
                return DnsDatagram.ReadFrom(new MemoryStream(result.Buffer));
            }
            Check((await UdpQuery("badip.example")).RCODE == DnsResponseCode.NoError, "Monitor mode rewrites live DNS response");
            securityPolicy = securityPolicy with { Mode = "Block", DhcpResponse = "DenyNewLeases", DeviceDnsResponse = "Block" };
            Check((await Call(original, "/api/security/policy/set", ("revision", "1"), ("policy", SerializePolicy(securityPolicy)))).GetProperty("status").GetString() == "ok", "Block policy activation failed");
            Check((await Call(original, "/api/security/policy/set", ("revision", "1"), ("policy", SerializePolicy(securityPolicy)))).GetProperty("status").GetString() == "error", "Stale security policy API write accepted");
            byte[] hardware = [2, 0, 0, 0, 0, 9];
            string clientIdentifier = new DnsServerCore.Dhcp.Options.ClientIdentifierOption(1, hardware).ToString();
            service.DnsServer.SecurityManager.DeviceResolver = ip => ip.Equals(IPAddress.Loopback) ? new SecurityDevice("Default", clientIdentifier, BitConverter.ToString(hardware), "lab-client", "default", "Lab", 99) : null;
            var badResponse = await UdpQuery("badip.example");
            Check(badResponse.RCODE == DnsResponseCode.Refused && !badResponse.AuthenticData && badResponse.CheckingDisabled, "Live DNS IP block flags failed");
            Check((await UdpQuery("badname.example")).RCODE == DnsResponseCode.Refused, "Live DNS domain blocking failed");
            Check((await UdpQuery("host.example")).RCODE == DnsResponseCode.Refused, "Shared device signal not applied to other DNS requests");
            Check((await UdpQuery("no-upstream.invalid")).RCODE == DnsResponseCode.Refused, "Device policy did not block before recursive lookup");
            var dhcpServer = (DnsServerCore.Dhcp.DhcpServer)typeof(DnsWebService).GetField("_dhcpServer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(service);
            DnsServerCore.Dhcp.Scope localScope = dhcpServer.GetScope("Default");
            byte[] discoverWire = new byte[244];
            discoverWire[0] = 1; discoverWire[1] = 1; discoverWire[2] = 6; hardware.CopyTo(discoverWire, 28);
            new byte[] { 99, 130, 83, 99, 53, 1, 1, 255 }.CopyTo(discoverWire, 236);
            Type messageType = typeof(DnsServerCore.Dhcp.Scope).Assembly.GetType("DnsServerCore.Dhcp.DhcpMessage");
            var discover = Activator.CreateInstance(messageType, [new MemoryStream(discoverWire)]);
            var getOffer = typeof(DnsServerCore.Dhcp.Scope).GetMethod("GetOfferAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var offer = await (Task<DnsServerCore.Dhcp.Lease>)getOffer.Invoke(localScope, [discover]);
            Check(offer is null, "DHCP allocation hook ignores DNS device signal");
            var securityEvents = await Call(fresh, "/api/security/events/list");
            Check(securityEvents.GetProperty("response").GetProperty("events").GetArrayLength() >= 3 && securityEvents.GetProperty("response").GetProperty("signals").GetArrayLength() >= 1, "Security investigation API lacks cross-service evidence");
            Check((await Call(original, "/api/security/signals/ingest", ("signals", "[]"))).GetProperty("status").GetString() == "error", "Non-cluster session allowed cluster signal injection");
            using (var peerClient = new DnsServerCore.HttpApi.HttpApiClient(original.BaseAddress, ipv6Mode: IPv6Mode.Disabled))
            {
                peerClient.UseApiToken(token);
                bool insecureTransportRejected = false;
                try { await peerClient.PushSecuritySignalsAsync("[]"); }
                catch (DnsServerCore.HttpApi.HttpApiClientException ex) { insecureTransportRejected = ex.Message.Contains("requires HTTPS"); }
                Check(insecureTransportRejected, "Peer publication can transmit signals over plaintext HTTP");
            }
            Check((await Call(reader, "/api/security/policy/set", ("revision", "2"), ("policy", SerializePolicy(securityPolicy)))).GetProperty("status").GetString() == "error", "Unauthorized security policy write accepted");
            Check((await original.GetStringAsync("/js/security.js")).Contains("refreshSecurity"), "Security console asset not served");
            using var securityBackup = await original.GetAsync("/api/settings/backup?dnsSettings=true");
            using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(await securityBackup.Content.ReadAsByteArrayAsync())))
                Check(archive.GetEntry("security.json") is not null && archive.GetEntry("security-signals.json") is not null && archive.GetEntry("security-misp.json") is not null, "Shared security state omitted from configuration transfer format");
            Check((await Call(fresh, "/api/security/policy/get")).GetProperty("status").GetString() == "ok", "Fresh administrative security access failed");
            var protectedPolicy = securityPolicy with { Misp = new MispSettings(false, "https://configured.example/"), Indicators = securityPolicy.Indicators.Append(new ThreatIndicator("inventory-ioc", "Network", "192.0.2.10", "Monitor", "Inventory lab")).ToArray() };
            Check((await Call(original, "/api/security/policy/set", ("revision", "2"), ("policy", SerializePolicy(protectedPolicy)))).GetProperty("status").GetString() == "ok", "Inventory security policy setup failed");
            var inventoryThreats = await Call(original, "/api/ipam/subnets/inspect", ("subnetId", subnetId));
            Check(inventoryThreats.GetProperty("response").GetProperty("securityFindings").GetArrayLength() > 0, "IPAM is unaware of shared threat intelligence");
            Check((await Call(original, "/api/admin/users/set", ("user", "ipam-reader-test"), ("memberOfGroups", "DNS Administrators"))).GetProperty("status").GetString() == "ok", "DNS administrator role fixture failed");
            using var dnsAdmin = new HttpClient { BaseAddress = original.BaseAddress };
            var dnsLogin = await Call(dnsAdmin, "/api/user/login", ("user", "ipam-reader-test"), ("pass", readerPassword), ("includeInfo", "true"));
            Check(dnsLogin.GetProperty("status").GetString() == "ok", "Fresh session after role change failed");
            dnsAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dnsLogin.GetProperty("token").GetString());
            Check((await Call(dnsAdmin, "/api/security/policy/get")).GetProperty("status").GetString() == "ok", "Fresh DNS admin session lacks intended view privilege");
            Check((await Call(dnsAdmin, "/api/ipam/netbox/settings/get")).GetProperty("status").GetString() == "error", "DNS-only administrator can access NetBox integration controls");
            Check((await Call(dnsAdmin, "/api/security/policy/set", ("revision", "3"), ("policy", SerializePolicy(protectedPolicy)))).GetProperty("status").GetString() == "ok", "Fresh DNS admin session lacks intended policy modify privilege");
            var unauthorizedSource = protectedPolicy with { Misp = new MispSettings(false, "https://unapproved.example/") };
            Check((await Call(dnsAdmin, "/api/security/policy/set", ("revision", "4"), ("policy", SerializePolicy(unauthorizedSource)))).GetProperty("status").GetString() == "error", "DNS-only admin can redirect MISP credential use");
            Check((await Call(fresh, "/api/security/policy/get")).GetProperty("response").GetProperty("policy").GetProperty("revision").GetInt64() == 4, "Denied source change mutated policy or fresh administrative access was lost");
            Check((await Call(fresh, "/api/security/policy/history")).GetProperty("response").GetProperty("policies").GetArrayLength() > 1, "Policy history API lacks prior revisions");
            Check((await Call(dnsAdmin, "/api/security/policy/rollback", ("revision", "4"), ("targetRevision", "1"))).GetProperty("status").GetString() == "error", "Rollback bypasses MISP configuration permission");
            Check((await Call(fresh, "/api/security/policy/rollback", ("revision", "4"), ("targetRevision", "4"))).GetProperty("response").GetProperty("policy").GetProperty("revision").GetInt64() == 5, "Rollback does not publish a new policy revision");
            var modelPolicy = JsonSerializer.Deserialize<SecurityPolicy>((await Call(fresh, "/api/security/policy/get")).GetProperty("response").GetProperty("policy").GetRawText(), DdiSecurityManager.JsonOptions) with { Llm = new LlmSettings(false, "http://127.0.0.1:11434/v1/chat/completions", "local-model") };
            Check((await Call(dnsAdmin, "/api/security/policy/set", ("revision", "5"), ("policy", SerializePolicy(modelPolicy)))).GetProperty("status").GetString() == "error", "DNS-only administrator can redirect model evidence");
            Check((await Call(original, "/api/security/policy/set", ("revision", "5"), ("policy", SerializePolicy(modelPolicy)))).GetProperty("status").GetString() == "ok", "Administrator cannot configure optional model connection");
            Check((await Call(fresh, "/api/security/policy/get")).GetProperty("response").GetProperty("policy").GetProperty("revision").GetInt64() == 6, "Fresh administrator cannot verify the model configuration change");
            Check((await Call(dnsAdmin, "/api/security/policy/rollback", ("revision", "6"), ("targetRevision", "5"))).GetProperty("status").GetString() == "error", "Rollback bypasses model configuration permission");
            Check((await Call(dnsAdmin, "/api/security/events/analyze", ("eventId", "missing"))).GetProperty("status").GetString() == "error", "DNS-only administrator can send model evidence");
            var modelEventId = securityEvents.GetProperty("response").GetProperty("events")[0].GetProperty("id").GetString();
            Check((await Call(fresh, "/api/security/events/analyze", ("eventId", modelEventId))).GetProperty("status").GetString() == "error", "Disabled model connection reports successful analysis");
            var securityManager = (DdiSecurityManager)typeof(DnsWebService).GetField("_security", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            for (int i = 0; i < 40; i++)
            {
                var analyticRequest = new DnsDatagram(42, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
                    [new DnsQuestionRecord($"abcdefghijklmnopqrstuvwxyz0123456789{i:D4}.leak.example", DnsResourceRecordType.TXT, DnsClass.IN)]);
                var analyticResponse = new DnsDatagram(42, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, DnsResponseCode.NxDomain, analyticRequest.Question);
                securityManager.InspectResponse(analyticRequest, new IPEndPoint(IPAddress.Parse("192.0.2.200"), 12345), analyticResponse);
            }
            var analyticEvent = securityManager.RecentEvents().Last();
            Check(analyticEvent.Behavior?.Findings?.Any(f => f.Kind == "DnsTunneling" && f.Score >= 70) == true && analyticEvent.Action == "Monitor", "Corroborated analytics did not reach the event API without automatic blocking");
            var modelBuilder = WebApplication.CreateBuilder();
            modelBuilder.Logging.ClearProviders(); modelBuilder.WebHost.UseUrls("http://127.0.0.1:" + FreePort());
            await using var modelFixture = modelBuilder.Build();
            modelFixture.MapPost("/v1/chat/completions", () => new { choices = new[] { new { message = new { content = "Fixture explanation: investigate the observed pattern. <b>This text must not become markup.</b>" } } } });
            await modelFixture.StartAsync();
            modelPolicy = modelPolicy with { Llm = modelPolicy.Llm with { Enabled = true, Endpoint = modelFixture.Urls.Single() + "/v1/chat/completions" } };
            Check((await Call(original, "/api/security/policy/set", ("revision", "6"), ("policy", SerializePolicy(modelPolicy)))).GetProperty("status").GetString() == "ok", "Optional local model fixture cannot be enabled");
            var analyzed = await Call(fresh, "/api/security/events/analyze", ("eventId", analyticEvent.Id));
            Check(analyzed.GetProperty("status").GetString() == "ok" && analyzed.GetProperty("response").GetProperty("analysis").GetProperty("status").GetString() == "Advisory", "Fresh administrator cannot complete a loopback model analysis");
            Check(securityManager.Policy.Revision == 7 && securityManager.RecentEvents().Last().Id == analyticEvent.Id, "Model explanation mutated protection or event evidence");
            var investigation = await Call(fresh, "/api/security/events/search", ("from", DateTime.UtcNow.AddMinutes(-10).ToString("O")), ("to", DateTime.UtcNow.AddMinutes(1).ToString("O")));
            var investigatedEvents = investigation.GetProperty("response").GetProperty("search").GetProperty("events");
            Check(investigatedEvents.GetArrayLength() > 0, "Investigation search API lacks events");
            Check((await Call(fresh, "/api/security/dispositions/set", ("eventId", investigatedEvents[0].GetProperty("id").GetString()), ("status", "Investigating"), ("notes", "Isolated test"))).GetProperty("status").GetString() == "ok", "Analyst disposition API failed");
            Console.WriteLine($"PASS: {checks} authenticated IPAM/security API and live DNS/DHCP-hook checks; loopback fresh administrative read and modify verified.");
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
