using DnsServerCore.Auth;
using DnsServerCore.Cluster;
using DnsServerCore.Dhcp;
using DnsServerCore.Ipam;
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
                }
                if (!string.IsNullOrEmpty(subnet.DnsZone))
                {
                    if (!_service._authManager.IsPermitted(PermissionSection.Zones, subnet.DnsZone, user, PermissionFlag.View)) throw new DnsWebServiceException("DNS zone view permission is required for this subnet's observations.");
                    if (_service._dnsServer.AuthZoneManager.GetAuthZoneInfo(subnet.DnsZone) is null) throw new InvalidOperationException("Linked DNS zone is unavailable on this node.");
                    List<DnsResourceRecord> records = [];
                    _service._dnsServer.AuthZoneManager.ListAllZoneRecords(subnet.DnsZone, [DnsResourceRecordType.A, DnsResourceRecordType.AAAA], records);
                    foreach (DnsResourceRecord record in records)
                    {
                        IPAddress address = record.RDATA switch { DnsARecordData a => a.Address, DnsAAAARecordData aaaa => aaaa.Address, _ => null };
                        if (address is not null && network.Contains(address)) result.Add(new IpamObservation(address.ToString(), "DnsRecord", subnet.DnsZone, record.Name, "", null));
                    }
                }
                return result;
            }

            public void Inspect(HttpContext context)
            {
                User user = Authorize(context, PermissionFlag.View);
                string subnetId = context.Request.GetQueryOrForm("subnetId");
                IpamSnapshot state = _service._ipam.Snapshot();
                IpamSubnet subnet = state.Subnets.SingleOrDefault(s => s.Id == subnetId) ?? throw new ArgumentException("Unknown subnet.");
                List<IpamObservation> observations = Observations(subnet, user);
                IpamNetwork network = new IpamNetwork(subnet.Prefix);
                string[] intended = state.Assignments.Where(a => a.SubnetId == subnetId).Select(a => a.Address).ToArray();
                int used = intended.Concat(observations.Select(o => o.Address)).Distinct().Count();
                Write(context, "subnet", subnet);
                Write(context, "observations", observations);
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
                string subnetId = context.Request.GetQueryOrForm("subnetId");
                IpamSubnet subnet = _service._ipam.Snapshot().Subnets.SingleOrDefault(s => s.Id == subnetId) ?? throw new ArgumentException("Unknown subnet.");
                // A suggestion is not a DHCP claim or a reservation. Revision checks protect intent writes.
                Write(context, "address", _service._ipam.NextAvailable(subnetId, Observations(subnet, user).Select(o => o.Address)));
                Write(context, "revision", _service._ipam.Snapshot().Revision);
            }
        }
    }
}
