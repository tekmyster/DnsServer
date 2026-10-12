using DnsServerCore.Auth;
using DnsServerCore.Cluster;
using DnsServerCore.Dhcp;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary;

namespace DnsServerCore
{
    public partial class DnsWebService
    {
        readonly DistributedAllocation _allocation;
        readonly CancellationTokenSource _allocationStop = new();
        bool IsAllocationService(HttpContext context) => _clusterManager.ClusterInitialized && context.Request.IsHttps && context.GetCurrentSession().Type == UserSessionType.ClusterApiToken && context.Request.QueryOrForm("actingUser") is null;
        void RequireAllocationService(HttpContext context)
        { if (!IsAllocationService(context)) throw new DnsWebServiceException("A direct HTTPS cluster session is required."); }
        void RequireDhcpPermission(HttpContext context, PermissionFlag flag)
        { if (!_authManager.IsPermitted(PermissionSection.DhcpServer, GetSessionUser(context), flag)) throw new DnsWebServiceException("Access was denied."); }
        string AllocationNode(string name)
        {
            if (_clusterManager.ClusterInitialized)
            { if (_clusterManager.TryGetClusterNode(name, out var node)) return node.Name; }
            else if (string.Equals(name, _allocation.NodeName(), StringComparison.OrdinalIgnoreCase)) return _allocation.NodeName();
            throw new ArgumentException("Owner must be an enrolled node.");
        }
        void WriteAllocationState(HttpContext context)
        {
            context.GetCurrentJsonWriter().WritePropertyName("state"); JsonSerializer.Serialize(context.GetCurrentJsonWriter(), _allocation.Snapshot(), DistributedAllocation.JsonOptions);
            context.GetCurrentJsonWriter().WriteString("health", _allocation.Health);
            context.GetCurrentJsonWriter().WriteString("node", _allocation.NodeName());
            context.GetCurrentJsonWriter().WritePropertyName("nodes"); JsonSerializer.Serialize(context.GetCurrentJsonWriter(), _clusterManager.ClusterInitialized ? _clusterManager.ClusterNodes.Values.Select(n => n.Name).Order(StringComparer.Ordinal).ToArray() : [_allocation.NodeName()]);
            context.GetCurrentJsonWriter().WritePropertyName("scopeStatus"); JsonSerializer.Serialize(context.GetCurrentJsonWriter(), _allocation.Snapshot().Plans.Select(p => new { scope = p.Scope, exists = _dhcpServer.GetScope(p.Scope) is not null, matchesOwnership = _dhcpServer.GetScope(p.Scope)?.MatchesAllocationPlan(p) == true, enabled = _dhcpServer.GetScope(p.Scope)?.Enabled == true }).ToArray());
        }
        void GetAllocationState(HttpContext context)
        {
            if (context.GetCurrentSession().Type == UserSessionType.ClusterApiToken && context.Request.QueryOrForm("actingUser") is null) RequireAllocationService(context);
            else RequireDhcpPermission(context, PermissionFlag.View);
            WriteAllocationState(context);
        }
        void LookupAllocationHistory(HttpContext context)
        {
            RequireDhcpPermission(context, PermissionFlag.View);
            uint address = AllocationSequence.Number(System.Net.IPAddress.Parse(context.Request.GetQueryOrForm("address")));
            DateTime timestamp = DateTimeOffset.Parse(context.Request.GetQueryOrForm("timestamp"), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            var writer = context.GetCurrentJsonWriter(); writer.WritePropertyName("device"); JsonSerializer.Serialize(writer, _allocation.ResolveAt(address, timestamp), DistributedAllocation.JsonOptions);
            writer.WriteString("coverage", "Last 2000 durable changes per scope and owner; null means unknown or ambiguous, not proof of absence.");
        }
        async Task SetAllocationPlan(HttpContext context)
        {
            RequireDhcpPermission(context, PermissionFlag.Modify);
            if (_clusterManager.ClusterInitialized && _clusterManager.GetSelfNode().Type != ClusterNodeType.Primary) throw new DnsWebServiceException("Create ownership on the primary.");
            string name = context.Request.GetQueryOrForm("scope"); var scope = _dhcpServer.GetScope(name) ?? throw new ArgumentException("Unknown scope.");
            if (!scope.CanInitializeAllocation) throw new InvalidOperationException("Bootstrap requires a disabled scope with no leases or offers on every owner.");
            string[] owners = context.Request.GetQueryOrForm("owners").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(AllocationNode).ToArray();
            var plan = _allocation.Plan(name);
            if (plan is null)
            {
                using MemoryStream bytes = new(); scope.WriteTo(bytes, false, false);
                var groups = JsonSerializer.Deserialize<DhcpHaGroup[]>(context.Request.GetQueryOrForm("haGroups", "[]"), DistributedAllocation.JsonOptions);
                if (groups is not null) groups = groups.Select(g => g with { Primary = AllocationNode(g.Primary), Secondary = AllocationNode(g.Secondary) }).ToArray();
                plan = scope.BuildAllocationPlan(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), owners) with { Configuration = Convert.ToBase64String(bytes.ToArray()), HaGroups = groups };
                _allocation.AddPlan(plan);
            }
            else if (!plan.Owners.SequenceEqual(owners.Order(StringComparer.Ordinal))) throw new InvalidOperationException("Existing ownership cannot be changed without fenced migration.");
            // Install the same immutable authority on all enrolled nodes while their scopes are drained.
            // A failed bootstrap retains the epoch for an idempotent retry, never repartitions it.
            if (_clusterManager.ClusterInitialized)
                foreach (var peer in _clusterManager.ClusterNodes.Values.Where(n => n.Id != _clusterManager.GetSelfNode().Id))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    await peer.PrepareAllocationAsync(Encoding.UTF8.GetString(_allocation.Export()), deadline.Token);
                }
            _log.Write(GetRemoteEndPoint(context), "[" + GetSessionUser(context).Username + "] Created immutable DHCP allocation ownership for '" + name + "'.");
            WriteAllocationState(context);
        }
        void IngestAllocationState(HttpContext context)
        {
            RequireAllocationService(context);
            string state = context.Request.GetQueryOrForm("state");
            if (Encoding.UTF8.GetByteCount(state) > 8 * 1024 * 1024) throw new ArgumentException("Replication state exceeds 8 MiB.");
            var snapshot = JsonSerializer.Deserialize<AllocationState>(state, DistributedAllocation.JsonOptions);
            if (snapshot is null || snapshot.Replicas is null) throw new ArgumentException("Missing state.");
            foreach (var replica in snapshot.Replicas)
                if (!_clusterManager.TryGetClusterNode(replica.Node, out _)) throw new ArgumentException("Replica source is not enrolled.");
            _allocation.Merge(Encoding.UTF8.GetBytes(state)); WriteAllocationState(context);
        }
        async Task PrepareAllocation(HttpContext context)
        {
            RequireAllocationService(context);
            string state = context.Request.GetQueryOrForm("state");
            if (Encoding.UTF8.GetByteCount(state) > 8 * 1024 * 1024) throw new ArgumentException("Bootstrap exceeds 8 MiB.");
            var snapshot = JsonSerializer.Deserialize<AllocationState>(state, DistributedAllocation.JsonOptions) ?? throw new ArgumentException("Missing state.");
            foreach (var plan in snapshot.Plans)
                foreach (string node in plan.Owners) if (!_clusterManager.TryGetClusterNode(node, out _)) throw new ArgumentException("Owner is not enrolled.");
            await InstallAllocationPlans(snapshot); WriteAllocationState(context);
        }
        async Task GrantAllocation(HttpContext context)
        {
            RequireAllocationService(context);
            string borrower = context.Request.GetQueryOrForm("borrower");
            if (!_clusterManager.TryGetClusterNode(borrower, out _)) throw new ArgumentException("Borrower is not enrolled.");
            borrower = AllocationNode(borrower);
            var scope = _dhcpServer.GetScope(context.Request.GetQueryOrForm("scope")) ?? throw new ArgumentException("Unknown scope.");
            byte[] state = await scope.GrantCapacityAsync(borrower, context.Request.GetQueryOrForm("requestId"));
            var writer = context.GetCurrentJsonWriter(); writer.WritePropertyName("state");
            if (state is null) writer.WriteNullValue(); else writer.WriteRawValue(state);
        }
        async Task InstallAllocationPlans(AllocationState state)
        {
            if (state is null || state.Plans is null) throw new ArgumentException("Missing allocation plans.");
            foreach (var plan in state.Plans)
            {
                if (_allocation.Plan(plan.Scope) is not null) continue;
                var scope = _dhcpServer.GetScope(plan.Scope);
                if (scope is null)
                {
                    if (plan.Configuration is null || plan.Configuration.Length > 1024 * 1024) throw new ArgumentException("Missing bootstrap scope configuration.");
                    using var bytes = new MemoryStream(Convert.FromBase64String(plan.Configuration));
                    scope = new Scope(bytes, _log, _dhcpServer);
                    if (scope.Enabled || scope.Name != plan.Scope) throw new ArgumentException("Bootstrap scope must remain disabled.");
                    if (!scope.CanInitializeAllocation) throw new ArgumentException("Bootstrap cannot import active leases or offers.");
                    var candidate = scope.BuildAllocationPlan(plan.Epoch, plan.Seed, plan.Owners) with { Configuration = plan.Configuration, HaGroups = plan.HaGroups };
                    if (JsonSerializer.Serialize(candidate, DistributedAllocation.JsonOptions) != JsonSerializer.Serialize(plan, DistributedAllocation.JsonOptions)) throw new InvalidOperationException("Bootstrap scope and ownership disagree.");
                    await _dhcpServer.AddScopeAsync(scope);
                }
                if (!scope.CanInitializeAllocation) throw new InvalidOperationException("Disable and drain the scope before joining allocation ownership.");
                var expected = scope.BuildAllocationPlan(plan.Epoch, plan.Seed, plan.Owners) with { Configuration = plan.Configuration, HaGroups = plan.HaGroups };
                if (JsonSerializer.Serialize(expected, DistributedAllocation.JsonOptions) != JsonSerializer.Serialize(plan, DistributedAllocation.JsonOptions)) throw new InvalidOperationException("Scope addressing does not match coordinator ownership.");
            }
            var localConfigurations = (_allocation.Snapshot().Configurations ?? []).ToDictionary(c => c.Scope);
            foreach (var configuration in state.Configurations ?? [])
            {
                if (localConfigurations.TryGetValue(configuration.Scope, out var local) && local.Revision > configuration.Revision) continue;
                if (local is not null && local.Revision == configuration.Revision && local.Hash != configuration.Hash) throw new InvalidOperationException("Conflicting configuration revision.");
                if (configuration.Data.Length > 1024 * 1024 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(configuration.Data))) != configuration.Hash) throw new ArgumentException("Invalid configuration content hash.");
                var scope = _dhcpServer.GetScope(configuration.Scope) ?? throw new ArgumentException("Unknown configured scope.");
                using MemoryStream actual = new(); scope.WriteTo(actual, false, false);
                if (local?.Revision == configuration.Revision && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual.ToArray())) == configuration.Hash) continue;
                var plan = state.Plans.Single(p => p.Scope == configuration.Scope);
                using var data = new MemoryStream(Convert.FromBase64String(configuration.Data)); using var source = new Scope(data, _log, _dhcpServer);
                if (source.Enabled || source.Name != scope.Name) throw new ArgumentException("Replicated configuration cannot activate a listener or rename a scope.");
                await scope.ApplySharedOptionsAsync(source, plan); _dhcpServer.SaveScope(scope.Name);
            }
            _allocation.Merge(JsonSerializer.SerializeToUtf8Bytes(state, DistributedAllocation.JsonOptions), true);
        }
        async Task SynchronizeAllocation(CancellationToken cancellationToken)
        {
            if (!_clusterManager.ClusterInitialized) return;
            bool coordinatorUnavailable = false;
            if (_clusterManager.GetSelfNode().Type == ClusterNodeType.Primary)
                foreach (var plan in _allocation.Snapshot().Plans)
                {
                    var scope = _dhcpServer.GetScope(plan.Scope) ?? throw new InvalidOperationException("A managed scope is missing.");
                    if (!scope.MatchesAllocationPlan(plan)) throw new InvalidOperationException("Scope drift changes ownership eligibility; migration is required.");
                    using MemoryStream configuration = new(); scope.WriteTo(configuration, false, false);
                    _allocation.PublishConfiguration(scope.Name, configuration.ToArray());
                }
            if (_clusterManager.GetSelfNode().Type != ClusterNodeType.Primary)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var state = await _clusterManager.GetPrimaryNode().GetAllocationStateAsync(deadline.Token);
                    await InstallAllocationPlans(JsonSerializer.Deserialize<AllocationState>(state, DistributedAllocation.JsonOptions));
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { coordinatorUnavailable = true; }
            }
            if (_allocation.Snapshot().Plans.Length == 0) return;
            string local = Encoding.UTF8.GetString(_allocation.Export());
            // Every enrolled node receives all owners' observations, independent of a fixed HA pair.
            var peers = _clusterManager.ClusterNodes.Values.Where(n => n.Id != _clusterManager.GetSelfNode().Id).ToArray();
            var results = await Task.WhenAll(peers.Select(async peer =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try { await peer.PushAllocationStateAsync(local, deadline.Token); return true; }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
            }));
            if (coordinatorUnavailable || results.Any(success => !success)) throw new IOException("A coordinator or peer is unavailable; reachable peers received the publication attempt and local authority is retained.");
        }
        async Task<byte[]> BorrowAllocation(string scope, string borrower)
        {
            var plan = _allocation.Plan(scope);
            foreach (string owner in plan.Owners.Where(n => n != borrower))
            {
                if (!_clusterManager.ClusterInitialized || !_clusterManager.TryGetClusterNode(owner, out var donor)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_allocationStop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try { var state = await donor.GrantAllocationAsync(scope, borrower, Guid.NewGuid().ToString("N"), deadline.Token); if (state is not null) return state; }
                catch (Exception) when (!_allocationStop.IsCancellationRequested) { }
            }
            return null;
        }
    }
}
