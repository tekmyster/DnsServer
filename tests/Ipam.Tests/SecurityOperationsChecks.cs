using DnsServerCore.Security;
using System.Text.Json;

static class SecurityOperationsChecks
{
    public static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-security-ops-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        try
        {
            var suffixes = new PublicSuffixes(["example"]);
            await using (var manager = new DdiSecurityManager(folder, suffixes))
            {
                manager.SetPolicy(SecurityPolicy.Default with { Enabled = true }, 0);
                manager.SetPolicy(manager.Policy with { Mode = "Block" }, 1);
                Check(manager.PolicyHistory().Select(p => p.Revision).SequenceEqual(new long[] { 0, 1, 2 }), "Policy history does not retain prior content");
                manager.SetPolicy(manager.PolicyHistory().Single(p => p.Revision == 1), 2);
                Check(manager.Policy.Revision == 3 && manager.Policy.Mode == "Monitor", "Rollback content does not become a new revision");
                var indicators = Enumerable.Range(0, 80).Select(i => new ThreatIndicator("rule-" + i, "Domain", "many.example", i == 79 ? "Block" : "Monitor", "Test", 80)).ToArray();
                manager.SetPolicy(manager.Policy with { Mode = "Block", Indicators = indicators }, 3);
                var decision = manager.Evaluate(System.Net.IPAddress.Loopback, "many.example", [], []);
                Check(decision.Action == "Block" && decision.Matches.Length == 32 && decision.Matches.Any(i => i.Action == "Block") && decision.AdditionalMatches == 48, "Evidence bounds hide an enforcement rule or discard match counts");
                manager.SetPolicy(manager.Policy with { Indicators = manager.Policy.Indicators.Concat(new[] { new ThreatIndicator("allow", "Domain", "exception.example", "Allow", "Test"), new ThreatIndicator("deny", "Domain", "exception.example", "Block", "Test") }).ToArray() }, 4);
                manager.DeviceResolver = _ => new SecurityDevice("Lab", "allow-client", "02-02", "client", "default", "Lab", null);
                var question = new TechnitiumLibrary.Net.Dns.DnsQuestionRecord("exception.example", TechnitiumLibrary.Net.Dns.ResourceRecords.DnsResourceRecordType.A, TechnitiumLibrary.Net.Dns.ResourceRecords.DnsClass.IN);
                var request = new TechnitiumLibrary.Net.Dns.DnsDatagram(12, false, TechnitiumLibrary.Net.Dns.DnsOpcode.StandardQuery, false, false, true, false, false, false, TechnitiumLibrary.Net.Dns.DnsResponseCode.NoError, [question]);
                var response = new TechnitiumLibrary.Net.Dns.DnsDatagram(12, true, TechnitiumLibrary.Net.Dns.DnsOpcode.StandardQuery, false, false, true, true, false, false, TechnitiumLibrary.Net.Dns.DnsResponseCode.NoError, [question]);
                Check(manager.InspectResponse(request, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234), response).RCODE == TechnitiumLibrary.Net.Dns.DnsResponseCode.NoError && !manager.Signals().Any(s => s.ClientIdentifier == "allow-client"), "Explicitly allowed query creates DHCP/device threat risk from a suppressed blocking rule");
                manager.ObserveDhcpConflict("Lab", "1-0201", "02-01", "client.example", "192.0.2.4");
                var item = manager.RecentEvents().Last();
                Check(manager.Signals().Any(s => s.Service == "DHCP" && s.Score == 80), "DHCP conflict observation is not shared security context");
                Check(manager.SetDisposition(item.Id, "Investigating", "Check endpoint ownership", "operator").Actor == "operator", "Analyst attribution missing");
                manager.SetDisposition(item.Id, "Resolved", "Confirmed lab test", "operator");
                Check(manager.Dispositions().Length == 1 && manager.Dispositions()[0].Status == "Resolved", "Disposition update creates duplicate cases");
                manager.Start();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!Directory.GetFiles(folder, "security-events-*.jsonl").Any() && DateTime.UtcNow < deadline) await Task.Delay(50);
            }
            await using var restored = new DdiSecurityManager(folder, suffixes);
            var found = await restored.SearchEventsAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1), "1-0201");
            Check(found.Events.Length == 1 && found.Scanned >= 1 && found.Events[0].QueryType == "Decline", "Persisted event timeline search failed after restart");
            Check(restored.Dispositions().Single().Status == "Resolved" && restored.PolicyHistory().Any(p => p.Revision == 0), "Restart loses investigation or rollback history");
            string journal = Directory.GetFiles(folder, "security-events-*.jsonl").Single(); await File.AppendAllTextAsync(journal, "not-json\n");
            Check((await restored.SearchEventsAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1))).CorruptLines == 1, "Corrupt journal coverage is hidden");
            bool rejected = false;
            try { await restored.SearchEventsAsync(DateTime.UtcNow.AddDays(-20), DateTime.UtcNow); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unbounded investigation search accepted");
            Console.WriteLine($"PASS: {checks} policy rollback, bounded evidence, persisted investigation and analyst disposition checks.");
        }
        finally
        { if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected test directory"); Directory.Delete(folder, true); }
    }
}
