using DnsServerCore;
using DnsServerCore.Cluster;
using DnsServerCore.Dhcp;
using DnsServerCore.Dns;
using DnsServerCore.HttpApi;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TechnitiumLibrary.Net.Dns;

static class DistributedTransportChecks
{
    static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    sealed class PinnedHandler : HttpClientHandler
    {
        public bool Disconnected;
        public PinnedHandler(X509Certificate2 certificate)
        { string expected = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256); ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual is not null && actual.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == expected; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { if (Disconnected) throw new HttpRequestException("Simulated disconnected replication link"); return base.SendAsync(request, cancellationToken); }
    }
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    static async Task<JsonElement> Call(HttpClient client, string path, params (string Key, string Value)[] fields)
    {
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }
    public static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-transport-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var services = new List<DnsWebService>(); var originals = new List<HttpClient>(); var freshAdmins = new List<HttpClient>(); var tlsClients = new List<HttpClient>(); var peerApis = new List<HttpApiClient>();
        string[] names = ["127.0.0.1", "127.0.0.2", "127.0.0.3"]; int[] tlsPorts = new int[3]; string[] serviceTokens = new string[3]; string[] passwords = new string[3]; var certificates = new X509Certificate2[3];
        string[] variables = ["DNS_SERVER_ADMIN_PASSWORD", "DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES", "DNS_SERVER_WEB_SERVICE_HTTP_PORT", "DNS_SERVER_WEB_SERVICE_HTTPS_PORT", "DNS_SERVER_WEB_SERVICE_ENABLE_HTTPS", "DNS_SERVER_WEB_SERVICE_USE_SELF_SIGNED_CERT", "DNS_SERVER_WEB_SERVICE_HTTP_TO_TLS_REDIRECT", "DNS_SERVER_WEB_SERVICE_WWW_FOLDER_PATH"];
        var originalEnvironment = variables.ToDictionary(n => n, Environment.GetEnvironmentVariable); int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        try
        {
            string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            for (int index = 0; index < 3; index++)
            {
                string config = Path.Combine(folder, "node-" + index); Directory.CreateDirectory(config); int httpPort = Port(); tlsPorts[index] = Port(); passwords[index] = Guid.NewGuid().ToString("N") + "Aa!9";
                Environment.SetEnvironmentVariable("DNS_SERVER_ADMIN_PASSWORD", passwords[index]); Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES", "127.0.0.1");
                Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_HTTP_PORT", httpPort.ToString()); Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_HTTPS_PORT", tlsPorts[index].ToString());
                Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_ENABLE_HTTPS", "true"); Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_USE_SELF_SIGNED_CERT", "true"); Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_HTTP_TO_TLS_REDIRECT", "false");
                Environment.SetEnvironmentVariable("DNS_SERVER_WEB_SERVICE_WWW_FOLDER_PATH", Path.Combine(repository, "DnsServerCore/www"));
                await using (var log = new LogManager(true, config)) using (var dns = new DnsServer(config, Path.Combine(repository, "DnsServerCore/dohwww"), log))
                { dns.LoadConfigFile(); dns.LocalEndPoints = [new IPEndPoint(IPAddress.Loopback, Port())]; dns.EnableCheckForUpdate = false; dns.SaveConfigFile(true); }
                var service = new DnsWebService(true, config); services.Add(service); await service.StartAsync(true);
                var original = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + httpPort) }; originals.Add(original);
                var login = await Call(original, "/api/user/login", ("user", "admin"), ("pass", passwords[index])); Check(login.GetProperty("status").GetString() == "ok", "Original administrative login failed");
                original.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
                certificates[index] = (X509Certificate2)typeof(DnsWebService).GetProperty("WebServiceTlsCertificate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                var tls = new HttpClient(new PinnedHandler(certificates[index])) { BaseAddress = new Uri("https://127.0.0.1:" + tlsPorts[index]) }; tlsClients.Add(tls);
                // The fixture establishes enrolled membership explicitly; cluster enrollment/DANE remains a separate test.
                object auth = Field<object>(service, "_authManager"); Type sessionType = auth.GetType().Assembly.GetType("DnsServerCore.Auth.UserSessionType");
                var createSession = auth.GetType().GetMethods().Single(m => m.Name == "CreateSession" && m.GetParameters()[2].ParameterType == typeof(string));
                var session = createSession.Invoke(auth, [Enum.Parse(sessionType, "ClusterApiToken"), "isolated-replication-lab", "admin", IPAddress.Loopback, "DDI transport test"]);
                serviceTokens[index] = (string)session.GetType().GetProperty("Token").GetValue(session);
                tls.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceTokens[index]);
            }
            var linkHandlers = new List<PinnedHandler>();
            var links = new Dictionary<(int Source, int Target), PinnedHandler>();
            for (int index = 0; index < 3; index++)
            {
                var manager = Field<ClusterManager>(services[index], "_clusterManager"); var nodes = new Dictionary<int, ClusterNode>();
                for (int peer = 0; peer < 3; peer++)
                {
                    var node = new ClusterNode(manager, peer + 1, new Uri("https://" + names[peer] + ":" + tlsPorts[peer]), [IPAddress.Loopback], peer == 0 ? ClusterNodeType.Primary : ClusterNodeType.Secondary, peer == index ? ClusterNodeState.Self : ClusterNodeState.Connected); nodes.Add(peer + 1, node);
                    if (peer == index) continue;
                    var api = new HttpApiClient(node.Url, ipv6Mode: IPv6Mode.Disabled); peerApis.Add(api);
                    var pinned = new PinnedHandler(certificates[peer]); linkHandlers.Add(pinned);
                    links[(index, peer)] = pinned;
                    var transport = new HttpClient(pinned) { BaseAddress = new Uri("https://127.0.0.1:" + tlsPorts[peer]), Timeout = TimeSpan.FromSeconds(3) };
                    Field<HttpClient>(api, "_httpClient").Dispose(); typeof(HttpApiClient).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(api, transport); api.UseApiToken(serviceTokens[peer]);
                    typeof(ClusterNode).GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(node, api);
                }
                typeof(ClusterManager).GetField("_clusterNodes", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, nodes);
            }
            for (int index = 0; index < 3; index++)
            {
                // Fresh transport sessions authenticate with the new token and exercise its required write privilege.
                Check((await Call(tlsClients[index], "/api/ddi/dhcp/state/get")).GetProperty("status").GetString() == "ok", "Fresh TLS cluster token read failed");
                using (var plaintext = new HttpClient { BaseAddress = originals[index].BaseAddress })
                {
                    plaintext.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceTokens[index]);
                    Check((await Call(plaintext, "/api/ddi/dhcp/state/get")).GetProperty("status").GetString() == "error", "Direct service token can read allocation state over HTTP");
                }
                var empty = new AllocationState(1, [], []);
                Check((await Call(tlsClients[index], "/api/ddi/dhcp/state/ingest", ("state", JsonSerializer.Serialize(empty, DistributedAllocation.JsonOptions)))).GetProperty("status").GetString() == "ok", "Fresh TLS cluster token write failed");
                Check((await Call(tlsClients[index], "/api/ddi/dhcp/state/ingest?actingUser=admin", ("state", JsonSerializer.Serialize(empty, DistributedAllocation.JsonOptions)))).GetProperty("status").GetString() == "error", "Proxied session can ingest evidence");
                var fresh = new HttpClient(new PinnedHandler(certificates[index])) { BaseAddress = tlsClients[index].BaseAddress }; freshAdmins.Add(fresh);
                var login = await Call(fresh, "/api/user/login", ("user", "admin"), ("pass", passwords[index])); Check(login.GetProperty("status").GetString() == "ok", "Fresh administrator TLS authentication failed");
                fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
                Check((await Call(fresh, "/api/ddi/dhcp/state/get")).GetProperty("status").GetString() == "ok", "Fresh administrator lacks DHCP view access");
                Check((await Call(fresh, "/api/dhcp/scopes/set", ("name", "Default"), ("domainName", "lab.example"))).GetProperty("status").GetString() == "ok", "Fresh administrator lacks DHCP modify access");
            }
            var bootstrap = await Call(originals[0], "/api/ddi/dhcp/plan/set", ("scope", "Default"), ("owners", string.Join(',', names)));
            Check(bootstrap.GetProperty("status").GetString() == "ok", "Live TLS bootstrap failed: " + bootstrap);
            var stores = services.Select(s => Field<DistributedAllocation>(s, "_allocation")).ToArray();
            Check(stores.Select(s => s.Plan("Default").Epoch).Distinct().Count() == 1, "Owners installed different allocation epochs");
            var sequence = new AllocationSequence(stores[0].Plan("Default")); uint address = sequence.Candidates(names[0]).First(); DateTime expiry = DateTime.UtcNow.AddHours(1);
            Check(stores[0].Hold("Default", address, "1-0201", "02-01", "client.lab.example", expiry), "Live owner hold failed"); stores[0].Commit("Default", address, "1-0201", "02-01", "client.lab.example", expiry);
            var synchronize = typeof(DnsWebService).GetMethod("SynchronizeAllocation", BindingFlags.Instance | BindingFlags.NonPublic);
            await (Task)synchronize.Invoke(services[0], [CancellationToken.None]);
            Check(stores[1].ResolveLive(address)?.Lease.Client == "1-0201" && stores[2].ResolveLive(address)?.Lease.Client == "1-0201", "Live HTTPS replication did not reach three servers");
            var primaryScope = Field<DhcpServer>(services[0], "_dhcpServer").GetScope("Default");
            primaryScope.DomainName = "updated.lab.example"; primaryScope.LeaseTimeDays = 0; primaryScope.LeaseTimeHours = 2;
            await (Task)synchronize.Invoke(services[0], [CancellationToken.None]);
            await (Task)synchronize.Invoke(services[1], [CancellationToken.None]); await (Task)synchronize.Invoke(services[2], [CancellationToken.None]);
            Check(services.Skip(1).All(s => Field<DhcpServer>(s, "_dhcpServer").GetScope("Default").DomainName == "updated.lab.example" && Field<DhcpServer>(s, "_dhcpServer").GetScope("Default").LeaseTimeHours == 2), "Shared scope options do not synchronize over actual HTTPS");
            Check(services.All(s => !Field<DhcpServer>(s, "_dhcpServer").GetScope("Default").Enabled), "Configuration synchronization activated a local DHCP listener");
            Check(stores.All(s => s.Snapshot().Configurations?.Single().Revision >= 1), "Applied configuration revisions are missing");
            Field<DhcpServer>(services[1], "_dhcpServer").GetScope("Default").DomainName = "local-drift.example";
            await (Task)synchronize.Invoke(services[1], [CancellationToken.None]);
            Check(Field<DhcpServer>(services[1], "_dhcpServer").GetScope("Default").DomainName == "updated.lab.example", "Equal-revision desired configuration does not reconcile local option drift");
            foreach (var handler in linkHandlers) handler.Disconnected = true;
            uint secondAddress = sequence.Candidates(names[0]).Skip(1).First();
            Check(stores[0].Hold("Default", secondAddress, "1-0202", "02-02", "isolated.lab.example", expiry), "Disconnected owner cannot allocate independently"); stores[0].Commit("Default", secondAddress, "1-0202", "02-02", "isolated.lab.example", expiry);
            try { await (Task)synchronize.Invoke(services[0], [CancellationToken.None]); } catch (IOException) { }
            Check(stores[1].ResolveLive(secondAddress) is null && stores[0].FindLease("Default", "1-0202") is not null, "Failed delivery lost local evidence or pretended convergence");
            foreach (var handler in linkHandlers) handler.Disconnected = false;
            await (Task)synchronize.Invoke(services[0], [CancellationToken.None]);
            Check(stores[1].ResolveLive(secondAddress) is not null && stores[2].ResolveLive(secondAddress) is not null, "Reconnect failed to converge over actual HTTPS endpoints");
            foreach (var link in links) if (link.Key.Source == 0 || link.Key.Target == 0) link.Value.Disconnected = true;
            uint secondaryAddress = sequence.Candidates(names[1]).First();
            stores[1].Hold("Default", secondaryAddress, "1-0303", "03-03", "secondary.lab.example", expiry); stores[1].Commit("Default", secondaryAddress, "1-0303", "03-03", "secondary.lab.example", expiry);
            try { await (Task)synchronize.Invoke(services[1], [CancellationToken.None]); } catch (IOException) { }
            Check(stores[2].ResolveLive(secondaryAddress)?.Lease.Client == "1-0303", "Secondary peers cannot exchange leases when the configuration primary is unreachable");
            foreach (var handler in linkHandlers) handler.Disconnected = false;
            var grant = await Call(tlsClients[1], "/api/ddi/dhcp/grant", ("scope", "Default"), ("borrower", names[0]), ("requestId", Guid.NewGuid().ToString("N")));
            Check(grant.GetProperty("status").GetString() == "ok" && grant.GetProperty("response").GetProperty("state").ValueKind == JsonValueKind.Object, "Live donor grant endpoint failed");
            stores[0].Merge(Encoding.UTF8.GetBytes(grant.GetProperty("response").GetProperty("state").GetRawText()));
            Check(stores[0].GrantedAddresses("Default").Length == 1, "Live donor authority not usable by borrower");
            Console.WriteLine($"PASS: {checks} three-server certificate-pinned HTTPS replication, bootstrap, authentication, link failure and grant checks; loopback fresh read/modify sessions verified.");
        }
        finally
        {
            // Roll back temporary service credentials before releasing original sessions, including failed tests.
            for (int index = 0; index < serviceTokens.Length && index < services.Count; index++)
                if (serviceTokens[index] is not null) Field<DnsServerCore.Auth.AuthManager>(services[index], "_authManager").DeleteSession(serviceTokens[index]);
            // Original administrative sessions remain open through every fresh-session check.
            foreach (var client in freshAdmins) client.Dispose(); foreach (var client in tlsClients) client.Dispose(); foreach (var original in originals) original.Dispose();
            foreach (var service in services) await service.DisposeAsync(); foreach (var api in peerApis) api.Dispose();
            foreach (var pair in originalEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected lab directory"); Directory.Delete(folder, true);
        }
    }
}
