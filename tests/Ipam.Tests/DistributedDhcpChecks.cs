using DnsServerCore;
using DnsServerCore.Dhcp;
using System.Net;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;

static class DistributedDhcpChecks
{
    public static async Task RunUdpListeners()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ddi-udp-listener-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int passed = 0;
        try
        {
            await CheckUdpListenerLifecycle(folder, (value, reason) => { if (!value) throw new Exception(reason); passed++; });
            Console.WriteLine($"UDP listener lifecycle: {passed} checks passed");
        }
        finally { Directory.Delete(folder, true); }
    }

    static async Task CheckUdpListenerLifecycle(string folder, Action<bool, string> check)
    {
        string configuration = Path.Combine(folder, "udp-listeners"); Directory.CreateDirectory(configuration);
        await using var log = new LogManager(true, configuration);
        using var server = new DhcpServer(configuration, log);
        var bind = typeof(DhcpServer).GetMethod("BindUdpListener", BindingFlags.Instance | BindingFlags.NonPublic);
        var unbind = typeof(DhcpServer).GetMethod("UnbindUdpListener", BindingFlags.Instance | BindingFlags.NonPublic);
        var listeners = (System.Collections.IDictionary)typeof(DhcpServer).GetField("_udpListeners", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        async Task Concurrent(Action<int> operation)
        {
            const int count = 24;
            using var ready = new CountdownEvent(count); using var release = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, count).Select(index => Task.Factory.StartNew(() => { ready.Signal(); release.Wait(); operation(index); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            bool started = ready.Wait(TimeSpan.FromSeconds(10)); release.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
            check(started, "Concurrent UDP listener operations did not all reach the start barrier");
        }
        int BoundSockets(IPEndPoint endpoint) => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Count(address => address.Equals(endpoint));
        for (int iteration = 0; iteration < 6; iteration++)
        {
            IPEndPoint endpoint;
            using (var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
            { probe.Bind(new IPEndPoint(IPAddress.Loopback, 0)); endpoint = (IPEndPoint)probe.LocalEndPoint; }
            await Concurrent(_ => bind.Invoke(server, [endpoint]));
            var listener = listeners[IPAddress.Loopback];
            var socket = (System.Net.Sockets.Socket)listener.GetType().GetProperty("Socket").GetValue(listener);
            check(listeners.Count == 1 && (int)listener.GetType().GetProperty("ScopeCount").GetValue(listener) == 24, "Concurrent binding lost tracked scope references");
            check(BoundSockets(endpoint) == 1, "Concurrent binding leaked an untracked receive socket");
            await Concurrent(_ => unbind.Invoke(server, [endpoint]));
            check(listeners.Count == 0 && socket.SafeHandle.IsClosed && BoundSockets(endpoint) == 0, "Final concurrent unbind left residual DHCP ingress");
            using (var replacement = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp) { ExclusiveAddressUse = true })
            using (var sender = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
            {
                replacement.Bind(endpoint);
                byte[] payload = System.Text.Encoding.UTF8.GetBytes("no-residual-dhcp-ingress");
                await sender.SendToAsync(payload, endpoint);
                byte[] received = new byte[128];
                var packet = await replacement.ReceiveFromAsync(received, System.Net.Sockets.SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)).WaitAsync(TimeSpan.FromSeconds(5));
                check(received.AsSpan(0, packet.ReceivedBytes).SequenceEqual(payload), "Released listener endpoint cannot exclusively receive real UDP traffic");
            }
            bind.Invoke(server, [endpoint]);
            await Concurrent(_ => { bind.Invoke(server, [endpoint]); unbind.Invoke(server, [endpoint]); });
            listener = listeners[IPAddress.Loopback];
            check(listeners.Count == 1 && BoundSockets(endpoint) == 1 && (int)listener.GetType().GetProperty("ScopeCount").GetValue(listener) == 1, "Mixed bind/unbind lost the remaining listener owner");
            check((bool)unbind.Invoke(server, [endpoint]) && listeners.Count == 0 && BoundSockets(endpoint) == 0, "Last owner cannot close a reused listener");
        }
        IPEndPoint occupiedEndpoint;
        using (var occupied = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp) { ExclusiveAddressUse = true })
        {
            occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0)); occupiedEndpoint = (IPEndPoint)occupied.LocalEndPoint;
            bool rejected = false;
            try { bind.Invoke(server, [occupiedEndpoint]); }
            catch (TargetInvocationException error) when (error.InnerException is System.Net.Sockets.SocketException) { rejected = true; }
            check(rejected && listeners.Count == 0, "Failed binding published a listener or leaked scope references");
        }
        bind.Invoke(server, [occupiedEndpoint]); bind.Invoke(server, [occupiedEndpoint]);
        var remaining = listeners[IPAddress.Loopback];
        var remainingSocket = (System.Net.Sockets.Socket)remaining.GetType().GetProperty("Socket").GetValue(remaining);
        server.Start();
        server.Stop();
        check(listeners.Count == 0 && remainingSocket.SafeHandle.IsClosed && BoundSockets(occupiedEndpoint) == 0, "Service stop discarded tracked listeners without disposing their sockets");
        using var stopReplacement = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp) { ExclusiveAddressUse = true };
        stopReplacement.Bind(occupiedEndpoint);
        check(stopReplacement.IsBound, "Service-stop socket cannot be exclusively rebound");
    }

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
            await CheckUdpListenerLifecycle(folder, Check);
            await CheckCrashBoundaries(folder, Check);
            await CheckLiveBootstrap(folder, Check, Reject);
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
            string stagedPath = Path.Combine(folder, "staged-recovery.json");
            File.WriteAllBytes(stagedPath, old);
            File.WriteAllBytes(stagedPath + ".tmp", east.Export());
            File.Copy(Path.Combine(folder, "east.json.required"), stagedPath + ".required");
            await using (var stagedRecovery = new DistributedAllocation(stagedPath) { NodeName = () => "east-a" })
                Check(stagedRecovery.Export().SequenceEqual(east.Export()) && !File.Exists(stagedPath + ".tmp"), "Interrupted rename did not complete the generation bound to its durable barrier");
            string missingSnapshotStage = Path.Combine(folder, "missing-snapshot-staged.json");
            File.WriteAllBytes(missingSnapshotStage + ".tmp", east.Export());
            File.Copy(Path.Combine(folder, "east.json.required"), missingSnapshotStage + ".required");
            await using (var firstGeneration = new DistributedAllocation(missingSnapshotStage) { NodeName = () => "east-a" })
                Check(firstGeneration.Export().SequenceEqual(east.Export()), "Interrupted initial snapshot cannot recover its barrier-bound staged generation");
            string wrongStage = Path.Combine(folder, "wrong-stage.json");
            File.WriteAllBytes(wrongStage, old); File.WriteAllBytes(wrongStage + ".tmp", old);
            File.Copy(Path.Combine(folder, "east.json.required"), wrongStage + ".required");
            Reject(() => new DistributedAllocation(wrongStage), "Unverified staged data bypasses the authority barrier");
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
            uint transferred = selections[0][5], quarantined = selections[0][6], pendingTransfer = selections[0][7];
            east.Commit("lab", transferred, "1-0210", "02-10", "moved", expiry);
            east.Hold("lab", quarantined, "1-0211", "02-11", "declined", expiry); east.Decline("lab", quarantined, "1-0211");
            east.Hold("lab", pendingTransfer, "1-0212", "02-12", "pending", expiry);
            long handoffSequence = east.Snapshot().Replicas.Single(r => r.Scope == "lab" && r.Node == "east-a").Sequence;
            string handoffId = Guid.NewGuid().ToString("N"); byte[] beforeHandoff = east.Export();
            Reject(() => east.Handoff("lab", [transferred], "west-a", handoffId, handoffSequence - 1), "Stale handoff snapshot accepted");
            Reject(() => east.Handoff("lab", [transferred, selections[2][0]], "west-a", handoffId, handoffSequence), "Batch surrender accepts another owner's address");
            Check(east.HasAuthority("lab", transferred), "Rejected batch partially surrendered authority");
            east.Handoff("lab", [transferred, quarantined, pendingTransfer], "west-a", handoffId, handoffSequence);
            byte[] afterHandoff = east.Export();
            east.Handoff("lab", [pendingTransfer, transferred, quarantined], "west-a", handoffId, handoffSequence);
            Check(east.Export().SequenceEqual(afterHandoff), "Lost-response retry changed durable handoff");
            Check(!east.HasAuthority("lab", transferred) && east.FindLease("lab", "1-0210") is null, "Donor retains surrendered lease authority");
            Check(!east.EntireOwnerSurrendered("lab", "east-a", "west-a"), "Partial handoff permits fenced cluster promotion");
            Reject(() => east.Commit("lab", transferred, "1-0210", "02-10", "moved", expiry), "Donor issues an ACK after surrender");
            Check(!west.HasAuthority("lab", transferred), "Receiver acquires authority without donor acknowledgement");
            west.Merge(afterHandoff); west.Merge(beforeHandoff);
            Check(west.FindLease("lab", "1-0210")?.Address == transferred && west.FindLease("lab", "1-0210")?.Expires == expiry, "Receiver cannot recover the original lease lifetime");
            Check(west.ResolveLive(transferred)?.Node == "west-a" && west.ResolveLive(transferred)?.Lease.Client == "1-0210", "Shared DNS identity disappears during handoff");
            Check(!west.Available("lab", transferred, "new-client") && west.Available("lab", transferred, "1-0210"), "Live surrendered lease can be reassigned to a different client");
            Check(!west.Available("lab", quarantined, "1-0211"), "Handoff drops the declined-address quarantine");
            Check(west.FindOffer("lab", "1-0212")?.Address == pendingTransfer && !west.Available("lab", pendingTransfer, "new-client"), "Handoff drops a pending offer");
            DateTime transferredAt = east.Snapshot().Replicas.Single(r => r.Node == "east-a").History.First(h => h.After.Address == transferred && h.After.State == "Grant").Timestamp;
            Check(west.ResolveAt(transferred, transferredAt)?.Node == "west-a", "Historical attribution has a gap before the receiver's first ACK");
            string receiverCopy = Path.Combine(folder, "receiver-restart.json"); File.WriteAllBytes(receiverCopy, west.Export()); File.Copy(Path.Combine(folder, "west.json.required"), receiverCopy + ".required");
            await using (var restartedReceiver = new DistributedAllocation(receiverCopy) { NodeName = () => "west-a" })
            {
                Check(restartedReceiver.FindLease("lab", "1-0210")?.Expires == expiry && !restartedReceiver.Available("lab", quarantined, "1-0211"), "Receiver restart drops inherited lease or quarantine protection");
                restartedReceiver.Release("lab", transferred, "1-0210");
                Check(restartedReceiver.FindLease("lab", "1-0210") is null, "Release before first recipient ACK does not clear inherited lease");
                restartedReceiver.Decline("lab", pendingTransfer, "1-0212");
                Check(restartedReceiver.Snapshot().Replicas.Single(r => r.Node == "west-a").Entries.Single(e => e.Address == pendingTransfer).State == "Declined" && !restartedReceiver.Available("lab", pendingTransfer, "1-0212"), "Decline before first recipient ACK does not quarantine the inherited offer");
            }
            string donorCopy = Path.Combine(folder, "donor-handoff-restart.json"); File.WriteAllBytes(donorCopy, east.Export()); File.Copy(Path.Combine(folder, "east.json.required"), donorCopy + ".required");
            await using (var restartedDonor = new DistributedAllocation(donorCopy) { NodeName = () => "east-a" })
                Check(!restartedDonor.HasAuthority("lab", transferred), "Donor restart restores surrendered authority");
            uint reservedAddress = Ip("10.77.0.10");
            var reservationPlan = plan with { Scope = "reservation-handoff", Epoch = Guid.NewGuid().ToString("N"), First = reservedAddress, Last = reservedAddress, Excluded = [], Reservations = [new(reservedAddress, "1-0214")] };
            string reservationOwner = new AllocationSequence(reservationPlan).Owner(reservedAddress);
            string reservationReceiver = plan.Owners.First(n => n != reservationOwner);
            await using var reservationDonor = new DistributedAllocation(Path.Combine(folder, "reservation-donor.json")) { NodeName = () => reservationOwner };
            await using var reservationTarget = new DistributedAllocation(Path.Combine(folder, "reservation-target.json")) { NodeName = () => reservationReceiver };
            reservationDonor.AddPlan(reservationPlan); reservationTarget.AddPlan(reservationPlan);
            reservationDonor.Handoff(reservationPlan.Scope, [reservedAddress], reservationReceiver, Guid.NewGuid().ToString("N"), 0);
            reservationTarget.Merge(reservationDonor.Export());
            Check(reservationTarget.EntireOwnerSurrendered(reservationPlan.Scope, reservationOwner, reservationReceiver), "Reservation-only complete surrender is not recognized");
            Check(reservationTarget.Available(reservationPlan.Scope, reservedAddress, "1-0214") && !reservationTarget.Available(reservationPlan.Scope, reservedAddress, "other"), "Reservation handoff loses its fixed client binding");
            reservationTarget.Commit(reservationPlan.Scope, reservedAddress, "1-0214", "02-14", "reserved", expiry);
            Check(!reservationDonor.Available(reservationPlan.Scope, reservedAddress, "1-0214") && reservationTarget.FindLease(reservationPlan.Scope, "1-0214")?.Address == reservedAddress, "Transferred reservation has duplicate ACK authority");
            west.Commit("lab", transferred, "1-0210", "02-10", "moved", DateTime.UtcNow.AddMinutes(1));
            Check(west.FindLease("lab", "1-0210")?.Expires == expiry, "Receiver shortens a previously acknowledged lifetime");
            west.Release("lab", transferred, "1-0210");
            west.Hold("lab", selections[2][1], "1-0213", "02-13", "unrelated", expiry);
            Check(west.FindLease("lab", "1-0210") is null && west.ResolveLive(transferred) is null, "Compaction resurrects a released inherited lease");
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
            string packetHandoffId = Guid.NewGuid().ToString("N");
            storeA.Handoff("packets", [durable.Address], "east-b", packetHandoffId, storeA.Snapshot().Replicas.Single(r => r.Node == "east-a").Sequence);
            storeB.Merge(storeA.Export());
            using var receivingScope = NewScope(serverB);
            var inheritedLease = receivingScope.GetExistingLeaseOrOffer((DhcpMessage)Discover(1));
            Check(inheritedLease?.Address.Equals(allOffers[0].Address) == true, "Actual recipient REQUEST hook loses the handed-off client");
            receivingScope.CommitLease(inheritedLease);
            Check(storeB.FindLease("packets", "1-020000000001")?.Address == durable.Address && !scopeA.CanRespondToDistributedRequest(allOffers[0].Address), "Recipient ACK does not persist exclusive authority");

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
    static async Task CheckLiveBootstrap(string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        uint Ip(string address) => AllocationSequence.Number(IPAddress.Parse(address));
        string id = Guid.NewGuid().ToString("N");
        var plan = new AllocationPlan("migration", id, Ip("192.0.2.10"), Ip("192.0.2.90"), id, ["a", "b", "c"], [], Configuration: "AQ==");
        var sequence = new AllocationSequence(plan);
        uint leased = sequence.Candidates("b").First(), offered = sequence.Candidates("c").First(), declined = sequence.Candidates("b").Skip(1).First();
        DateTime expiry = DateTime.UtcNow.AddHours(5), now = DateTime.UtcNow;
        AllocationEntry lease = new(leased, "1-0201", "02-01", "native.example", "Lease", expiry);
        AllocationEntry offer = new(offered, "1-0202", "02-02", "offer.example", "Offer", now.AddSeconds(60));
        AllocationEntry quarantine = new(declined, "1-0203", "02-03", null, "Declined", now.AddMinutes(10));
        AllocationNativeSnapshot[] native = [new("a", plan.Scope, plan.Configuration, [lease, offer, quarantine]), new("b", plan.Scope, "Ag==", [lease with { Expires = expiry.AddHours(-1) }]), new("c", plan.Scope, "Aw==", [])];
        var input = DistributedAllocation.BuildBootstrap(id, "a", plan, native, now);
        native[0].Entries[0] = lease with { Client = "1-ffff" };
        check(input.Native[0].Entries[0] == lease, "Bootstrap input aliases caller native evidence");
        native = input.Native;
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(n => n.Node == "b" ? n with { Entries = [lease with { Client = "1-0204" }] } : n).ToArray(), now), "Conflicting dormant native lease accepted");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(n => n.Node == "c" ? n with { Entries = [offer with { Client = "1-0205", State = "Offer" }] } : n).ToArray(), now), "Unknown peer offer is inferred free");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(n => n.Node == "c" ? n with { Entries = [lease with { Address = plan.First - 1 }] } : n).ToArray(), now), "Out of pool active native evidence accepted");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, [null, native[1], native[2]], now), "Null native participant accepted");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(n => n.Node == "c" ? n with { Entries = [lease with { Expires = DateTime.SpecifyKind(now.AddHours(-1), DateTimeKind.Unspecified) }] } : n).ToArray(), now), "Malformed expired native evidence accepted");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan with { Configuration = "BA==" }, native, now), "Unrelated source options accepted");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(snapshot => snapshot.Node == "b" ? snapshot with { Entries = [lease with { State = "Declined" }] } : snapshot).ToArray(), now), "Dormant quarantine silently replaces a valid live client binding");
        reject(() => DistributedAllocation.BuildBootstrap(id, "a", plan, native.Select(snapshot => snapshot.Node == "c" ? snapshot with { Entries = [lease with { Client = "not-an-identifier", Expires = now.AddHours(-1) }] } : snapshot).ToArray(), now), "Malformed expired native client identifier accepted");
        var paths = plan.Owners.Select(n => Path.Combine(folder, "migration-" + n + ".json")).ToArray();
        var stores = paths.Select((path, index) => new DistributedAllocation(path) { NodeName = () => plan.Owners[index] }).ToArray();
        try
        {
            var futureCapture = DistributedAllocation.BuildBootstrap(id, "a", plan, native, DateTime.UtcNow.AddSeconds(90));
            reject(() => stores[0].PrepareBootstrap(futureCapture), "Future capture timestamp discards a still-active offer");
            foreach (var store in stores) store.PrepareBootstrap(input);
            var record = stores[0].Bootstrap(plan.Scope);
            record.PreparedNodes[0] = "forged";
            stores[0].Snapshot().Bootstraps[0].PreparedNodes[0] = "forged";
            check(stores[0].Bootstrap(plan.Scope).PreparedNodes.SequenceEqual(["a"]), "Returned acknowledgements mutate durable local state");
            foreach (var store in stores) check(!store.Available(plan.Scope, leased, lease.Client) && !store.Hold(plan.Scope, sequence.Candidates(store.NodeName()).Last(), "1-0210", "02-10", null, expiry), "Prepared migration grants active allocation");
            reject(() => stores[0].CommitBootstrap(plan.Scope, id, record.Hash), "Coordinator commits without all acknowledgements");
            reject(() => stores[0].AcknowledgeBootstrap(plan.Scope, id, record.Hash, "d"), "Unenrolled preparation acknowledgement accepted");
            reject(() => stores[0].PrepareBootstrap(input with { Seeds = input.Seeds.Select(r => r.Node == "a" ? r with { Entries = [] } : r).ToArray() }), "Forged bootstrap seeds accepted");
            byte[] prepared = stores[0].Export();
            var invalidState = stores[0].Snapshot();
            reject(() => stores[1].Merge(JsonSerializer.SerializeToUtf8Bytes(invalidState with { Bootstraps = [invalidState.Bootstraps[0] with { PreparedNodes = ["a", "a"] }] }, DistributedAllocation.JsonOptions)), "Malformed duplicate acknowledgement array accepted");
            reject(() => stores[1].Merge(JsonSerializer.SerializeToUtf8Bytes(invalidState with { Bootstraps = [invalidState.Bootstraps[0] with { PreparedNodes = null }] }, DistributedAllocation.JsonOptions)), "Missing acknowledgement array accepted");
            foreach (var store in stores) store.Merge(stores[1].Export());
            check(stores[0].Bootstrap(plan.Scope).Hash == record.Hash && stores[0].Bootstrap(plan.Scope).PreparedNodes.SequenceEqual(["a"]), "Ordinary mesh traffic changes local preparation journal");
            prepared = stores[0].Export();
            stores[0].AcknowledgeBootstrap(plan.Scope, id, record.Hash, "b");
            string rollback = Path.Combine(folder, "migration-rollback.json");
            File.WriteAllBytes(rollback, prepared); File.Copy(paths[0] + ".required", rollback + ".required");
            reject(() => new DistributedAllocation(rollback), "Older prepared acknowledgements bypass watermark");
            File.WriteAllBytes(rollback + ".tmp", stores[0].Export());
            await using (var recovered = new DistributedAllocation(rollback) { NodeName = () => "a" })
                check(recovered.Bootstrap(plan.Scope).PreparedNodes.Contains("b") && recovered.BootstrapPending(plan.Scope), "Crash between barrier and rename loses prepared journal");
            string noSafety = Path.Combine(folder, "migration-no-safety.json");
            File.WriteAllBytes(noSafety, prepared);
            var watermark = JsonSerializer.Deserialize<AllocationWatermark>(File.ReadAllBytes(paths[0] + ".required"), DistributedAllocation.JsonOptions);
            File.WriteAllBytes(noSafety + ".required", JsonSerializer.SerializeToUtf8Bytes(watermark with { SafetyHash = null }, DistributedAllocation.JsonOptions));
            reject(() => new DistributedAllocation(noSafety), "Old watermark permits a migration journal");
            await stores[0].DisposeAsync(); stores[0] = new DistributedAllocation(paths[0]) { NodeName = () => "a" };
            byte[] beforeRetry = stores[0].Export(); stores[0].PrepareBootstrap(input);
            check(stores[0].Export().SequenceEqual(beforeRetry), "Lost prepare response reseeds migrated authority");
            stores[0].AcknowledgeBootstrap(plan.Scope, id, record.Hash, "c");
            foreach (var store in stores.Skip(1)) store.CommitBootstrap(plan.Scope, id, record.Hash);
            stores[0].CommitBootstrap(plan.Scope, id, record.Hash);
            check(stores[0].FindLease(plan.Scope, lease.Client)?.Expires == expiry && stores[0].FindLease(plan.Scope, lease.Client)?.Address == leased, "Migration changes client, address or acknowledged expiry");
            check(stores[0].FindOffer(plan.Scope, offer.Client)?.Address == offered && !stores[2].Available(plan.Scope, offered), "Migration loses outstanding offer or donor hold");
            check(!stores[0].Available(plan.Scope, declined, quarantine.Client) && !stores[1].Available(plan.Scope, declined), "Migration loses DECLINE quarantine");
            stores[0].Commit(plan.Scope, leased, lease.Client, lease.Hardware, lease.Host, expiry.AddHours(-2));
            check(stores[0].FindLease(plan.Scope, lease.Client).Expires == expiry, "Short live renewal reduces preserved native lifetime");
            byte[] renewed = stores[0].Export(); stores[0].PrepareBootstrap(input); stores[0].CommitBootstrap(plan.Scope, id, record.Hash);
            check(stores[0].Export().SequenceEqual(renewed), "Committed replay overwrites live renewals");
            var concurrent = await Task.WhenAll(stores.Select(store => Task.Run(() => sequence.Candidates(store.NodeName()).Where(address => store.Hold(plan.Scope, address, "1-02" + address.ToString("x8"), "02-10", null, expiry)).ToArray())));
            check(concurrent.SelectMany(addresses => addresses).Distinct().Count() == concurrent.Sum(addresses => addresses.Length), "Concurrent migrated owners allocate overlapping addresses");
            foreach (var store in stores.Skip(1)) store.Merge(stores[0].Export());
            check(stores[1].Bootstrap(plan.Scope).PreparedNodes.SequenceEqual(["b"]), "Mesh overwrites local acknowledgement journal");
        }
        finally { foreach (var store in stores) await store.DisposeAsync(); }
        string nativePath = Path.Combine(folder, "native-hold.json");
        await using (var store = new DistributedAllocation(nativePath) { NodeName = () => "a" })
        {
            store.ProtectNative(plan.Scope, quarantine); store.ProtectNativeOffers(plan.Scope, [offer]);
            reject(() => store.ProtectNative(plan.Scope, offer with { Hardware = "zz" }), "Malformed durable native hardware accepted");
            check(store.NativeProtected(plan.Scope, declined, quarantine.Client) && store.NativeProtected(plan.Scope, offered, "other") && !store.NativeProtected(plan.Scope, offered, offer.Client), "Durable pre-plan native protections are incorrect");
            reject(() => store.AddPlan(plan), "Lease-free bootstrap ignores durable native protections");
            reject(() => store.Merge(JsonSerializer.SerializeToUtf8Bytes(new AllocationState(1, [plan], []), DistributedAllocation.JsonOptions), true), "Old ordinary mesh plan ignores pre-plan native protections");
            store.ReleaseNative(plan.Scope, declined, quarantine.Client);
            check(store.NativeProtected(plan.Scope, declined), "Native RELEASE removes DECLINE quarantine");
            store.ReleaseNative(plan.Scope, offered, offer.Client);
            check(!store.NativeProtected(plan.Scope, offered), "Native RELEASE retains an unnecessary offer hold");
        }
        string missingBarrier = Path.Combine(folder, "native-missing-barrier.json"); File.Copy(nativePath, missingBarrier);
        reject(() => new DistributedAllocation(missingBarrier), "Pre-plan native protections resume without watermark");
        await using (var store = new DistributedAllocation(nativePath) { NodeName = () => "a" }) check(store.NativeProtected(plan.Scope, declined), "Restart discards native DECLINE protection");
        string monotonicPath = Path.Combine(folder, "native-monotonic.json");
        await using (var store = new DistributedAllocation(monotonicPath) { NodeName = () => "a" })
        {
            store.ProtectNative(plan.Scope, lease);
            store.ProtectNativeOffers(plan.Scope, [lease with { State = "Offer", Host = "stale-offer.example", Expires = now.AddMinutes(1) }]);
            check(store.NativeHolds(plan.Scope).Single() == lease, "Captured short offer downgrades acknowledged lease identity or lifetime");
            store.ProtectNative(plan.Scope, lease with { State = "Offer", Hardware = "02:01", Host = null, Expires = now.AddMinutes(1) });
            check(store.NativeHolds(plan.Scope).Single() == lease, "Direct short offer downgrades acknowledged lease identity or lifetime");
            store.ProtectNativeOffers(plan.Scope, [lease with { State = "Offer", Expires = expiry.AddHours(1) }]);
            check(store.NativeHolds(plan.Scope).Single().State == "Lease" && store.NativeHolds(plan.Scope).Single().Expires == expiry.AddHours(1), "Longer compatible offer fails to retain Lease state and maximum expiry");
            store.ProtectNative(plan.Scope, lease with { Expires = expiry.AddHours(-1) });
            check(store.NativeHolds(plan.Scope).Single().Expires == expiry.AddHours(1), "Compatible shorter lease renewal shortens protection");
            var protectedState = store.Export();
            reject(() => store.ProtectNative(plan.Scope, lease with { State = "Offer", Client = "1-0204" }), "Direct conflicting offer client overwrites lease");
            reject(() => store.ProtectNativeOffers(plan.Scope, [lease with { State = "Offer", Client = "1-0204" }]), "Captured conflicting offer client overwrites lease");
            reject(() => store.ProtectNative(plan.Scope, lease with { State = "Offer", Hardware = "02-04" }), "Direct conflicting offer hardware overwrites lease");
            reject(() => store.ProtectNativeOffers(plan.Scope, [lease with { State = "Offer", Hardware = "02-04" }]), "Captured conflicting offer hardware overwrites lease");
            reject(() => store.ProtectNative(plan.Scope, lease with { Hardware = "02-04" }), "Conflicting lease hardware overwrites acknowledged identity");
            reject(() => store.ProtectNativeOffers(plan.Scope, [offer, lease with { State = "Offer", Hardware = "02-04" }]), "Conflicting offer batch commits partial native protection");
            check(store.Export().SequenceEqual(protectedState), "Rejected evidence changes durable native protections");
            store.ProtectNative(plan.Scope, quarantine);
            store.ProtectNativeOffers(plan.Scope, [quarantine with { State = "Offer", Expires = now.AddMinutes(1) }]);
            check(store.NativeHolds(plan.Scope).Single(entry => entry.Address == declined) == quarantine, "Capture shortens or removes DECLINE quarantine");
            reject(() => store.ProtectNative(plan.Scope, quarantine with { State = "Offer" }), "Direct offer is permitted over active quarantine");
            reject(() => store.ProtectNativeOffers(plan.Scope, [quarantine with { State = "Offer", Hardware = "02-04" }]), "Quarantine silently ignores incompatible captured hardware");
        }
        await using (var store = new DistributedAllocation(monotonicPath) { NodeName = () => "a" })
        {
            var restored = store.NativeHolds(plan.Scope).Single(entry => entry.Address == leased);
            check(restored.State == "Lease" && restored.Expires == expiry.AddHours(1) && restored.Client == lease.Client && restored.Hardware == lease.Hardware && restored.Host == lease.Host, "Restart loses combined acknowledged identity or maximum lifetime");
        }
    }
    public static async Task CrashFixture(string path, string phase, string expires)
    {
        DateTime expiry = DateTime.Parse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        string id = "00000000000000000000000000000042";
        var plan = new AllocationPlan("crash", id, AllocationSequence.Number(IPAddress.Parse("203.0.113.10")), AllocationSequence.Number(IPAddress.Parse("203.0.113.50")), id, ["a", "b"], [], Configuration: "AQ==");
        uint address = new AllocationSequence(plan).Candidates("b").First();
        AllocationEntry lease = new(address, "1-0201", "02-01", "crash.example", "Lease", expiry);
        await using var source = new DistributedAllocation(path) { NodeName = () => "a" };
        if (phase == "capture")
        {
            string config = Path.Combine(Path.GetDirectoryName(path), "capture-" + Path.GetFileNameWithoutExtension(path));
            Directory.CreateDirectory(config);
            await using var log = new LogManager(true, config);
            using var server = new DhcpServer(config, log) { Allocation = source };
            using var scope = new Scope("crash", false, AllocationSequence.Address(plan.First), AllocationSequence.Address(plan.Last), IPAddress.Parse("255.255.255.0"), log, server)
            { DnsUpdates = false, LeaseTimeDays = 0, LeaseTimeHours = 1 };
            await server.AddScopeAsync(scope);
            scope.CommitLease(Lease.Recover(lease with { Expires = expiry.AddDays(-1) }, false)); server.SaveScope(scope.Name);
            File.WriteAllText(Path.Combine(config, "old-scope.sha256"), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(config, "crash.scope")))));
            scope.CommitLease(Lease.Recover(lease, false));
            var shortOffer = Lease.Recover(lease with { State = "Offer", Expires = DateTime.UtcNow.AddMinutes(1) }, false);
            var offers = (System.Collections.Concurrent.ConcurrentDictionary<DnsServerCore.Dhcp.Options.ClientIdentifierOption, Lease>)typeof(Scope).GetField("_offers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(scope);
            offers[shortOffer.ClientIdentifier] = shortOffer;
            string blockedRoot = Path.Combine(config, "blocked-scope-writes"); File.WriteAllText(blockedRoot, "Capture scope persistence fault");
            typeof(DhcpServer).GetField("_scopesFolder", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(server, blockedRoot);
            await scope.CaptureNativeAllocationAsync("a", CancellationToken.None, async snapshot =>
            {
                if (!snapshot.Entries.Any(entry => entry.State == "Lease" && entry.Expires == expiry) || !snapshot.Entries.Any(entry => entry.State == "Offer" && entry.Expires < expiry)) throw new Exception("Capture fixture lacks acknowledged lease and shorter offer");
                Console.WriteLine(); Console.WriteLine("READY"); Console.Out.Flush(); await Task.Delay(Timeout.Infinite);
            });
        }
        source.ProtectNative("crash", lease);
        if (phase != "native")
        {
            var input = DistributedAllocation.BuildBootstrap(id, "a", plan, [new("a", "crash", "AQ==", [lease]), new("b", "crash", "AQ==", [])]);
            source.PrepareBootstrap(input);
            await using var peer = new DistributedAllocation(path + ".peer") { NodeName = () => "b" };
            peer.PrepareBootstrap(input); source.AcknowledgeBootstrap("crash", id, source.Bootstrap("crash").Hash, "b");
            if (phase == "committed")
            {
                peer.CommitBootstrap("crash", id, peer.Bootstrap("crash").Hash);
                source.CommitBootstrap("crash", id, source.Bootstrap("crash").Hash);
            }
            Console.WriteLine("READY"); Console.Out.Flush();
            await Task.Delay(Timeout.Infinite);
        }
        Console.WriteLine("READY"); Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
    }
    static async Task CheckCrashBoundaries(string folder, Action<bool, string> check)
    {
        DateTime expiry = DateTime.UtcNow.AddDays(2);
        foreach (string phase in new[] { "native", "capture", "prepared", "committed" })
        {
            string path = Path.Combine(folder, "killed-" + phase + ".json");
            var launch = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            launch.ArgumentList.Add(typeof(DistributedDhcpChecks).Assembly.Location); launch.ArgumentList.Add("--bootstrap-crash"); launch.ArgumentList.Add(path); launch.ArgumentList.Add(phase); launch.ArgumentList.Add(expiry.ToString("O"));
            using var process = Process.Start(launch) ?? throw new Exception("Crash fixture did not start");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string output;
                do { output = await process.StandardOutput.ReadLineAsync(deadline.Token); } while (output is not null && output != "READY");
                check(output == "READY", "Crash fixture did not reach durable " + phase + " boundary");
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await using var recovered = new DistributedAllocation(path) { NodeName = () => "a" };
            check(recovered.NativeHolds("crash").Single().Expires == expiry, "Abrupt process death loses native acknowledged expiry");
            if (phase == "capture")
            {
                check(recovered.NativeHolds("crash").Single().State == "Lease" && recovered.Plan("crash") is null, "Interrupted capture downgrades acknowledged lease to an offer");
                string config = Path.Combine(folder, "capture-" + Path.GetFileNameWithoutExtension(path));
                await using var log = new LogManager(true, config);
                using var server = new DhcpServer(config, log) { Allocation = recovered };
                using var original = File.OpenRead(Path.Combine(config, "crash.scope"));
                using var restored = new Scope(original, log, server);
                var serializedOldExpiry = DateTime.UnixEpoch.AddMilliseconds(Convert.ToInt64((expiry.AddDays(-1) - DateTime.UnixEpoch).TotalMilliseconds));
                check(restored.Leases.Single().Value.LeaseExpires == serializedOldExpiry, "Failed capture overwrote the deliberately old native scope snapshot");
                check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(config, "crash.scope")))) == File.ReadAllText(Path.Combine(config, "old-scope.sha256")), "Interrupted capture changed bytes of the older native scope file");
                original.Dispose();
                await server.AddScopeAsync(restored);
                var binding = restored.Leases.Single().Value;
                check(binding.LeaseExpires == expiry && binding.ClientIdentifier.ToString() == "1-0201" && AllocationSequence.Number(binding.Address) == recovered.NativeHolds("crash").Single().Address, "Restart from older scope snapshot loses last acknowledged client/address/expiry");
            }
            else if (phase == "native") check(recovered.Plan("crash") is null && recovered.NativeProtected("crash", recovered.NativeHolds("crash").Single().Address), "Abrupt native restart drops protection before any plan");
            else if (phase == "prepared") check(recovered.BootstrapPending("crash") && !recovered.HasAuthority("crash", recovered.NativeHolds("crash").Single().Address), "Abrupt prepared restart enables serving authority");
            else check(recovered.FindLease("crash", "1-0201")?.Expires == expiry && recovered.HasAuthority("crash", recovered.NativeHolds("crash").Single().Address), "Abrupt committed restart loses acknowledged lease or inherited authority");
        }
    }
}
