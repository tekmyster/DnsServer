using DnsServerCore;
using DnsServerCore.Dhcp;
using System.Net;
using System.Reflection;
using System.Text.Json;

static class DistributedDhcpChecks
{
    public static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-dhcp-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        int passed = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); passed++; }
        void Reject(Action action, string reason) { try { action(); } catch (ArgumentException) { passed++; return; } catch (InvalidOperationException) { passed++; return; } catch (InvalidDataException) { passed++; return; } catch (IOException) { passed++; return; } throw new Exception(reason); }
        uint Ip(string address) => AllocationSequence.Number(IPAddress.Parse(address));
        var plan = new AllocationPlan("lab", Guid.NewGuid().ToString("N"), Ip("192.0.2.1"), Ip("192.0.2.120"), "repeatable-seed", ["east-a", "east-b", "west-a"], [new(Ip("192.0.2.4"), Ip("192.0.2.8")), new(Ip("192.0.2.7"), Ip("192.0.2.10"))]);
        try
        {
            var sequence = new AllocationSequence(plan);
            var selections = plan.Owners.Select(owner => sequence.Candidates(owner).ToArray()).ToArray();
            Check(selections.Sum(s => s.Length) == 113 && selections.SelectMany(s => s).Distinct().Count() == 113, "Ownership is not exhaustive and disjoint");
            Check(selections.Max(s => s.Length) - selections.Min(s => s.Length) <= 1, "Sequences are not balanced");
            foreach (string node in plan.Owners) Check(sequence.Candidates(node).All(a => sequence.Owner(a) == node), "Inverse ownership formula differs");
            Check(new AllocationSequence(plan with { Owners = plan.Owners.Reverse().ToArray() }).Candidates("east-a").SequenceEqual(selections[0]), "Owner sorting is unstable");
            Check(selections.SelectMany(s => s).All(a => a < Ip("192.0.2.4") || a > Ip("192.0.2.10")), "Overlapping exclusions are not removed");
            var full = new AllocationSequence(plan with { First = 0, Last = uint.MaxValue, Excluded = [] });
            Check(full.Count == 4294967296UL && full.Candidates("east-a").Take(100).All(a => full.Owner(a) == "east-a"), "Large pool arithmetic overflow or enumeration");
            var reservedOnly = new AllocationSequence(plan with { First = Ip("192.0.2.1"), Last = Ip("192.0.2.2"), Excluded = [], Reservations = [new(Ip("192.0.2.1"), "1-0201"), new(Ip("192.0.2.2"), "1-0202")] });
            Check(reservedOnly.Count == 0 && plan.Owners.All(n => !reservedOnly.Candidates(n).Any()) && reservedOnly.Owner(Ip("192.0.2.1")) is not null, "Reservation-only pools require a nonexistent dynamic sequence");
            Reject(() => new AllocationSequence(plan with { Last = plan.First }), "Too many owners for capacity accepted");
            Reject(() => new AllocationSequence(plan with { Owners = ["east-a", "east-a"] }), "Duplicate owners accepted");
            await using var eastLifetime = new DistributedAllocation(Path.Combine(folder, "east.json")) { NodeName = () => "east-a" };
            var east = eastLifetime;
            await using var second = new DistributedAllocation(Path.Combine(folder, "second.json")) { NodeName = () => "east-b" };
            await using var west = new DistributedAllocation(Path.Combine(folder, "west.json")) { NodeName = () => "west-a" };
            foreach (var store in new[] { east, second, west }) store.AddPlan(plan);
            Reject(() => new DistributedAllocation(Path.Combine(folder, "east.json")), "Two local processes can write one authoritative allocation store");
            uint first = selections[0][0]; DateTime expiry = DateTime.UtcNow.AddHours(1);
            Check(east.Hold("lab", first, "1-0201", "02-01", "client", expiry), "Local durable offer failed");
            Check(!east.Hold("lab", first, "1-0202", "02-02", "other", expiry), "Concurrent address reuse accepted");
            Check(!second.Hold("lab", first, "1-0201", "02-01", "client", expiry), "Disconnected owner used another sequence");
            byte[] old = east.Export(); east.Commit("lab", first, "1-0201", "02-01", "client", expiry);
            east.Commit("lab", first, "1-0201", "02-01", "client", DateTime.UtcNow.AddMinutes(30));
            Check(east.FindLease("lab", "1-0201").Expires == expiry, "Shorter renewal can reuse an address before the previously acknowledged lifetime ends");
            second.Merge(east.Export()); west.Merge(east.Export());
            Check(second.Snapshot().Replicas.Single(r => r.Node == "east-a").Entries.Single().State == "Lease", "Replica lacks lease commit");
            DateTime heldAt = east.Snapshot().Replicas.Single(r => r.Node == "east-a").History.Last().Timestamp;
            Check(west.ResolveAt(first, heldAt)?.Lease.Client == "1-0201", "Historical owner is unavailable on a synchronized peer");
            east.Release("lab", first, "1-0201"); second.Merge(east.Export()); second.Merge(old);
            Check(second.Snapshot().Replicas.Single(r => r.Node == "east-a").Entries.Single().State == "Released", "Delayed snapshot resurrected released lease");
            Check(second.ResolveAt(first, heldAt)?.Lease.Client == "1-0201" && second.ResolveLive(first) is null, "Release destroyed historical ownership or retained current attribution");
            string rollbackPath = Path.Combine(folder, "rollback.json"); File.WriteAllBytes(rollbackPath, old); File.Copy(Path.Combine(folder, "east.json.required"), rollbackPath + ".required");
            Reject(() => new DistributedAllocation(rollbackPath), "Older backup can allocate before contacting peers");
            string missingWatermark = Path.Combine(folder, "missing-watermark.json"); File.WriteAllBytes(missingWatermark, east.Export());
            Reject(() => new DistributedAllocation(missingWatermark), "Missing durable watermark falls back to an unverified owner");
            string stalePath = Path.Combine(folder, "stale.json");
            await using (var stale = new DistributedAllocation(stalePath) { NodeName = () => "east-a" })
            {
                stale.AddPlan(plan); stale.Hold("lab", first, "1-0201", "02-01", "client", expiry);
                Reject(() => stale.Merge(second.Export()), "Restored stale owner silently disregarded newer peer evidence");
                Check(stale.RecoveryRequired && !stale.Available("lab", selections[0][2]), "Stale owner continues allocating after recovery requirement");
            }
            Reject(() => new DistributedAllocation(stalePath), "Restart clears a persisted recovery barrier");
            await east.DisposeAsync();
            await using var recovered = new DistributedAllocation(Path.Combine(folder, "east.json")) { NodeName = () => "east-a" }; east = recovered;
            Check(recovered.Available("lab", first), "Restart lost authoritative release");
            Reject(() => recovered.AddPlan(plan with { Owners = ["east-a", "west-a"] }), "Membership changed without handoff");
            Reject(() => recovered.AddPlan(plan with { Scope = "other" }), "Overlapping distributed scope accepted");
            uint donation = selections[1][0]; string requestId = Guid.NewGuid().ToString("N");
            var grant = second.Grant("lab", donation, "east-a", requestId);
            Check(second.Grant("lab", donation, "east-a", requestId) == grant, "Grant retry creates different authority");
            Check(!second.Available("lab", donation), "Donor can reuse a granted address");
            east.Merge(second.Export());
            Check(east.Hold("lab", donation, "1-0203", "02-03", "borrower", expiry), "Acknowledged overflow grant unusable");
            east.Commit("lab", donation, "1-0203", "02-03", "borrower", expiry); west.Merge(east.Export());
            Check(west.Snapshot().Replicas.Single(r => r.Node == "east-a").Entries.Any(e => e.Address == donation && e.State == "Lease" && e.AuthorityOwner == "east-b"), "Borrowed lease authority missing on third node");
            await second.DisposeAsync();
            await using var donorRestart = new DistributedAllocation(Path.Combine(folder, "second.json")) { NodeName = () => "east-b" };
            Check(!donorRestart.Available("lab", donation), "Restart discarded donor hold");
            string lostPath = Path.Combine(folder, "lost.json"); File.WriteAllText(lostPath + ".required", "1");
            Reject(() => new DistributedAllocation(lostPath), "Missing authoritative allocation file fell back to legacy DHCP");
            uint contested = selections[0][1]; int successes = 0;
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => { if (east.Hold("lab", contested, "client-" + i, "02-01", "test", expiry)) Interlocked.Increment(ref successes); })));
            Check(successes == 1, "Parallel offers overlap");
            east.Decline("lab", contested, east.Snapshot().Replicas.Single(r => r.Node == "east-a").Entries.Single(e => e.Address == contested).Client);
            Check(!east.Available("lab", contested) && !east.Available("lab", contested, "client-0"), "Declined address is immediately reusable");

            await using var log = new LogManager(true, folder);
            using var serverA = new DhcpServer(Path.Combine(folder, "scope-a"), log);
            using var serverB = new DhcpServer(Path.Combine(folder, "scope-b"), log);
            using var serverC = new DhcpServer(Path.Combine(folder, "scope-c"), log);
            await using var storeA = new DistributedAllocation(Path.Combine(folder, "packet-a.json")) { NodeName = () => "east-a" };
            await using var storeB = new DistributedAllocation(Path.Combine(folder, "packet-b.json")) { NodeName = () => "east-b" };
            await using var storeC = new DistributedAllocation(Path.Combine(folder, "packet-c.json")) { NodeName = () => "west-a" };
            serverA.Allocation = storeA; serverB.Allocation = storeB; serverC.Allocation = storeC;
            Scope NewScope(DhcpServer server) => new("packets", false, IPAddress.Parse("198.51.100.10"), IPAddress.Parse("198.51.100.21"), IPAddress.Parse("255.255.255.0"), log, server) { DnsUpdates = false, IgnoreClientIdentifierOption = true };
            using var scopeA = NewScope(serverA); using var scopeB = NewScope(serverB); using var scopeC = NewScope(serverC);
            var packetPlan = scopeA.BuildAllocationPlan(Guid.NewGuid().ToString("N"), "packet-test", plan.Owners) with { HaGroups = [new("east", "East", "east-a", "east-b")] };
            foreach (var store in new[] { storeA, storeB, storeC }) store.AddPlan(packetPlan);
            Check(scopeA.DistributedOfferDelayMilliseconds == 0 && scopeB.DistributedOfferDelayMilliseconds == 500 && scopeC.DistributedOfferDelayMilliseconds == 1500, "Primary/secondary/regional serving preference is not deterministic");
            Reject(() => new AllocationSequence(plan with { HaGroups = [new("bad", "East", "east-a", "east-a")] }), "Invalid HA pair accepted");
            Type messageType = typeof(Scope).Assembly.GetType("DnsServerCore.Dhcp.DhcpMessage");
            object Discover(byte client)
            {
                byte[] wire = new byte[244]; wire[0] = 1; wire[1] = 1; wire[2] = 6;
                wire[28] = 2; wire[33] = client; new byte[] { 99, 130, 83, 99, 53, 1, 1, 255 }.CopyTo(wire, 236);
                return Activator.CreateInstance(messageType, [new MemoryStream(wire)]);
            }
            var offerMethod = typeof(Scope).GetMethod("GetOfferAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var commitMethod = typeof(Scope).GetMethod("CommitLease", BindingFlags.Instance | BindingFlags.NonPublic);
            async Task<Lease> Offer(Scope scope, byte client) => await (Task<Lease>)offerMethod.Invoke(scope, [Discover(client)]);
            var allOffers = new List<Lease>();
            for (byte i = 1; i <= 4; i++)
            {
                var offers = await Task.WhenAll(Offer(scopeA, i), Offer(scopeB, (byte)(i + 10)), Offer(scopeC, (byte)(i + 20)));
                Check(offers.All(o => o is not null), "One of three isolated servers cannot serve its own sequence"); allOffers.AddRange(offers);
            }
            Check(allOffers.Select(o => o.Address).Distinct().Count() == 12, "Actual DHCP offer hooks duplicated an address across three isolated owners");
            await serverB.AddScopeAsync(scopeB); scopeB.SetEnabled(true);
            typeof(Scope).GetField("_interfaceAddress", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scopeB, IPAddress.Any);
            byte[] rebindingWire = new byte[244]; rebindingWire[0] = 1; rebindingWire[1] = 1; rebindingWire[2] = 6;
            allOffers[0].Address.GetAddressBytes().CopyTo(rebindingWire, 12); IPAddress.Parse("198.51.100.1").GetAddressBytes().CopyTo(rebindingWire, 24);
            rebindingWire[28] = 2; rebindingWire[33] = 1; new byte[] { 99, 130, 83, 99, 53, 1, 3, 255 }.CopyTo(rebindingWire, 236);
            var processPacket = typeof(DhcpServer).GetMethod("ProcessDhcpMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var peerReplyTask = (Task)processPacket.Invoke(serverB, [Activator.CreateInstance(messageType, [new MemoryStream(rebindingWire)]), new IPEndPoint(IPAddress.Loopback, 68), default(System.Net.Sockets.IPPacketInformation)]);
            await peerReplyTask;
            Check(peerReplyTask.GetType().GetProperty("Result").GetValue(peerReplyTask) is null, "A peer owner NAKs another owner's valid lease");
            scopeB.SetEnabled(false);
            Check(await Offer(scopeA, 99) is null, "Exhausted sequence borrowed without donor authority");
            using (var beforeAckRestart = NewScope(serverA))
                Check(beforeAckRestart.GetExistingLeaseOrOffer((DhcpMessage)Discover(1))?.Address.Equals(allOffers[0].Address) == true, "Restart loses an outstanding durable offer before client REQUEST");
            commitMethod.Invoke(scopeA, [allOffers[0]]);
            var durable = storeA.FindLease("packets", "1-020000000001");
            Check(durable is not null && durable.State == "Lease", "Actual ACK hook did not durably commit");
            using var freshScopeA = NewScope(serverA);
            Check((await Offer(freshScopeA, 1)).Address.Equals(allOffers[0].Address), "Crash recovery changed a still-active client's address");

            // A second lab demonstrates own-sequence exhaustion followed by acknowledged overflow.
            using var overflowScopeA = new Scope("overflow", false, IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.21"), IPAddress.Parse("255.255.255.0"), log, serverA) { DnsUpdates = false, IgnoreClientIdentifierOption = true };
            using var overflowScopeB = new Scope("overflow", false, IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.21"), IPAddress.Parse("255.255.255.0"), log, serverB) { DnsUpdates = false, IgnoreClientIdentifierOption = true };
            var overflowPlan = overflowScopeA.BuildAllocationPlan(Guid.NewGuid().ToString("N"), "overflow-test", plan.Owners);
            storeA.AddPlan(overflowPlan); storeB.AddPlan(overflowPlan);
            int borrowCalls = 0;
            serverA.BorrowCapacity = async (_, borrower) => { borrowCalls++; return await overflowScopeB.GrantCapacityAsync(borrower, Guid.NewGuid().ToString("N")); };
            for (byte i = 31; i <= 34; i++) Check(await Offer(overflowScopeA, i) is not null, "Own overflow lab sequence unavailable");
            Check(borrowCalls == 0, "Borrowed before exhausting own sequence");
            var overflow = await Offer(overflowScopeA, 35);
            Check(overflow is not null && borrowCalls == 1 && !storeB.Available("overflow", AllocationSequence.Number(overflow.Address)), "Actual offer did not obtain exclusive donor hold");
            serverA.BorrowCapacity = (_, _) => Task.FromResult<byte[]>(null);
            Check((await Offer(overflowScopeA, 35)).Address.Equals(overflow.Address), "Disconnected borrower lost its existing granted offer");
            overflowScopeA.ObservePeerSelection((DhcpMessage)Discover(35));
            Check(storeA.FindOffer("overflow", "1-020000000023") is null && await Offer(overflowScopeA, 35) is null, "Selecting another server leaves a held offer or delayed duplicate offer");
            await using (var security = new DnsServerCore.Security.DdiSecurityManager(Path.Combine(folder, "ack-risk-security"), new DnsServerCore.Security.PublicSuffixes(["example"])))
            using (var ackScope = new Scope("ack-risk", false, IPAddress.Parse("10.0.0.10"), IPAddress.Parse("10.0.0.20"), IPAddress.Parse("255.255.255.0"), log, serverA) { DnsUpdates = false, IgnoreClientIdentifierOption = true })
            {
                serverA.SecurityManager = security;
                security.SetPolicy(DnsServerCore.Security.SecurityPolicy.Default with { Enabled = true, Mode = "Block", DhcpResponse = "DenyNewLeases", DeviceThreshold = 80 }, 0);
                var pending = await Offer(ackScope, 45);
                security.ObserveDhcpConflict("ack-risk", "1-02000000002D", "02-00-00-00-00-2D", "client", pending.Address.ToString());
                Reject(() => ackScope.CommitLease(pending), "New IoA between OFFER and REQUEST does not stop a new ACK");
                Check(ackScope.Leases.Count == 0, "Denied ACK created a committed lease");
                serverA.SecurityManager = null;
            }
            Console.WriteLine($"PASS: {passed} distributed DHCP sequence, persistence, three-owner partition, and actual offer/ACK/overflow checks.");
        }
        finally
        {
            if (Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected test directory");
            Directory.Delete(folder, true);
        }
    }
}
