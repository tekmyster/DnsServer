using System;

namespace DnsServerCore.Security
{
    public sealed record ThreatIndicator(string Id, string Kind, string Value, string Action, string Source, int Confidence = 80, DateTime? Expires = null, string ClientNetwork = null, string Evidence = null);
    public sealed record MispSettings(bool Enabled = false, string Url = "", string ApiKeyEnvironment = "DDI_MISP_API_KEY", int RefreshSeconds = 300, int CacheLifetimeHours = 24);
    public sealed record SecurityPolicy(int Version, long Revision, bool Enabled, string Mode, bool InspectAuthoritative, int AlertThreshold, string DeviceDnsResponse, string DhcpResponse, int DeviceThreshold, ThreatIndicator[] Indicators, MispSettings Misp)
    {
        public static SecurityPolicy Default => new SecurityPolicy(1, 0, false, "Monitor", false, 70, "Monitor", "Observe", 90, [], new MispSettings());
    }
    public sealed record SecurityDevice(string Scope, string ClientIdentifier, string HardwareAddress, string HostName, string ViewId, string Site, int? Vlan);
    public sealed record ScoreContribution(string Feature, int Points, string Evidence);
    public sealed record BehavioralScore(int Score, string Domain, int Samples, DateTime WindowStart, ScoreContribution[] Contributions, string ModelVersion = "heuristic-v1", string Confidence = "Uncalibrated");
    public sealed record SecurityDecision(string Action, ThreatIndicator[] Matches, BehavioralScore Behavior, string Reason);
    public sealed record SecurityEvent(string Id, DateTime Timestamp, string Node, string Service, string ClientAddress, string Query, string QueryType, string ResponseCode, string Action, long PolicyRevision, SecurityDevice Device, ThreatIndicator[] Matches, BehavioralScore Behavior, string Reason);
    public sealed record SecuritySignal(string Id, DateTime Timestamp, DateTime Expires, string Node, string Service, string Scope, string ClientIdentifier, string HardwareAddress, int Score, string Reason, string[] EvidenceKeys = null);
    public sealed record MispHealth(DateTime? LastSuccess, DateTime? LastAttempt, string Status, int Indicators);
    public sealed record MispCache(string SourceUrl, ThreatIndicator[] Indicators);
    public sealed record SignalHealth(DateTime? LastSuccess, string Status, long DeliveryErrors);
}
