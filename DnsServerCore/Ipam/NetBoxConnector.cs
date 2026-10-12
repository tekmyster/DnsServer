using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DnsServerCore.Ipam
{
    public sealed record NetBoxMapping(string ViewId, int? VrfId);
    public sealed record NetBoxSettings(bool Enabled, string Url, string ApiKeyEnvironment, string TokenScheme, NetBoxMapping[] Mappings);
    public sealed record NetBoxChange(string Kind, string LocalId, string Operation, int? RemoteId, string RemoteUpdated, string Fields, string Reason, string Before = null);
    public sealed record NetBoxPreview(string Id, long InventoryRevision, DateTime Created, string SettingsHash, NetBoxChange[] Changes);
    public sealed record NetBoxResult(string PreviewId, int Applied, string Status);

    // DDI owns mapped prefix/address fields. Existing unowned NetBox objects are conflicts,
    // never silently adopted or deleted. External writes use reviewed, revision-bound previews.
    public sealed class NetBoxConnector : IDisposable
    {
        public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        readonly string _folder;
        string _installation;
        readonly HttpClient _http;
        readonly SemaphoreSlim _gate = new(1, 1);
        NetBoxSettings _settings;
        public NetBoxConnector(string folder, HttpClient client = null)
        {
            _folder = folder; Directory.CreateDirectory(folder);
            string installationFile = Path.Combine(folder, "netbox-installation.txt");
            if (!File.Exists(installationFile)) File.WriteAllText(installationFile, Guid.NewGuid().ToString("N"));
            _installation = File.ReadAllText(installationFile).Trim();
            if (!Guid.TryParseExact(_installation, "N", out _)) throw new InvalidDataException("Invalid NetBox installation identity.");
            _settings = File.Exists(Path.Combine(folder, "netbox.json")) ? JsonSerializer.Deserialize<NetBoxSettings>(File.ReadAllBytes(Path.Combine(folder, "netbox.json")), JsonOptions) : new(false, "", "DDI_NETBOX_API_KEY", "Bearer", []);
            Validate(_settings);
            _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        }
        public NetBoxSettings Settings { get { var settings = _settings; return settings with { Mappings = settings.Mappings.ToArray() }; } }
        public byte[] ExportConfiguration() => JsonSerializer.SerializeToUtf8Bytes(new NetBoxConfiguration(_installation, Settings), JsonOptions);
        public sealed record NetBoxConfiguration(string Installation, NetBoxSettings Settings);
        public void RestoreConfiguration(byte[] bytes)
        {
            var bundle = JsonSerializer.Deserialize<NetBoxConfiguration>(bytes, JsonOptions) ?? throw new ArgumentException("Invalid NetBox backup.");
            if (!Guid.TryParseExact(bundle.Installation, "N", out _)) throw new ArgumentException("Invalid NetBox identity."); Validate(bundle.Settings);
            _gate.Wait();
            try
            {
                Save("netbox.json", bundle.Settings);
                string identity = Path.Combine(_folder, "netbox-installation.txt");
                using (var stream = new FileStream(identity + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { byte[] data = Encoding.UTF8.GetBytes(bundle.Installation); stream.Write(data); stream.Flush(true); } File.Move(identity + ".tmp", identity, true);
                _installation = bundle.Installation; _settings = bundle.Settings with { Mappings = bundle.Settings.Mappings.ToArray() };
            }
            finally { _gate.Release(); }
        }
        static void Validate(NetBoxSettings settings)
        {
            if (settings is null || settings.Mappings is null || settings.Mappings.Length > 1000 || settings.Mappings.Any(m => string.IsNullOrWhiteSpace(m.ViewId) || m.ViewId.Length > 255 || m.VrfId <= 0) || settings.Mappings.Select(m => m.ViewId).Distinct().Count() != settings.Mappings.Length || settings.Mappings.Select(m => m.VrfId).Distinct().Count() != settings.Mappings.Length || settings.TokenScheme != "Bearer" && settings.TokenScheme != "Token" || settings.ApiKeyEnvironment is null || !settings.ApiKeyEnvironment.StartsWith("DDI_NETBOX_", StringComparison.Ordinal) || !settings.ApiKeyEnvironment.EndsWith("_API_KEY", StringComparison.Ordinal) || settings.ApiKeyEnvironment.Length > 128 || settings.ApiKeyEnvironment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Invalid NetBox mappings, authentication scheme, or credential reference.");
            if (settings.Enabled && (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)) throw new ArgumentException("NetBox requires an HTTPS base URL.");
        }
        void Save(string name, object value)
        {
            string path = Path.Combine(_folder, name); using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value, value.GetType(), JsonOptions); stream.Flush(true); } File.Move(path + ".tmp", path, true);
        }
        public async Task ConfigureAsync(NetBoxSettings settings, CancellationToken cancellationToken = default)
        {
            Validate(settings); await _gate.WaitAsync(cancellationToken);
            try { settings = settings with { Mappings = settings.Mappings.ToArray() }; Save("netbox.json", settings); _settings = settings; }
            finally { _gate.Release(); }
        }
        string SettingsHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_settings, JsonOptions)));
        Uri Endpoint(string path) => new(new Uri(_settings.Url.TrimEnd('/') + "/"), path);
        string Marker(string kind, string id) => "DDI:" + _installation + ":" + kind + ":" + id;
        async Task<(JsonElement Json, string ETag)> Request(HttpMethod method, Uri uri, string fields, string etag, CancellationToken cancellationToken)
        {
            if (!_settings.Enabled) throw new InvalidOperationException("NetBox integration is disabled.");
            Uri root = Endpoint("api/");
            if (uri.Scheme != root.Scheme || uri.Authority != root.Authority || !uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)) throw new ArgumentException("NetBox pagination left the configured API origin.");
            string key = Environment.GetEnvironmentVariable(_settings.ApiKeyEnvironment);
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("NetBox API key environment variable is missing.");
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue(_settings.TokenScheme, key);
            if (fields is not null) request.Content = new StringContent(fields, Encoding.UTF8, "application/json");
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400) throw new HttpRequestException("NetBox redirect refused.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("NetBox returned HTTP " + (int)response.StatusCode); // no token or potentially sensitive response body
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken); using MemoryStream bytes = new(); byte[] buffer = new byte[8192]; int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) { if (bytes.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("NetBox response exceeds 8 MiB."); bytes.Write(buffer, 0, read); }
            using var json = JsonDocument.Parse(bytes.ToArray()); return (json.RootElement.Clone(), response.Headers.ETag?.ToString());
        }
        async Task<JsonElement[]> List(string kind, CancellationToken cancellationToken)
        {
            List<JsonElement> results = []; Uri next = Endpoint("api/ipam/" + kind + "/?limit=200"); HashSet<string> visited = [];
            while (next is not null)
            {
                if (!visited.Add(next.AbsoluteUri) || visited.Count > 100) throw new InvalidDataException("NetBox pagination exceeded its bounds.");
                var page = (await Request(HttpMethod.Get, next, null, null, cancellationToken)).Json;
                results.AddRange(page.GetProperty("results").EnumerateArray()); if (results.Count > 10000) throw new InvalidDataException("NetBox snapshot exceeds 10000 objects.");
                var value = page.GetProperty("next"); next = value.ValueKind == JsonValueKind.Null ? null : new Uri(next, value.GetString());
            }
            return results.ToArray();
        }
        static int? Vrf(JsonElement obj) => obj.GetProperty("vrf").ValueKind == JsonValueKind.Null ? null : obj.GetProperty("vrf").GetProperty("id").GetInt32();
        static string Text(JsonElement obj, string field) => obj.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
        static bool MatchesFields(JsonElement remote, JsonElement fields)
        {
            foreach (var property in fields.EnumerateObject())
            {
                if (!remote.TryGetProperty(property.Name, out var current)) return false;
                if (property.Name == "vrf") { if (Vrf(remote) != (property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetInt32())) return false; }
                else if (property.Name == "status" && current.ValueKind == JsonValueKind.Object) { if (current.GetProperty("value").GetString() != property.Value.GetString()) return false; }
                else if (current.ToString() != property.Value.ToString()) return false;
            }
            return true;
        }
        public async Task<NetBoxPreview> PreviewAsync(IpamSnapshot inventory, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var prefixes = await List("prefixes", cancellationToken); var addresses = await List("ip-addresses", cancellationToken); List<NetBoxChange> changes = [];
                void Compare(string kind, string id, string natural, int? vrf, object data, JsonElement[] objects, string naturalField)
                {
                    string fields = JsonSerializer.Serialize(data, JsonOptions); var matches = objects.Where(o => (kind == "ip-addresses" ? IPAddress.Parse(Text(o, naturalField).Split('/')[0]).Equals(IPAddress.Parse(natural.Split('/')[0])) : Text(o, naturalField) == natural) && Vrf(o) == vrf).ToArray();
                    if (matches.Length > 1) { changes.Add(new(kind, id, "Conflict", null, "", fields, "Multiple NetBox objects have the same address/prefix and VRF")); return; }
                    if (matches.Length == 0) { changes.Add(new(kind, id, "Create", null, "", fields, "No matching object")); return; }
                    var remote = matches[0]; string marker = Marker(kind, id);
                    if (Text(remote, "comments") != marker) { changes.Add(new(kind, id, "Conflict", remote.GetProperty("id").GetInt32(), Text(remote, "last_updated"), fields, "Existing object is not owned by this DDI installation")); return; }
                    using var parsed = JsonDocument.Parse(fields);
                    var before = parsed.RootElement.EnumerateObject().Where(p => remote.TryGetProperty(p.Name, out _)).ToDictionary(p => p.Name, p => remote.GetProperty(p.Name).Clone());
                    changes.Add(new(kind, id, MatchesFields(remote, parsed.RootElement) ? "Unchanged" : "Update", remote.GetProperty("id").GetInt32(), Text(remote, "last_updated"), fields, "DDI owns these mapped fields", JsonSerializer.Serialize(before, JsonOptions)));
                }
                foreach (var subnet in inventory.Subnets)
                {
                    var mapping = _settings.Mappings.SingleOrDefault(m => m.ViewId == subnet.ViewId); if (mapping is null) continue;
                    Compare("prefixes", subnet.Id, subnet.Prefix, mapping.VrfId, new { prefix = subnet.Prefix, vrf = mapping.VrfId, status = subnet.Container ? "container" : "active", description = subnet.Name, comments = Marker("prefixes", subnet.Id) }, prefixes, "prefix");
                }
                foreach (var assignment in inventory.Assignments)
                {
                    var subnet = inventory.Subnets.Single(s => s.Id == assignment.SubnetId); var mapping = _settings.Mappings.SingleOrDefault(m => m.ViewId == subnet.ViewId); if (mapping is null) continue;
                    string address = assignment.Address + "/" + new IpamNetwork(subnet.Prefix).PrefixLength;
                    Compare("ip-addresses", assignment.Id, address, mapping.VrfId, new { address, vrf = mapping.VrfId, status = assignment.Status switch { "Reserved" => "reserved", "Deprecated" => "deprecated", _ => "active" }, dns_name = assignment.HostName ?? "", description = assignment.Description ?? "", comments = Marker("ip-addresses", assignment.Id) }, addresses, "address");
                }
                var preview = new NetBoxPreview(Guid.NewGuid().ToString("N"), inventory.Revision, DateTime.UtcNow, SettingsHash(), changes.ToArray()); Save("netbox-preview.json", preview); return preview;
            }
            finally { _gate.Release(); }
        }
        public async Task<NetBoxResult> ApplyAsync(string previewId, long inventoryRevision, CancellationToken cancellationToken = default, Func<long> readRevision = null)
        {
            await _gate.WaitAsync(cancellationToken); int applied = 0;
            try
            {
                string resultPath = Path.Combine(_folder, "netbox-result.json");
                if (File.Exists(resultPath) && JsonSerializer.Deserialize<NetBoxResult>(File.ReadAllBytes(resultPath), JsonOptions)?.PreviewId == previewId) throw new InvalidOperationException("This preview was already submitted; generate a fresh preview to reconcile outcomes.");
                var preview = JsonSerializer.Deserialize<NetBoxPreview>(File.ReadAllBytes(Path.Combine(_folder, "netbox-preview.json")), JsonOptions);
                if (preview.Id != previewId || preview.InventoryRevision != inventoryRevision || preview.Created < DateTime.UtcNow.AddMinutes(-15) || preview.SettingsHash != SettingsHash() || preview.Changes.Any(c => c.Operation == "Conflict")) throw new InvalidOperationException("Preview is stale, conflicts, or does not match current intent/settings.");
                Save("netbox-result.json", new NetBoxResult(previewId, 0, "Applying; interrupted outcomes require a new preview"));
                foreach (var change in preview.Changes.Where(c => c.Operation != "Unchanged"))
                {
                    if (readRevision is not null && readRevision() != inventoryRevision) throw new InvalidOperationException("DDI intent changed while applying; reconcile remaining objects with a new preview.");
                    string etag = null; var endpoint = Endpoint("api/ipam/" + change.Kind + "/");
                    if (change.Operation == "Update")
                    {
                        endpoint = new Uri(endpoint, change.RemoteId + "/"); var current = await Request(HttpMethod.Get, endpoint, null, null, cancellationToken);
                        if (Text(current.Json, "last_updated") != change.RemoteUpdated || Text(current.Json, "comments") != Marker(change.Kind, change.LocalId)) throw new InvalidOperationException("NetBox object changed since preview.");
                        etag = current.ETag ?? throw new InvalidOperationException("NetBox must support ETag conditional writes before updates are allowed.");
                    }
                    else
                    {
                        // Recheck natural identity immediately before create; uncertain HTTP outcomes are never blindly retried.
                        using var fields = JsonDocument.Parse(change.Fields); string naturalField = change.Kind == "prefixes" ? "prefix" : "address";
                        var current = await List(change.Kind, cancellationToken); var vrfValue = fields.RootElement.GetProperty("vrf"); int? vrf = vrfValue.ValueKind == JsonValueKind.Null ? null : vrfValue.GetInt32();
                        string natural = fields.RootElement.GetProperty(naturalField).GetString();
                        if (current.Any(o => (change.Kind == "ip-addresses" ? IPAddress.Parse(Text(o, naturalField).Split('/')[0]).Equals(IPAddress.Parse(natural.Split('/')[0])) : Text(o, naturalField) == natural) && Vrf(o) == vrf)) throw new InvalidOperationException("NetBox identity appeared since preview.");
                    }
                    await Request(change.Operation == "Create" ? HttpMethod.Post : HttpMethod.Patch, endpoint, change.Fields, etag, cancellationToken); applied++;
                    Save("netbox-result.json", new NetBoxResult(previewId, applied, "Applying"));
                }
                var result = new NetBoxResult(previewId, applied, "Completed"); Save("netbox-result.json", result); return result;
            }
            catch
            { if (applied > 0) Save("netbox-result.json", new NetBoxResult(previewId, applied, "Partial; reconcile with a new preview before continuing")); throw; }
            finally { _gate.Release(); }
        }
        public void Dispose() { _http.Dispose(); _gate.Dispose(); }
    }
}
