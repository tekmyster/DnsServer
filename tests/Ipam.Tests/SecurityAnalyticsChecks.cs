using DnsServerCore.Security;
using System.Net;
using System.Text.Json;

static class SecurityAnalyticsChecks
{
    sealed class ModelStub : HttpMessageHandler
    {
        public string Body;
        public string Authorization;
        public string Response = "{\"choices\":[{\"message\":{\"content\":\"Review the observed evidence; legitimate software is also possible.\"}}]}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return new(Status) { Content = new StringContent(Response) };
        }
    }
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
        void Reject(Action action, string reason) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(reason); }
        async Task Fail(Func<Task> action, string reason) { try { await action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(reason); }
        var suffixes = new PublicSuffixes(["example", "com", "github.io"]);
        var analyzer = new BehavioralAnalyzer(suffixes);
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        BehavioralScore score = null;
        for (int i = 0; i < 50; i++) score = analyzer.Observe("slow-client", $"abcdefghijklmnopqrstuvwxyz0123456789{i:D4}.leak.example", "TXT", true, now.AddMinutes(i));
        Check(score.Score >= 70 && score.WindowMinutes == 60 && score.Findings.Any(f => f.Kind == "DnsTunneling" && f.WindowMinutes == 60), "Slow, sustained payload pattern was missed");
        Check(!score.Findings.Any(f => f.Kind == "DnsTunneling" && f.WindowMinutes == 5 && f.Score >= 70), "Short window incorrectly includes expired samples");
        Check(score.ModelVersion == "heuristic-v2" && score.BaselineStatus == "NotLearned", "Model or baseline provenance absent");
        Check(analyzer.Observe("slow-client", "ordinary.leak.example", "A", false, now.AddMinutes(110)).Score == 0, "Hour-old observations survived expiry");
        for (int i = 0; i < 120; i++) score = analyzer.Observe("txt-client", "verification.service.example", "TXT", true, now);
        Check(score.Score < 70, "TXT and NXDOMAIN alone establish an alert");
        for (int i = 0; i < 120; i++) score = analyzer.Observe("repeat-client", "abcdefghijklmnopqrstuvwxyz0123456789.leak.example", "TXT", true, now);
        Check(score.Score <= 49, "Repeated opaque labels escalate without changing payload evidence");
        for (int i = 0; i < 40; i++) score = analyzer.Observe("dga-client", $"abcdefghijklmnop012345{i:D4}.example", "A", true, now.AddSeconds(i));
        Check(score.Findings.Any(f => f.Kind == "DgaPattern" && f.Scope == "Client" && f.Score == 80), "Cross-domain DGA pattern is absent");
        for (int i = 0; i < 40; i++) score = analyzer.Observe("working-domains", $"abcdefghijklmnop012345{i:D4}.example", "A", false, now.AddSeconds(i));
        Check(!score.Findings.Any(f => f.Kind == "DgaPattern"), "Successful generated-looking domains were classified as DGA");
        for (int i = 0; i < 30; i++) score = analyzer.Observe("periodic-client", "heartbeat.service.example", "A", false, now.AddSeconds(30 * i));
        Check(score.Findings.Any(f => f.Kind == "PeriodicDns" && f.Score == 35) && score.Score < 70, "Periodic activity lacks low-priority evidence or falsely escalates");
        Check(analyzer.Observe("replacement-device", "heartbeat.service.example", "A", false, now).Score == 0, "New device identity inherited old activity");
        for (int i = 0; i < 600; i++) score = analyzer.Observe("burst-client", $"abcdefghijklmnopqrstuvwxyz0123456789{i:D4}.leak.example", "TXT", true, now);
        Check(score.SampleLimited && score.Samples == 512 && score.Score >= 70, "Bounded capture silently lost coverage or burst detection");
        Check(analyzer.Observe("burst-client", "ordinary.leak.example", "A", false, now.AddSeconds(-1)).Score == 0, "Clock rollback retains future observations");
        var legacy = JsonSerializer.Deserialize<BehavioralScore>("{\"score\":80,\"domain\":\"example\",\"samples\":30,\"windowStart\":\"2026-10-13T12:00:00Z\",\"contributions\":[]}", DdiSecurityManager.JsonOptions);
        Check(legacy.ModelVersion == "heuristic-v1" && legacy.Findings is null, "Legacy evidence compatibility was broken");

        var settings = new LlmSettings(true, "http://127.0.0.1:11434/v1/chat/completions", "local-model", "DDI_LLM_TEST_API_KEY");
        Reject(() => SecurityLlm.Validate(settings with { Endpoint = "http://192.0.2.1/v1/chat/completions", AllowRemote = true }), "Plaintext network endpoint accepted");
        Reject(() => SecurityLlm.Validate(settings with { Endpoint = "https://model.example/v1/chat/completions" }), "Network evidence sharing lacks explicit permission");
        Reject(() => SecurityLlm.Validate(settings with { ApiKeyEnvironment = "AWS_SECRET_ACCESS_KEY" }), "Unrelated credential reference accepted");
        Reject(() => SecurityLlm.Validate(settings with { Endpoint = "https://user:password@model.example/v1/chat/completions", AllowRemote = true }), "Endpoint credentials accepted");
        Reject(() => SecurityLlm.Validate(settings with { Endpoint = "http://localhost:11434/v1/chat/completions" }), "Non-literal insecure loopback accepted");
        Check(SecurityLlm.Validate(settings with { Endpoint = "https://model.example/v1/chat/completions", AllowRemote = true }).AllowRemote, "Explicit HTTPS network endpoint rejected");
        var stub = new ModelStub();
        using var client = new HttpClient(stub);
        using var connector = new SecurityLlm(client);
        var evidence = new SecurityEvent("event-1", now, "private-node", "DNS", "192.0.2.5", "encoded.secret.example", "TXT", "NxDomain", "Monitor", 1,
            new("Scope", "secret-client", "02-00-00-00-00-01", "private-host", "View", "Private Site", 42), [],
            new(80, "secret.example", 40, now, [new("HighEntropy", 20, "untrusted evidence should not be forwarded verbatim")]), "arbitrary untrusted reason");
        Environment.SetEnvironmentVariable("DDI_LLM_TEST_API_KEY", "fixture-key");
        try
        {
            var result = await connector.AnalyzeAsync(settings, evidence);
            Check(result.Status == "Advisory" && result.EventId == evidence.Id && !result.DomainsShared, "Analysis lacks advisory provenance");
            Check(stub.Authorization == "Bearer fixture-key", "Model credential header missing");
            Check(!stub.Body.Contains("192.0.2.5") && !stub.Body.Contains("02-00") && !stub.Body.Contains("private-host") && !stub.Body.Contains("secret-client") && !stub.Body.Contains("Private Site"), "Identity leaked into model evidence");
            Check(!stub.Body.Contains("secret.example") && !stub.Body.Contains("arbitrary untrusted reason") && !stub.Body.Contains("untrusted evidence should"), "Opt-out or field selection leaked raw event data");
            Check(stub.Body.Contains("max_tokens") && stub.Body.Contains("HighEntropy") && !stub.Body.Contains("fixture-key"), "Bounded request omits useful features or embeds credentials");
            await connector.AnalyzeAsync(settings with { ShareDomains = true }, evidence);
            Check(stub.Body.Contains("secret.example") && !stub.Body.Contains("encoded.secret.example"), "Domain opt-in forwards query payloads");
            await Fail(() => connector.AnalyzeAsync(settings with { Enabled = false }, evidence), "Disabled connector made a request");
            stub.Status = HttpStatusCode.ServiceUnavailable;
            await Fail(() => connector.AnalyzeAsync(settings, evidence), "Provider outage reported as success");
            stub.Status = HttpStatusCode.OK; stub.Response = "{\"choices\":[]}";
            await Fail(() => connector.AnalyzeAsync(settings, evidence), "Empty model choices reported as success");
            stub.Response = new string('x', 65537);
            await Fail(() => connector.AnalyzeAsync(settings, evidence), "Oversized response accepted");
            Environment.SetEnvironmentVariable("DDI_LLM_TEST_API_KEY", "invalid\ncredential");
            await Fail(() => connector.AnalyzeAsync(settings, evidence), "Invalid header credential accepted");
            Environment.SetEnvironmentVariable("DDI_LLM_TEST_API_KEY", null);
            await Fail(() => connector.AnalyzeAsync(settings, evidence), "Missing model credential was ignored");
        }
        finally { Environment.SetEnvironmentVariable("DDI_LLM_TEST_API_KEY", null); }
        Console.WriteLine($"PASS: {checks} rolling analytics, corroboration, bounded evidence and optional model connector checks.");
    }
}
