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

Define durable events for offers or allocation holds, commits, renewals, releases, declines, expiration, reservation changes, and administrative removals. Include scope identity, network view, allocation epoch, sequence number, client identifier, hardware address, IP, hostname, event time, and absolute lease expiry where applicable.

Use a persisted journal and acknowledgements. Consumers must tolerate duplicate delivery, detect missing sequences, and reject updates from obsolete allocation epochs. Snapshot transfer includes its journal watermark; replay only subsequent events. Recovery must be resumable and detect corrupt or incomplete snapshots.

In the initial coordinated mode, commit an allocation through consensus and durably persist it on a majority of voters before returning DHCPACK. This boundary is required for allocation changes and renewals; waiting for every replica would unnecessarily stop service whenever any node is unavailable. Remaining replicas catch up from the committed journal. Offers need committed allocation holds before advertising an address so a subsequent owner cannot offer it to another client. A node unable to establish safe ownership or complete the required durability step must withhold allocation and renewal acknowledgements. Any future degraded service mode needs a defined lease lifetime and takeover safety contract.

## Ownership and failover

Start with one active allocator per scope and any number of synchronized standby nodes. Different nodes can own different scopes concurrently, allowing multiple servers to serve clients without allowing independent allocation from the same scope. One cluster configuration leader does not necessarily need to own allocation for every site. Prefer explicit scope placement and ownership to assumptions based on DNS primary or secondary roles.

Scope ownership and its epoch must be committed in the same consensus state machine that validates address allocation. Validate the owner, epoch, client, and address uniqueness when applying each mutation. A serving node can send a lease acknowledgement only after that specific mutation is committed. This prevents an isolated former owner from issuing new leases or extending existing ones from stale local state. Heartbeats or a locally remembered owner flag are insufficient authorization to allocate.

Automatic takeover, if selected, requires a committed ownership transfer to an eligible synchronized node while a voting majority remains reachable. A minority partition cannot elect an allocator or authorize allocation mutations. DNS can continue resolving using its local configuration during loss of DHCP quorum; existing client leases retain their original expiry, but coordinated DHCP acknowledgements are withheld. For manual takeover, use the same committed ownership mechanism. Forced disaster recovery without a majority is a separate procedure requiring isolation of the old owners and conservative treatment of outstanding leases.

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
5. A partition never allows conflicting allocations for the same scope. Test three voters split 2 to 1 and five voters split 3 to 2; minority nodes cannot acknowledge new or renewed leases.
6. A stale or partially restored node cannot serve until its role and state are validated. Disk full, journal corruption, and interrupted snapshots produce explicit degraded health.
7. Direct and relayed DHCP discover, request, renew, and rebind flows work under the supported ownership model; associated DNS updates remain consistent.
8. Replication lag, lease latency, allocation throughput, and event loss are measured. Lease acknowledgement proves that the mutation was committed and durably replicated to a voting majority.
9. Existing standalone DHCP behavior remains compatible when replication is disabled.
10. Run synchronization tests with three and five servers and with five voters plus additional nonvoting replicas. Every replica converges after disconnect and reconnect; node counts are configuration rather than hardcoded pairs.
11. Add, promote, replace, and remove nodes while lease traffic continues. Replayed membership messages cannot create independent voting majorities or reactivate an obsolete owner.
12. A three voter cluster tolerates one unavailable voter and a five voter cluster tolerates two, provided the remaining majority and an assigned serving node remain connected. Losing the majority prevents allocation acknowledgements without interrupting independent DNS resolution.
13. Run different scopes on different owners concurrently. Moving a scope does not duplicate an address or alter ownership of other scopes; a nonvoting service node uses the same committed allocation checks as a voter.

## Delivery boundary

This design establishes the required behavior. Runtime configuration replication, lease journaling, ownership enforcement, node recovery, and failover tests still need implementation. Scope file backup or a successful build does not satisfy these requirements.
