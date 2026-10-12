# DHCP synchronization implementation and target design

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

`DistributedAllocation` and the actual `Scope` offer/commit/release paths now implement the first allocation protocol. Stable, sorted owner identities select balanced scattered sequences using an invertible affine permutation of eligible address ranks. Exclusions are merged arithmetically; even a full IPv4 range need not be materialized. Configured reservations have deterministic individual ownership and are excluded from dynamic sequences, including reservation-only pools. Offers are durably held for the existing sixty-second offer window; full lease lifetimes are persisted before ACK. Outstanding offers recover after restart. A client selecting another server releases the unused offer and suppresses that transaction's delayed offers. Mutations carry per-owner sequences and before/after lease history, retaining the last 2000 changes per scope and owner.

Each store holds an exclusive process file lock. A separate flushed `dhcp-allocation.json.required` watermark binds the local node, immutable plan, sequence and content hash. It is written before snapshot replacement, so an interrupted write or an older snapshot cannot silently resume allocation. Preserve this watermark with the node's allocation state during offline recovery. Missing state/watermarks and conflicting or newer peer evidence stop service. Copying a whole old node directory to another active machine remains outside this protection: distinct physical servers must have distinct enrolled identities, and recovery must fence the old instance. A live restore never blindly replaces local authoritative allocation state with a replica.

Bootstrap requires disabled scopes with no active leases or offers. The coordinator prepares the same immutable epoch on every enrolled node; a failed bootstrap retains that epoch for retry. Missing scopes are created from a disabled, lease-free configuration snapshot. Ownership maps, ranges, exclusions and reservations cannot be changed in place. Scope drift withholds service. The existing cluster transfer still passes `scopes: false`; the new `dhcp-allocation.json` state is separate from legacy scope files and participates in DNS-settings backup/restore and configuration transfer. Replication workers also exchange bounded snapshots directly with all enrolled peers over HTTPS every five seconds and retry after failed links. Receivers use owner sequences to reject conflicting versions and ignore older snapshots; they do not replay an older full snapshot over newer authoritative state.

After exhausting its own sequence, an owner uses previously granted addresses and then requests capacity from reachable donors. The donor checks its local leases/offers, durably holds the address, and returns its grant snapshot before the borrower can persist an offer. Donor grants do not expire autonomously and cannot be allocated by the donor after restart. Grant return and capacity reclamation remain unimplemented; repeated lost responses can consume capacity conservatively. New borrowing needs donor contact, while existing grants remain usable during isolation.

Optional ordered HA pairs define local primary, local secondary and then regional recovery response preferences. The first primary adds no delay, its secondary adds 500 ms, and subsequent pairs add 1500 ms per group position. Unpaired recovery owners follow the configured pairs. Every serving node retains independent address ownership; a nonowner remains a replica. This provides preference and new-allocation continuity, not lease-preserving takeover of an unreachable owner's addresses. A server remains silent for DHCP REQUESTs concerning another owner's address rather than sending an incorrect NAK. Client DECLINE observations quarantine an owned address for ten minutes and can publish shared security context.

The controller exposes `/api/ddi/dhcp/state/get`, POST `/plan/set`, POST `/prepare`, POST `/state/ingest`, POST `/grant`, and `/history/lookup` under the same prefix. User operations use DHCP permissions. Preparation, ingestion and grants require direct HTTPS cluster sessions; proxied user sessions cannot mutate replication state. Snapshot payloads are capped at 8 MiB. The existing shared cluster token trusts enrolled members; cryptographic per-node attribution is still required for stronger member isolation.

`--dhcp` tests exercise the real offer, ACK, overflow and nonowner request paths, three-owner partitions, restart, stale snapshots, durable watermarks, process exclusion and historical attribution. `--transport` starts three isolated servers and exchanges state through actual HTTPS endpoints using pinned lab certificates; it tests bootstrap, link failure, reconnect, grants and fresh-session privileges. Membership is seeded by the fixture. It does not validate upstream enrollment/DANE, physical fencing, an end-to-end DHCP relay or broadcast deployment, regional WAN behavior, clock jumps, full-directory clones, or production throughput.

The primary continuously publishes content-hashed, revisioned scope option snapshots, and replicas reconcile those options while retaining local listener enablement and interface selection. The snapshots exclude active leases. Node-local DNS address selection is recomputed when the shared policy says to use that node as the resolver. Local option drift is reconciled even at the same desired revision. Configuration that would change allocation eligibility is rejected rather than shifting existing ownership. Reachable secondary peers still exchange lease observations when the configuration primary is unavailable. Renewals retain at least the previously acknowledged allocation lifetime when configuration shortens the new lease duration, protecting clients that might miss the renewal ACK.

Remaining work includes reservation/range migration, consensus membership and ownership changes, safe grant return, lease-preserving failover and DHCPv6. Snapshot JSON rewrites and bounded history are an initial implementation rather than enterprise transactional storage. Fencing integration must be selected before automatic takeover is enabled. Static infrastructure addresses must be explicitly excluded from managed dynamic pools; discovery is not a substitute for those exclusions.

The sections below describe the target protocol beyond this first immutable-ownership implementation. Management API operations that change reservations, lease type, exclusions or membership still need the migration protocol; changing those values locally is not a supported distributed reconfiguration workflow.

## Replicated configuration

Replicate scope identity, network view and site, address ranges, subnet masks, exclusions, reservations, lease duration, DHCP options, and DNS update policy. Keep interface selection, listener addresses, server identity, and allocation role local to the destination node.

Each publication carries a monotonically increasing revision and content hash. Validate a revision before activation, apply it atomically, and acknowledge its status. A standby must not start serving simply because the primary scope is enabled. Deleting or renaming a scope needs a replicated tombstone or stable identifier so an offline node cannot resurrect obsolete configuration.

## Replicated allocation state

The preferred allocation mode uses exclusive address partitions and local durable journals. An owner can continue allocating and renewing within its partition when replication is interrupted, provided it retains its authoritative local state. Configuration publication and ownership changes use cluster coordination; individual leases do not require a WAN round trip. A node that loses its local allocation journal cannot infer availability from an older replica and must recover before serving.

Define durable events for offers or allocation holds, commits, renewals, releases, declines, expiration, reservation changes, and administrative removals. Include scope identity, network view, allocation epoch, sequence number, client identifier, hardware address, IP, hostname, event time, and absolute lease expiry where applicable.

Use a persisted journal and acknowledgements. Consumers must tolerate duplicate delivery, detect missing sequences, and reject updates from obsolete allocation epochs. Snapshot transfer includes its journal watermark; replay only subsequent events. Recovery must be resumable and detect corrupt or incomplete snapshots.

Persist offers and lease mutations locally before advertising or acknowledging them, then replicate the journal to peers. The management interface must distinguish locally durable state from peer acknowledged state. Delayed replication is safe for independent allocations because partitions do not overlap; it does not authorize takeover of a partition whose latest lease state is unknown. A future shared pool mode would instead require a consensus commitment for each allocation and renewal before acknowledgement.

## Automatic balanced allocation sequences

For each subnet, count the explicitly assigned allocation owners, not every cluster replica and not the servers currently reachable. Local HA members and regional recovery servers may participate according to the subnet policy. A recovery server that must allocate independently during isolation needs its own exclusive partition; a replica without a partition remains standby until safe ownership transfer.

Build an ordered list `P` of eligible dynamic addresses after removing exclusions, infrastructure addresses, and reservations. Let `A` be its length, `N` the configured number of allocation owners, and `s` the owner's zero based index after sorting by stable node identity. Generate candidates with `IP(s, k) = P[permutation(s + k * N)]`, using nonnegative values of `k` for which `s + k * N < A`. The shared permutation must be a bijection of indexes `0` through `A - 1`, persisted with the pool revision and shuffle seed; a hash modulo `A` is not sufficient. These disjoint sequences scatter addresses across the pool while differing in size by at most one. Reject zero owners and more allocation owners than eligible addresses.

For 100 eligible addresses and three owners, input sequences are `0, 3, 6, ...`, `1, 4, 7, ...`, and `2, 5, 8, ...`, containing 34, 33, and 33 addresses. The shared permutation spreads each sequence throughout the subnet rather than allocating contiguous blocks. Reservations have separate explicit ownership and never enter the dynamic candidate sequences.

A server must exhaust available candidates in its own sequence before requesting capacity from another sequence. An unavailable peer is not evidence that its addresses are free. Overflow requires an atomic coordinated claim that accounts for the original owner's authoritative leases and outstanding offers. The original owner must durably hold the address and acknowledge that it will not allocate it before the borrower receives authority to offer or lease it. Commit and persist a unique grant identifying the address, donor, borrower, and ownership revision. Missing acknowledgements or ambiguous outcomes keep the address unavailable until recovery resolves the claim.

After a grant, both owner and borrower exclude the address from independent candidate selection; only the borrower can serve its granted lease. Persist grants across restart and replicate them to all nodes. Returning an address to its original sequence requires a coordinated release and protection of outstanding leases; a local grant timer must not make an address reusable while a borrower can still renew it. A borrower may continue serving a previously granted address during disconnection according to its persisted authority. It cannot obtain new overflow grants while the required coordination is unavailable.

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
17. Verify that overflow begins only after the owner's sequence has no available addresses. Race multiple borrowers against the original owner for the same candidate; at most one allocation authority succeeds. Drop messages and restart participants at every grant stage to verify durable holds, safe recovery, and eventual convergence.

## Delivery boundary

This design establishes the required behavior. Runtime configuration replication, lease journaling, ownership enforcement, node recovery, and failover tests still need implementation. Scope file backup or a successful build does not satisfy these requirements.
