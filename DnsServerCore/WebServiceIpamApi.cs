using DnsServerCore.Auth;
using DnsServerCore.Cluster;
using DnsServerCore.Dhcp;
using DnsServerCore.Ipam;
using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Zones;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore
{
    public partial class DnsWebService
    {
        readonly object _ipamDiscoveryLock = new object();
        System.Threading.Timer _ipamDiscoveryTimer;

        void RefreshIpamDiscovery()
        {
            lock (_ipamDiscoveryLock)
            {
                if (_dhcpServer is null || _dnsServer is null || _clusterManager is null) return;
                // Replicas receive coordinator inventory through existing cluster configuration sync.
                if (_clusterManager.ClusterInitialized && _clusterManager.GetSelfNode().Type != ClusterNodeType.Primary) return;
                List<IpamDiscoveredNetwork> networks = [];
                List<IpamDiscoveredAddress> addresses = [];
                HashSet<string> dynamicAddresses = [];
                var scopes = _dhcpServer.Scopes.Values.ToArray();
                foreach (Scope scope in scopes)
                {
                    int length = 0;
                    bool zero = false;
                    foreach (byte value in scope.SubnetMask.GetAddressBytes())
                        for (int bit = 7; bit >= 0; bit--)
                        {
                            bool one = (value & (1 << bit)) != 0;
                            if (one && zero) throw new InvalidOperationException("DHCP scope has a non-contiguous subnet mask.");
                            if (one) length++; else zero = true;
                        }
                    string zone = (scope.DomainName ?? "").TrimEnd('.').ToLowerInvariant();
                    if (_dnsServer.AuthZoneManager.GetAuthZoneInfo(zone) is null) zone = "";
                    networks.Add(new(scope.NetworkAddress + "/" + length, scope.Name, scope.Name, zone, "DhcpScope"));
                    foreach (Lease lease in scope.Leases.Values)
                        if (lease.LeaseExpires > DateTime.UtcNow) dynamicAddresses.Add(lease.Address.ToString());
                    foreach (Lease lease in scope.ReservedLeases)
                    {
                        string host = lease.HostName ?? "";
                        if (host.Length > 0 && !host.Contains('.') && !string.IsNullOrEmpty(scope.DomainName)) host += "." + scope.DomainName.TrimEnd('.');
                        addresses.Add(new(lease.Address.ToString(), host, lease.ClientIdentifier.ToString(), "Reserved", "DhcpReservation"));
                    }
                    if (scope.RouterAddress is not null && !scope.RouterAddress.Equals(IPAddress.Any))
                        addresses.Add(new(scope.RouterAddress.ToString(), "", scope.Name, "Reserved", "DhcpGateway"));
                }
                foreach (var device in scopes.SelectMany(scope => _allocation.LiveLeases(scope.Name))) dynamicAddresses.Add(AllocationSequence.Address(device.Lease.Address).ToString());
                foreach (var zone in _dnsServer.AuthZoneManager.GetAllZones())
                {
                    if (zone.Disabled || (zone.Type != AuthZoneType.Primary && zone.Type != AuthZoneType.Secondary)) continue;
                    string prefix = ReverseZonePrefix(zone.Name);
                    if (prefix is not null) networks.Add(new(prefix, zone.Name, "", zone.Name, "DnsReverseZone"));
                    List<DnsResourceRecord> records = [];
                    _dnsServer.AuthZoneManager.ListAllZoneRecords(zone.Name, [DnsResourceRecordType.A, DnsResourceRecordType.AAAA, DnsResourceRecordType.PTR], records);
                    foreach (var record in records)
                    {
                        if (record.GetAuthGenericRecordInfo().Disabled) continue;
                        IPAddress address = RecordAddress(record);
                        if (address is null || IPAddress.IsLoopback(address) || dynamicAddresses.Contains(address.ToString())) continue;
                        // A lingering DNS record in a dynamic pool must not become static intent after lease expiry.
                        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && scopes.Any(s => IpamNetwork.Number(address) >= IpamNetwork.Number(s.StartingAddress) && IpamNetwork.Number(address) <= IpamNetwork.Number(s.EndingAddress) && !s.IsAddressExcluded(address))) continue;
                        addresses.Add(new(address.ToString(), record.RDATA is DnsPTRRecordData ptr ? ptr.Domain : record.Name, zone.Name, "Assigned", "DnsRecord"));
                    }
                }
                if (_ipam.SynchronizeDiscovery(networks, addresses) && _clusterManager.ClusterInitialized)
                    _clusterManager.TriggerNotifyAllSecondaryNodesIfPrimarySelfNode();
            }
        }

        static string ReverseZonePrefix(string name)
        {
            if (name.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
            {
                var labels = name[..^13].Split('.');
                if (labels.Length < 1 || labels.Length > 4 || labels.Any(l => !byte.TryParse(l, out _))) return null;
                var bytes = labels.Reverse().Select(byte.Parse).Concat(Enumerable.Repeat((byte)0, 4 - labels.Length)).ToArray();
                if (bytes[0] == 127 || bytes[0] == 0) return null;
                return new IpamNetwork(new IPAddress(bytes) + "/" + labels.Length * 8).Prefix;
            }
            if (name.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase))
            {
                var labels = name[..^9].Split('.');
                if (labels.Length < 1 || labels.Length > 32 || labels.Any(l => l.Length != 1 || !Uri.IsHexDigit(l[0]))) return null;
                string hex = string.Concat(labels.Reverse()).PadRight(32, '0');
                return new IpamNetwork(new IPAddress(Convert.FromHexString(hex)) + "/" + labels.Length * 4).Prefix;
            }
            return null;
        }

        static IPAddress RecordAddress(DnsResourceRecord record)
        {
            if (record.RDATA is DnsARecordData a) return a.Address;
            if (record.RDATA is DnsAAAARecordData aaaa) return aaaa.Address;
            if (record.RDATA is DnsPTRRecordData)
            {
                string prefix = ReverseZonePrefix(record.Name);
                if (prefix is not null && (prefix.EndsWith("/32", StringComparison.Ordinal) || prefix.EndsWith("/128", StringComparison.Ordinal)))
                    return IPAddress.Parse(prefix.Split('/')[0]);
            }
            return null;
        }

        void TryRefreshIpamDiscovery()
        {
            try { RefreshIpamDiscovery(); }
            catch (Exception ex) { _log.Write("IPAM automatic discovery failed; existing inventory was retained.", ex); }
        }

        sealed class WebServiceIpamApi
        {
            readonly DnsWebService _service;
            static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            public WebServiceIpamApi(DnsWebService service) { _service = service; }

            User Authorize(HttpContext context, PermissionFlag permission)
            {
                User user = _service.GetSessionUser(context);
                // Reuse existing Settings permissions; no existing ACL is silently expanded.
                if (!_service._authManager.IsPermitted(PermissionSection.Settings, user, permission)) throw new DnsWebServiceException("Access was denied.");
                if (permission != PermissionFlag.View && _service._clusterManager.ClusterInitialized && _service._clusterManager.GetSelfNode().Type != ClusterNodeType.Primary)
                    throw new DnsWebServiceException("IPAM changes must be made on the cluster primary node.");
                return user;
            }

            void Write(HttpContext context, string name, object value)
            {
                Utf8JsonWriter writer = context.GetCurrentJsonWriter();
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, value, value.GetType(), JsonOptions);
            }
            long Revision(HttpContext context) => long.Parse(context.Request.GetQueryOrForm("revision"));
            void Changed(HttpContext context, User user, string operation)
            {
                _service._log.Write(_service.GetRemoteEndPoint(context), "[" + user.Username + "] IPAM " + operation + "; revision " + _service._ipam.Snapshot().Revision);
                if (_service._clusterManager.ClusterInitialized) _service._clusterManager.TriggerNotifyAllSecondaryNodesIfPrimarySelfNode();
            }

            public void List(HttpContext context)
            {
                Authorize(context, PermissionFlag.View);
                _service.RefreshIpamDiscovery();
                Write(context, "inventory", _service._ipam.Snapshot());
            }
            public void SetView(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.Modify);
                var r = context.Request;
                Write(context, "view", _service._ipam.PutView(Revision(context), user.Username, r.GetQueryOrForm("id", ""), r.GetQueryOrForm("name"), r.GetQueryOrForm("description", "")));
                Changed(context, user, "view set");
            }
            public void SetSubnet(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.Modify);
                var r = context.Request;
                string dhcpScope = r.GetQueryOrForm("dhcpScope", "");
                string dnsZone = r.GetQueryOrForm("dnsZone", "").TrimEnd('.').ToLowerInvariant();
                IpamNetwork network = new IpamNetwork(r.GetQueryOrForm("prefix"));
                if (dhcpScope.Length > 0)
                {
                    if (!_service._authManager.IsPermitted(PermissionSection.DhcpServer, user, PermissionFlag.View)) throw new DnsWebServiceException("DHCP view permission is required to link a scope.");
                    Scope scope = _service._dhcpServer.GetScope(dhcpScope) ?? throw new ArgumentException("Unknown DHCP scope.");
                    if (!network.Contains(scope.StartingAddress) || !network.Contains(scope.EndingAddress)) throw new ArgumentException("Linked DHCP range must be contained in the subnet.");
                }
                if (dnsZone.Length > 0)
                {
                    if (!_service._authManager.IsPermitted(PermissionSection.Zones, dnsZone, user, PermissionFlag.View)) throw new DnsWebServiceException("DNS zone view permission is required to link a zone.");
                    if (_service._dnsServer.AuthZoneManager.GetAuthZoneInfo(dnsZone) is null) throw new ArgumentException("Unknown DNS zone.");
                }
                string vlan = r.GetQueryOrForm("vlan", "");
                Write(context, "subnet", _service._ipam.PutSubnet(Revision(context), user.Username, r.GetQueryOrForm("id", ""), r.GetQueryOrForm("viewId", "default"), network.Prefix, r.GetQueryOrForm("name"), r.GetQueryOrForm("site", ""), vlan.Length == 0 ? null : int.Parse(vlan), dhcpScope, dnsZone, bool.Parse(r.GetQueryOrForm("container", "false"))));
                Changed(context, user, "subnet set");
            }
            public void SetAssignment(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.Modify);
                var r = context.Request;
                Write(context, "assignment", _service._ipam.PutAssignment(Revision(context), user.Username, r.GetQueryOrForm("id", ""), r.GetQueryOrForm("subnetId"), r.GetQueryOrForm("address"), r.GetQueryOrForm("hostName", ""), r.GetQueryOrForm("owner", ""), r.GetQueryOrForm("status", "Assigned"), r.GetQueryOrForm("description", "")));
                Changed(context, user, "assignment set");
            }
            public void Delete(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.Delete);
                _service._ipam.Delete(Revision(context), user.Username, context.Request.GetQueryOrForm("kind"), context.Request.GetQueryOrForm("id"));
                Changed(context, user, "object deleted");
            }

            List<IpamObservation> Observations(IpamSubnet subnet, User user)
            {
                IpamNetwork network = new IpamNetwork(subnet.Prefix);
                List<IpamObservation> result = [];
                if (!string.IsNullOrEmpty(subnet.DhcpScope))
                {
                    if (!_service._authManager.IsPermitted(PermissionSection.DhcpServer, user, PermissionFlag.View)) throw new DnsWebServiceException("DHCP view permission is required for this subnet's observations.");
                    Scope scope = _service._dhcpServer.GetScope(subnet.DhcpScope) ?? throw new InvalidOperationException("Linked DHCP scope is unavailable on this node.");
                    foreach (Lease lease in scope.Leases.Values)
                        if (lease.LeaseExpires > DateTime.UtcNow && network.Contains(lease.Address)) result.Add(new IpamObservation(lease.Address.ToString(), "DhcpLease", scope.Name, lease.HostName, lease.ClientIdentifier.ToString(), lease.LeaseExpires));
                    foreach (Lease lease in scope.ReservedLeases)
                        if (network.Contains(lease.Address)) result.Add(new IpamObservation(lease.Address.ToString(), "DhcpReservation", scope.Name, lease.HostName, lease.ClientIdentifier.ToString(), null));
                    if (scope.RouterAddress is not null && !scope.RouterAddress.Equals(IPAddress.Any) && network.Contains(scope.RouterAddress))
                        result.Add(new IpamObservation(scope.RouterAddress.ToString(), "DhcpGateway", scope.Name, "", "", null));
                    foreach (var device in _service._allocation.LiveLeases(subnet.DhcpScope))
                    {
                        var lease = device.Lease;
                        if (network.Contains(AllocationSequence.Address(lease.Address)) && !result.Any(o => o.Source == "DhcpLease" && o.Address == AllocationSequence.Address(lease.Address).ToString() && o.ClientIdentifier == lease.Client))
                            result.Add(new(AllocationSequence.Address(lease.Address).ToString(), "DhcpLease", device.Scope, lease.Host, lease.Client, lease.Expires));
                    }
                }
                if (!string.IsNullOrEmpty(subnet.DnsZone))
                {
                    if (!_service._authManager.IsPermitted(PermissionSection.Zones, subnet.DnsZone, user, PermissionFlag.View)) throw new DnsWebServiceException("DNS zone view permission is required for this subnet's observations.");
                    if (_service._dnsServer.AuthZoneManager.GetAuthZoneInfo(subnet.DnsZone) is null) throw new InvalidOperationException("Linked DNS zone is unavailable on this node.");
                }
                foreach (var zone in _service._dnsServer.AuthZoneManager.GetAllZones())
                {
                    if (zone.Disabled || (zone.Type != AuthZoneType.Primary && zone.Type != AuthZoneType.Secondary) || !_service._authManager.IsPermitted(PermissionSection.Zones, zone.Name, user, PermissionFlag.View)) continue;
                    List<DnsResourceRecord> records = [];
                    _service._dnsServer.AuthZoneManager.ListAllZoneRecords(zone.Name, [DnsResourceRecordType.A, DnsResourceRecordType.AAAA, DnsResourceRecordType.PTR], records);
                    foreach (DnsResourceRecord record in records)
                    {
                        if (record.GetAuthGenericRecordInfo().Disabled) continue;
                        IPAddress address = RecordAddress(record);
                        if (address is not null && network.Contains(address)) result.Add(new IpamObservation(address.ToString(), "DnsRecord", zone.Name, record.RDATA is DnsPTRRecordData ptr ? ptr.Domain : record.Name, "", null));
                    }
                }
                return result;
            }

            public void Inspect(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.View);
                _service.RefreshIpamDiscovery();
                string subnetId = context.Request.GetQueryOrForm("subnetId");
                IpamSnapshot state = _service._ipam.Snapshot();
                IpamSubnet subnet = state.Subnets.SingleOrDefault(s => s.Id == subnetId) ?? throw new ArgumentException("Unknown subnet.");
                List<IpamObservation> observations = Observations(subnet, user);
                IpamNetwork network = new IpamNetwork(subnet.Prefix);
                string[] intended = state.Assignments.Where(a => a.SubnetId == subnetId).Select(a => a.Address).ToArray();
                int used = intended.Concat(observations.Select(o => o.Address)).Distinct().Count();
                Write(context, "subnet", subnet);
                Write(context, "observations", observations);
                Write(context, "ranges", AddressRanges(subnet, user));
                Write(context, "findings", _service._ipam.Reconcile(subnetId, observations));
                var assets = state.Assignments.Where(a => a.SubnetId == subnetId).Select(a => (a.Address, a.HostName, Source: "IPAM intent"))
                    .Concat(observations.Select(o => (o.Address, o.HostName, Source: o.Source))).Distinct().ToArray();
                var securityFindings = new List<object>();
                foreach (var asset in assets)
                {
                    Security.SecurityDecision decision;
                    try { decision = _service._security.Evaluate(IPAddress.Parse(asset.Address), asset.HostName, [], [IPAddress.Parse(asset.Address)]); }
                    catch (ArgumentException) { decision = _service._security.Evaluate(IPAddress.Parse(asset.Address), null, [], [IPAddress.Parse(asset.Address)]); }
                    if (decision.Matches.Any(i => i.Action != "Allow")) securityFindings.Add(new { address = asset.Address, source = asset.Source, action = decision.Action, reason = decision.Reason, indicators = decision.Matches.Select(i => i.Value).ToArray() });
                }
                Write(context, "securityFindings", securityFindings);
                Write(context, "utilization", new { usableAddresses = network.Capacity.ToString(), intendedAddresses = intended.Length, observedAddresses = observations.Select(o => o.Address).Distinct().Count(), knownUsedAddresses = used });
            }

            public void NextAvailable(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.View);
                _service.RefreshIpamDiscovery();
                string subnetId = context.Request.GetQueryOrForm("subnetId");
                IpamSubnet subnet = _service._ipam.Snapshot().Subnets.SingleOrDefault(s => s.Id == subnetId) ?? throw new ArgumentException("Unknown subnet.");
                // A suggestion is not a DHCP claim or a reservation. Revision checks protect intent writes.
                Write(context, "address", _service._ipam.NextAvailable(subnetId, Observations(subnet, user).Select(o => o.Address), AddressRanges(subnet, user)));
                Write(context, "revision", _service._ipam.Snapshot().Revision);
            }

            IpamAddressRange[] AddressRanges(IpamSubnet subnet, User user)
            {
                var network = new IpamNetwork(subnet.Prefix);
                List<IpamAddressRange> ranges = [];
                foreach (var scope in _service._dhcpServer.Scopes.Values)
                {
                    if (!network.Contains(scope.StartingAddress) && !network.Contains(scope.EndingAddress) && !(new IpamNetwork(scope.NetworkAddress + "/32").Bits == network.Bits && IpamNetwork.Number(scope.StartingAddress) <= network.First && IpamNetwork.Number(scope.EndingAddress) >= network.Last)) continue;
                    if (!_service._authManager.IsPermitted(PermissionSection.DhcpServer, user, PermissionFlag.View)) throw new DnsWebServiceException("DHCP view permission is required to check dynamic pools.");
                    ranges.Add(new(scope.StartingAddress.ToString(), scope.EndingAddress.ToString(), "DhcpPool"));
                    foreach (var exclusion in scope.Exclusions ?? []) ranges.Add(new(exclusion.StartingAddress.ToString(), exclusion.EndingAddress.ToString(), "DhcpExclusion"));
                    if (scope.RouterAddress is not null && !scope.RouterAddress.Equals(IPAddress.Any)) ranges.Add(new(scope.RouterAddress.ToString(), scope.RouterAddress.ToString(), "Gateway"));
                }
                return ranges.ToArray();
            }
        }
    }
}
