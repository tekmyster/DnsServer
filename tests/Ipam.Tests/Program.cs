using DnsServerCore.Ipam;
using System.Collections.Concurrent;
using System.Net;
using System.Numerics;

if (args.Contains("--api") || args.Contains("--serve")) { await ApiChecks.Run(args.Contains("--serve")); return; }
if (args.Contains("--security")) { await SecurityChecks.Run(); return; }

string folder = Path.Combine(Path.GetTempPath(), "ddi-ipam-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { passed++; return; } catch (InvalidOperationException) { passed++; return; } catch (InvalidDataException) { passed++; return; }
    throw new Exception(message);
}
IpamSubnet AddSubnet(IpamManager manager, string view, string prefix, bool container = false) => manager.PutSubnet(manager.Snapshot().Revision, "test", "", view, prefix, prefix, "East", 20, "", "", container);
try
{
    string file = Path.Combine(folder, "ipam.json");
    IpamManager manager = new IpamManager(file);
    var root = AddSubnet(manager, "default", "10.0.0.0/8", true);
    var subnet = AddSubnet(manager, "default", "10.20.0.43/24");
    Check(subnet.Prefix == "10.20.0.0/24", "Canonical prefix");
    Reject(() => AddSubnet(manager, "default", "10.20.0.0/25"), "Overlapping leaf prefix accepted");
    var view = manager.PutView(manager.Snapshot().Revision, "test", "", "West", "Independent routing domain");
    var overlapping = AddSubnet(manager, view.Id, "10.20.0.0/24");
    var first = manager.PutAssignment(manager.Snapshot().Revision, "test", "", subnet.Id, "10.20.0.1", "device.example", "NetOps", "Assigned", "");
    manager.PutAssignment(manager.Snapshot().Revision, "test", "", overlapping.Id, "10.20.0.1", "other.example", "NetOps", "Assigned", "");
    Reject(() => manager.PutAssignment(manager.Snapshot().Revision, "test", "", subnet.Id, "10.20.0.1", "", "", "Assigned", ""), "Duplicate address accepted");
    Reject(() => manager.PutAssignment(manager.Snapshot().Revision, "test", "", subnet.Id, "10.20.0.0", "", "", "Assigned", ""), "Network address accepted");
    Reject(() => manager.PutAssignment(manager.Snapshot().Revision, "test", "", subnet.Id, "10.20.0.255", "", "", "Assigned", ""), "Broadcast accepted");
    Reject(() => manager.PutAssignment(manager.Snapshot().Revision, "test", "", subnet.Id, "10.21.0.1", "", "", "Assigned", ""), "Out of subnet address accepted");
    Check(manager.NextAvailable(subnet.Id, ["10.20.0.2", "10.20.0.3", "192.0.2.1"]) == "10.20.0.4", "Observed usage not excluded");
    Reject(() => manager.NextAvailable(root.Id, []), "Container allocation accepted");
    var tiny = AddSubnet(manager, "default", "192.0.2.0/31");
    Check(manager.NextAvailable(tiny.Id, []) == "192.0.2.0", "/31 first endpoint unavailable");
    Check(manager.NextAvailable(tiny.Id, ["192.0.2.0"]) == "192.0.2.1", "/31 second endpoint unavailable");
    Reject(() => manager.NextAvailable(tiny.Id, ["192.0.2.0", "192.0.2.1"]), "Exhausted pool returned address");
    var ipv6 = AddSubnet(manager, "default", "2001:db8::/64");
    Check(new IpamNetwork(ipv6.Prefix).Capacity == (BigInteger.One << 64) - 1, "IPv6 capacity overflow");
    Check(manager.NextAvailable(ipv6.Id, ["2001:db8::1"]) == "2001:db8::2", "IPv6 next available failed");
    Reject(() => new IpamNetwork("fe80::1%2/64"), "Scoped address accepted");
    Reject(() => manager.Delete(manager.Snapshot().Revision, "test", "view", "default"), "Default view deleted");
    Reject(() => manager.Delete(manager.Snapshot().Revision, "test", "subnet", root.Id), "Nonempty container deleted");
    Reject(() => manager.Delete(manager.Snapshot().Revision, "test", "subnet", subnet.Id), "Nonempty subnet deleted");
    Reject(() => manager.PutView(0, "test", "", "Stale", ""), "Stale revision accepted");
    var reloaded = new IpamManager(file);
    Check(reloaded.Snapshot().Revision == manager.Snapshot().Revision && reloaded.Snapshot().Assignments.Length == 2, "Durable reload failed");
    Check(manager.Snapshot().Changes.Any(c => c.After?.Contains("device.example") == true), "Assignment history lacks values");
    var findings = manager.Reconcile(subnet.Id, [new IpamObservation("10.20.0.1", "DhcpLease", "scope", "wrong.example", "client1", DateTime.UtcNow.AddHours(1)), new IpamObservation("10.20.0.1", "DhcpLease", "scope", "wrong.example", "client2", DateTime.UtcNow.AddHours(1)), new IpamObservation("10.20.0.2", "DnsRecord", "example", "new.example", "", null)]);
    Check(findings.Any(f => f.Kind == "DuplicateLease") && findings.Any(f => f.Kind == "HostNameDrift") && findings.Any(f => f.Kind == "Unmanaged"), "Reconciliation findings incomplete");
    long revision = manager.Snapshot().Revision;
    ConcurrentBag<string> results = [];
    Parallel.For(0, 8, i => { try { manager.PutAssignment(revision, "test", "", subnet.Id, "10.20.0.10", "", "", "Reserved", ""); results.Add("ok"); } catch (InvalidOperationException) { results.Add("stale"); } });
    Check(results.Count(r => r == "ok") == 1 && results.Count(r => r == "stale") == 7, "Concurrent updates not serialized");
    byte[] snapshot = manager.Export();
    manager.Restore(snapshot);
    Check(manager.Snapshot().Revision == revision + 1, "Backup restore changed revision");
    Reject(() => manager.Restore(System.Text.Encoding.UTF8.GetBytes("{\"version\":99,\"revision\":0,\"views\":[],\"subnets\":[],\"assignments\":[],\"changes\":[]}")), "Unknown schema accepted");
    Check(manager.Snapshot().Revision == revision + 1, "Failed restore corrupted state");
    manager.Delete(manager.Snapshot().Revision, "test", "subnet", tiny.Id);
    Check(!manager.Snapshot().Subnets.Any(s => s.Id == tiny.Id), "Subnet deletion failed");
    string failingPath = Path.Combine(folder, "not-a-directory");
    File.WriteAllText(failingPath, "file");
    IpamManager failing = new IpamManager(Path.Combine(failingPath, "ipam.json"));
    try { failing.PutView(0, "test", "", "NeverSaved", ""); throw new Exception("Save unexpectedly succeeded"); } catch (IOException) { passed++; }
    Check(failing.Snapshot().Revision == 0 && failing.Snapshot().Views.Length == 1, "Failed save changed in-memory state");
    Console.WriteLine($"PASS: {passed} IPAM checks");
}
finally
{
    // The target is the unique directory created by this test under the OS temp directory.
    if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected test directory");
    Directory.Delete(folder, true);
}
