using DnsServerCore.Ipam;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static class NetBoxChecks
{
    sealed class Stub : HttpMessageHandler
    {
        public readonly Dictionary<string, List<JsonObject>> Objects = new() { ["prefixes"] = [], ["ip-addresses"] = [] };
        public int Writes, Gets; public bool ForeignPagination, UnknownCreateOutcome; public string Scheme = "Bearer";
        int version = 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Scheme != Scheme || request.Headers.Authorization?.Parameter != "test-key") throw new Exception("Wrong NetBox credential header");
            string[] path = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string kind = path[2]; JsonObject result; string etag = null;
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                if (path.Length == 3)
                {
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { count = Objects[kind].Count, next = ForeignPagination ? "https://untrusted.test/api/ipam/prefixes/" : null, results = Objects[kind] }), Encoding.UTF8, "application/json") };
                }
                result = Objects[kind].Single(o => (int)o["id"] == int.Parse(path[3])); etag = "W/\"" + (string)result["last_updated"] + "\"";
            }
            else
            {
                Writes++; result = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).AsObject();
                if (request.Method == HttpMethod.Patch)
                {
                    var current = Objects[kind].Single(o => (int)o["id"] == int.Parse(path[3]));
                    if (!request.Headers.TryGetValues("If-Match", out var values) || values.Single() != "W/\"" + (string)current["last_updated"] + "\"") throw new Exception("Conditional write not used");
                    result["id"] = (int)current["id"]; Objects[kind].Remove(current);
                }
                else result["id"] = Objects[kind].Count + 1;
                if (result["vrf"] is not null) result["vrf"] = new JsonObject { ["id"] = (int)result["vrf"] };
                result["status"] = new JsonObject { ["value"] = (string)result["status"] };
                result["last_updated"] = "version-" + ++version; Objects[kind].Add(result);
                if (UnknownCreateOutcome && request.Method == HttpMethod.Post) throw new HttpRequestException("Lost response after remote create");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") };
            if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag); return response;
        }
    }
    public static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-netbox-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        async Task Reject(Func<Task> action, string why) { try { await action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } catch (HttpRequestException) { checks++; return; } throw new Exception(why); }
        Environment.SetEnvironmentVariable("DDI_NETBOX_TEST_API_KEY", "test-key");
        try
        {
            var ipam = new IpamManager(Path.Combine(folder, "ipam.json"));
            var subnet = ipam.PutSubnet(0, "test", "", "default", "192.0.2.0/24", "Lab", "East", 20, "", "");
            ipam.PutAssignment(1, "test", "", subnet.Id, "192.0.2.9", "host.example", "Owner", "Assigned", "Client");
            var stub = new Stub(); using var connector = new NetBoxConnector(folder, new HttpClient(stub));
            var settings = new NetBoxSettings(true, "https://netbox.test/", "DDI_NETBOX_TEST_API_KEY", "Bearer", [new("default", null)]);
            await connector.ConfigureAsync(settings);
            var preview = await connector.PreviewAsync(ipam.Snapshot());
            Check(preview.Changes.Count(c => c.Operation == "Create") == 2 && stub.Writes == 0, "Preview writes or fails to propose prefix and address");
            await Reject(() => connector.ApplyAsync(preview.Id, 99), "Stale local revision accepted");
            Check((await connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision)).Applied == 2 && stub.Writes == 2, "Preview publication did not create objects");
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision), "Submitted preview is blindly retried");
            Check((await connector.PreviewAsync(ipam.Snapshot())).Changes.All(c => c.Operation == "Unchanged"), "Idempotent reconciliation produces changes");
            string addressMarker = stub.Objects["ip-addresses"][0]["comments"].ToString();
            stub.Objects["ip-addresses"][0]["comments"] = "Operator notes\n\n" + addressMarker;
            var annotated = await connector.PreviewAsync(ipam.Snapshot());
            Check(annotated.Changes.Single(c => c.Kind == "ip-addresses").Operation == "Unchanged" && annotated.Changes.Single(c => c.Kind == "ip-addresses").Fields.Contains("Operator notes"), "Publication discards existing operator notes");
            stub.Objects["ip-addresses"][0]["comments"] += "\nDDI:another-installation:ip-addresses:other";
            Check((await connector.PreviewAsync(ipam.Snapshot())).Changes.Single(c => c.Kind == "ip-addresses").Operation == "Conflict", "Multiple ownership markers accepted");
            stub.Objects["ip-addresses"][0]["comments"] = addressMarker;
            using (var restored = new NetBoxConnector(Path.Combine(folder, "restored"), new HttpClient(stub)))
            {
                restored.RestoreConfiguration(connector.ExportConfiguration());
                Check((await restored.PreviewAsync(ipam.Snapshot())).Changes.All(c => c.Operation == "Unchanged"), "Backup loses managed-object installation provenance");
            }
            subnet = ipam.PutSubnet(2, "test", subnet.Id, "default", subnet.Prefix, "Renamed Lab", "East", 20, "", "");
            preview = await connector.PreviewAsync(ipam.Snapshot());
            Check(preview.Changes.Single(c => c.Kind == "prefixes").Operation == "Update", "Owned object update absent");
            Check((await connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision)).Applied == 1, "ETag guarded update failed");
            subnet = ipam.PutSubnet(3, "test", subnet.Id, "default", subnet.Prefix, "Third Lab", "East", 20, "", "");
            preview = await connector.PreviewAsync(ipam.Snapshot()); stub.Objects["prefixes"][0]["last_updated"] = "changed-by-other-user";
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision), "Concurrent remote change overwritten");
            int prior = stub.Writes;
            preview = await connector.PreviewAsync(ipam.Snapshot());
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision, readRevision: () => 999), "Local intent change during publication ignored");
            Check(stub.Writes == prior, "Changed intent made external writes");
            stub.Objects["prefixes"][0]["comments"] = "Owned by someone else";
            preview = await connector.PreviewAsync(ipam.Snapshot());
            Check(preview.Changes.Any(c => c.Operation == "Conflict"), "Unowned remote object silently adopted");
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision), "Conflicting preview applied");
            stub.ForeignPagination = true;
            await Reject(() => connector.PreviewAsync(ipam.Snapshot()), "Authentication follows cross-origin pagination"); stub.ForeignPagination = false;
            await Reject(() => connector.ConfigureAsync(settings with { Url = "http://netbox.test/" }), "Plaintext credential endpoint accepted");
            await Reject(() => connector.ConfigureAsync(settings with { ApiKeyEnvironment = "AWS_SECRET_ACCESS_KEY" }), "Unrelated server environment secret exposed");
            await Reject(() => connector.ConfigureAsync(settings with { Mappings = [new("default", null), new("other", null)] }), "Two network views can collide in one NetBox VRF");
            stub.Objects["prefixes"].Clear(); stub.Objects["ip-addresses"].Clear(); stub.UnknownCreateOutcome = true;
            preview = await connector.PreviewAsync(ipam.Snapshot());
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision), "Unknown remote outcome not surfaced");
            await Reject(() => connector.ApplyAsync(preview.Id, ipam.Snapshot().Revision), "Unknown create is blindly retried");
            Check((await connector.PreviewAsync(ipam.Snapshot())).Changes.Single(c => c.Kind == "prefixes").Operation == "Unchanged", "Remote success after lost response cannot be reconciled");
            stub.Scheme = "Token"; await connector.ConfigureAsync(settings with { TokenScheme = "Token" });
            Check((await connector.PreviewAsync(ipam.Snapshot())).Changes.Length == 2, "Legacy Token authentication unsupported");
            Console.WriteLine($"PASS: {checks} NetBox publication, revision, provenance, ETag, authentication and uncertain-outcome checks.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DDI_NETBOX_TEST_API_KEY", null);
            if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected test directory"); Directory.Delete(folder, true);
        }
    }
}
