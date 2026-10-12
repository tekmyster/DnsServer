# DHCP synchronization design

DHCP configuration and lease state must synchronize across the DDI nodes alongside DNS. Operators should see one managed service with per node applied revisions, replication lag, and health. Allocation ownership requires stronger coordination than periodically copying DNS configuration.

## Current implementation

`ClusterManager.TransferConfigAsync` and `ClusterManager.SyncConfigFromAsync` explicitly pass `scopes: false` to configuration backup and restore. DHCP scope and lease handling is local to `DhcpServer` and `Scope`. `Scope.WriteTo` serializes configuration, reservations, and active leases together, making the existing scope file unsuitable as an independent configuration replication contract.

`Scope.GetOfferAsync`, `CommitLease`, `ReleaseLease`, and `RemoveExpiredLeases` are integration points for allocation and lease lifecycle events. Management API operations also change reservations, lease type, and exclusions; those changes must participate in synchronization rather than being captured only at DHCP packet ingress.

## Replicated configuration

Replicate scope identity, network view and site, address ranges, subnet masks, exclusions, reservations, lease duration, DHCP options, and DNS update policy. Keep interface selection, listener addresses, server identity, and allocation role local to the destination node.

Each publication carries a monotonically increasing revision and content hash. Validate a revision before activation, apply it atomically, and acknowledge its status. A standby must not start serving simply because the primary scope is enabled. Deleting or renaming a scope needs a replicated tombstone or stable identifier so an offline node cannot resurrect obsolete configuration.

## Replicated allocation state

Define durable events for offers or allocation holds, commits, renewals, releases, declines, expiration, reservation changes, and administrative removals. Include scope identity, network view, allocation epoch, sequence number, client identifier, hardware address, IP, hostname, event time, and absolute lease expiry where applicable.

Use a persisted journal and acknowledgements. Consumers must tolerate duplicate delivery, detect missing sequences, and reject updates from obsolete allocation epochs. Snapshot transfer includes its journal watermark; replay only subsequent events. Recovery must be resumable and detect corrupt or incomplete snapshots.

Decide the acknowledgement boundary explicitly. In the initial coordinated mode, persist and replicate an allocation commitment before returning DHCPACK. Offers need coordinated holds before advertising an address so a subsequent owner cannot offer it to another client. A node unable to establish safe ownership or complete the required durability step must withhold new allocations. Any future degraded service mode needs a defined lease lifetime and takeover safety contract.

## Ownership and failover

Start with one active allocator per scope and replicated standby state. One cluster configuration leader does not necessarily need to own allocation for every site. Prefer explicit scope placement and ownership to assumptions based on DNS primary or secondary roles.

Automatic takeover requires a quorum or external fencing mechanism that proves the previous allocator can no longer issue conflicting leases. A two node heartbeat alone cannot distinguish a failed peer from a partition. Without this mechanism, retain synchronized standby state and use a documented manual takeover procedure that fences the former owner and verifies replication completeness.

A recovering node first joins as standby, catches up its snapshot and journal, and proves it has the current allocation epoch before becoming eligible to serve. An outdated node must not reactivate scopes automatically after restart. Ownership transfer must account for outstanding offers and the latest committed lease expiries.

DHCP renew, rebind, DHCP server identifier, relay routing, direct broadcast reachability, and DNS update ownership need an explicit deployment design. Decide whether takeover preserves a service address or relies on client rebind behavior. Test both relayed and directly connected clients when both paths are supported.

## Management and security

Authenticate replication through node identities and encrypt transport. Apply dedicated replication permissions and validate messages independently of transport authentication. Replay protection uses scope epoch and sequence state stored durably. Bound journal retention, snapshot size, replay work, and replication queues.

Show desired and applied configuration revisions, last durable lease sequence, acknowledgement lag, active owner, fencing status, and recovery progress. Audit policy changes and ownership transfers. If enrollment or integration requires ACL or credential changes, retain the original administrative session until a fresh session verifies authentication, privileges, and required authorized paths.

## Acceptance requirements

1. A scope, option, reservation, exclusion, rename, and deletion appear consistently on every assigned node, while local listener settings remain correct.
2. Offers, commits, renewals, releases, declines, expirations, and administrative changes survive owner restart and converge after peer reconnect.
3. Duplicate, out of order, and missing events cannot resurrect a released lease or allocate an already assigned address.
4. Concurrent clients receive distinct addresses during normal operation, takeover, and recovery. Outstanding offers participate in this check.
5. A partition never allows two allocators to act as owner for the same scope. A two node deployment without fencing does not automatically promote a standby.
6. A stale or partially restored node cannot serve until its role and state are validated. Disk full, journal corruption, and interrupted snapshots produce explicit degraded health.
7. Direct and relayed DHCP discover, request, renew, and rebind flows work under the supported ownership model; associated DNS updates remain consistent.
8. Replication lag, lease latency, allocation throughput, and event loss are measured. Tests state whether lease acknowledgement guarantees local or peer durability.
9. Existing standalone DHCP behavior remains compatible when replication is disabled.

## Delivery boundary

This design establishes the required behavior. Runtime configuration replication, lease journaling, ownership enforcement, node recovery, and failover tests still need implementation. Scope file backup or a successful build does not satisfy these requirements.
