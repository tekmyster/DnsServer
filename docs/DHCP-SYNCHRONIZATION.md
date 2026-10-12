# DHCP synchronization design

DHCP configuration and lease state must synchronize across clusters of three or more DDI servers alongside DNS. The protocol and management model must support a configurable number of nodes, rather than a fixed failover pair. Operators should see one managed service with per node applied revisions, replication lag, and health. Allocation ownership requires stronger coordination than periodically copying DNS configuration.

## Cluster topology

Separate cluster membership, voting membership, replicated state, and permission to serve DHCP. Every enrolled node receives the shared configuration and, by default, the committed lease state for all cluster scopes. A node can retain synchronized state without being assigned to serve a particular site. Future selective replication requires an explicit scope replica group and recovery contract; it must not silently weaken this default.

Use a consensus group with three or five voting nodes initially, plus additional nonvoting replicas when more service nodes are needed. Both voters and nonvoting replicas can be DHCP service nodes if their scope assignment and current ownership permit it. Nonvoting replicas receive the committed journal and snapshots, but their acknowledgements do not count toward quorum. The cluster leader coordinates state changes; it does not need to receive client DHCP broadcasts directly.

| Voting nodes | Majority required | Unavailable voters tolerated while a majority remains connected |
| --- | --- | --- |
| 3 | 2 | 1 |
| 5 | 3 | 2 |

These counts describe coordination availability, not client service reachability. A working quorum cannot replace a missing relay route or an unreachable serving node. Additional nonvoting replicas increase placement options without increasing voting fault tolerance. An even number of voters is supported by majority rules, but odd counts avoid an extra voter without a corresponding increase in failure tolerance.

Membership changes use a consensus protocol with safe configuration transitions, such as joint consensus. Add new nodes as learners, complete snapshot and journal catchup, then promote voters through the committed membership process. Removal or replacement of a voter must not create disjoint majorities. Do not implement membership by independently editing node lists on each server.

## Current implementation

`ClusterManager.TransferConfigAsync` and `ClusterManager.SyncConfigFromAsync` explicitly pass `scopes: false` to configuration backup and restore. DHCP scope and lease handling is local to `DhcpServer` and `Scope`. `Scope.WriteTo` serializes configuration, reservations, and active leases together, making the existing scope file unsuitable as an independent configuration replication contract.

`Scope.GetOfferAsync`, `CommitLease`, `ReleaseLease`, and `RemoveExpiredLeases` are integration points for allocation and lease lifecycle events. Management API operations also change reservations, lease type, and exclusions; those changes must participate in synchronization rather than being captured only at DHCP packet ingress.

## Replicated configuration

Replicate scope identity, network view and site, address ranges, subnet masks, exclusions, reservations, lease duration, DHCP options, and DNS update policy. Keep interface selection, listener addresses, server identity, and allocation role local to the destination node.

Each publication carries a monotonically increasing revision and content hash. Validate a revision before activation, apply it atomically, and acknowledge its status. A standby must not start serving simply because the primary scope is enabled. Deleting or renaming a scope needs a replicated tombstone or stable identifier so an offline node cannot resurrect obsolete configuration.

## Replicated allocation state

The preferred allocation mode uses exclusive address partitions and local durable journals. An owner can continue allocating and renewing within its partition when replication is interrupted, provided it retains its authoritative local state. Configuration publication and ownership changes use cluster coordination; individual leases do not require a WAN round trip. A node that loses its local allocation journal cannot infer availability from an older replica and must recover before serving.

Define durable events for offers or allocation holds, commits, renewals, releases, declines, expiration, reservation changes, and administrative removals. Include scope identity, network view, allocation epoch, sequence number, client identifier, hardware address, IP, hostname, event time, and absolute lease expiry where applicable.

Use a persisted journal and acknowledgements. Consumers must tolerate duplicate delivery, detect missing sequences, and reject updates from obsolete allocation epochs. Snapshot transfer includes its journal watermark; replay only subsequent events. Recovery must be resumable and detect corrupt or incomplete snapshots.

Persist offers and lease mutations locally before advertising or acknowledging them, then replicate the journal to peers. The management interface must distinguish locally durable state from peer acknowledged state. Delayed replication is safe for independent allocations because partitions do not overlap; it does not authorize takeover of a partition whose latest lease state is unknown. A future shared pool mode would instead require a consensus commitment for each allocation and renewal before acknowledgement.

## Automatic balanced address partitioning

For each subnet, count the explicitly assigned allocation owners, not every cluster replica and not the servers currently reachable. Local HA members and regional recovery servers may participate according to the subnet policy. A recovery server that must allocate independently during isolation needs its own exclusive partition; a replica without a partition remains standby until safe ownership transfer.

Build an ordered list of eligible dynamic addresses after removing exclusions, infrastructure addresses, and reservations. For `A` addresses and `N` assigned owners, calculate `q = floor(A / N)` and `r = A mod N`. Sort owners by stable node identity. Owner index `i`, starting at zero, receives `q + 1` addresses when `i < r`, and `q` otherwise. Its starting list offset is `i * q + min(i, r)`. Partitions differ in size by at most one address and cover the dynamic pool exactly once. Reject zero owners and more allocation owners than eligible addresses.

For a pool of `10.20.0.100` through `10.20.0.199` with no exclusions, three owners receive `.100` through `.133`, `.134` through `.166`, and `.167` through `.199`: 34, 33, and 33 addresses. Random selection is permitted within each owner's assigned partition. Reservations have separate explicit ownership and never enter the random dynamic pool.

Persist and replicate the resulting ownership map with a revision. Disconnected nodes continue using the same map. A failed heartbeat never reduces `N` or triggers autonomous repartitioning. Adding or removing an allocation owner generates a proposed balanced map, but activates changed addresses only after a safe handoff. Existing leases and offers remain protected while the old owner drains or transfers its state. Temporary imbalance during migration is preferable to duplicate leases.

Changing pool ranges, exclusions, or reservations also requires this migration procedure when ownership or address eligibility changes. Replacing the ordered eligible address list independently on each node would otherwise shift boundaries and create overlap. When a former owner cannot be reached, fence it and recover authoritative state or wait a conservatively bounded outstanding lease lifetime before reusing its addresses. The lease lifetime bound must account for renewals and offers made during isolation, not just the last replicated expiry.

## Ownership and failover

Allow multiple allocation owners per scope, each with an exclusive address partition, plus synchronized standby nodes. Organize service nodes into local HA groups with primary and secondary preference and ordered regional recovery targets. Serving preference determines who normally responds; partition ownership determines which addresses a node can allocate safely. Prefer explicit scope placement and ownership to assumptions based on DNS primary or secondary roles.

Partition ownership revisions must be committed through cluster coordination. A committed revision alone cannot revoke an isolated server's old map while that server is permitted to continue operating locally. Ownership transfer therefore also requires confirmed relinquishment or external fencing of the former owner, plus protection of its outstanding leases. Validate each local allocation against the active map and durable lease journal. Heartbeats or a newly elected coordinator alone cannot authorize reuse of another owner's partition.

Automatic takeover, if selected, requires coordinated ownership transfer, fencing or confirmed relinquishment, and validated lease recovery. During a link failure, each node may continue using its own partition, but no node can claim another partition based on silence. A minority partition cannot publish a new ownership map. DNS continues resolving using local configuration. Forced disaster recovery without a majority is a separate procedure requiring isolation of the old owners and conservative treatment of outstanding leases.

A recovering node first joins as standby, catches up its snapshot and journal, and proves it has the current allocation epoch before becoming eligible to serve. An outdated node must not reactivate scopes automatically after restart. Ownership transfer must account for outstanding offers and the latest committed lease expiries.

DHCP renew, rebind, DHCP server identifier, relay routing, direct broadcast reachability, and DNS update ownership need an explicit deployment design. Decide whether takeover preserves a service address or relies on client rebind behavior. Test both relayed and directly connected clients when both paths are supported.

## Management and security

Authenticate replication through node identities and encrypt transport. Apply dedicated replication permissions and validate messages independently of transport authentication. Replay protection uses scope epoch and sequence state stored durably. Bound journal retention, snapshot size, replay work, and replication queues.

Show desired and applied configuration revisions, last durable lease sequence, acknowledgement lag, active owner, ownership epoch, voter and replica roles, quorum health, and recovery progress for every node. Audit policy changes and ownership transfers. If enrollment or integration requires ACL or credential changes, retain the original administrative session until a fresh session verifies authentication, privileges, and required authorized paths.

## Acceptance requirements

1. A scope, option, reservation, exclusion, rename, and deletion appear consistently on every assigned node, while local listener settings remain correct.
2. Offers, commits, renewals, releases, declines, expirations, and administrative changes survive owner restart and converge after peer reconnect.
3. Duplicate, out of order, and missing events cannot resurrect a released lease or allocate an already assigned address.
4. Concurrent clients receive distinct addresses during normal operation, takeover, and recovery. Outstanding offers participate in this check.
5. A network partition never allows conflicting allocations for the same scope. Test three voters split 2 to 1 and five voters split 3 to 2; isolated owners can allocate only within their unchanged exclusive partitions and cannot publish new ownership.
6. A stale or partially restored node cannot serve until its role and state are validated. Disk full, journal corruption, and interrupted snapshots produce explicit degraded health.
7. Direct and relayed DHCP discover, request, renew, and rebind flows work under the supported ownership model; associated DNS updates remain consistent.
8. Replication lag, lease latency, allocation throughput, and event loss are measured. Lease acknowledgement proves local durability; peer acknowledgement and lag are reported separately.
9. Existing standalone DHCP behavior remains compatible when replication is disabled.
10. Run synchronization tests with three and five servers and with five voters plus additional nonvoting replicas. Every replica converges after disconnect and reconnect; node counts are configuration rather than hardcoded pairs.
11. Add, promote, replace, and remove nodes while lease traffic continues. Replayed membership messages cannot create independent voting majorities or reactivate an obsolete owner.
12. A three voter cluster can coordinate changes with one unavailable voter and a five voter cluster with two, provided the remaining majority is connected. Loss of majority prevents ownership changes but does not stop healthy owners serving their established partitions.
13. Run multiple owners within one scope and across different scopes. Moving a partition does not duplicate an address or alter ownership of unrelated partitions; voter and nonvoting service nodes enforce the same address ownership rules.
14. For varying pool sizes and owner counts, all eligible addresses have exactly one owner and partition sizes differ by at most one. Include exclusions, reservations, uneven division, and invalid owner counts.
15. Drop every replication message while owners allocate and renew concurrently. No owner issues an address from another owner's partition. Restore communication and verify journal convergence.
16. Change allocation membership while a former owner is disconnected. The proposed map cannot activate overlapping ownership until that owner is fenced and outstanding leases are protected.

## Delivery boundary

This design establishes the required behavior. Runtime configuration replication, lease journaling, ownership enforcement, node recovery, and failover tests still need implementation. Scope file backup or a successful build does not satisfy these requirements.
