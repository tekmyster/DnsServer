# DDI delivery status

This is a developing open-source DDI fork. Features have no paid software tier, but a working feature is not a claim of production parity or superior detection accuracy versus Infoblox, EfficientIP or BlueCat. The table separates current functionality from the remaining product requirements.

| Requested capability | Current behavior | Remaining requirements |
| --- | --- | --- |
| Technitium fork and baseline | Upstream engines retained; repeatable Release build; fork and draft PR | Packaging, upgrades, compatibility and production qualification |
| Multi-server DHCP allocation | Balanced disjoint scattered sequences, stable identities, exclusions, initial reservations, durable offers/ACKs | Live membership/range/reservation migration and controller consensus |
| More than two synchronized servers | All enrolled peers exchange sequenced snapshots over HTTPS; revisioned scope options reconcile from the primary; reachable peers continue during coordinator outage; three live local servers validated | Regional deployment, enrollment/DANE and scale qualification |
| Disconnected service | Owners use their unchanged sequences and previously granted capacity | Safe recovery tooling, clock-bound qualification and physical fencing |
| Overflow after own sequence | Donor checks authoritative state and persists a permanent exclusive grant before borrower offers | Grant return/reclamation and bounded idempotent retry across unknown HTTP outcomes |
| Local primary/secondary and regional recovery | Ordered HA pairs control response preference; connected owners can atomically surrender selected addresses with preserved leases, offers and quarantine; retries distinguish surrender from receiver persistence | Unreachable-owner takeover, fencing provider, automatic role control, whole-scope handoff and grant return |
| Shared DNS/DHCP security awareness | IoCs and behavioral/decline observations create shared expiring device evidence; IPAM exposes findings | Historical-event backfill, per-node signed evidence and finer security roles |
| True IPAM | IPv4/IPv6 views, containers, assignments, metadata, utilization and reconciliation | Atomic service provisioning, discovery, subnet/prefix allocation, workflow, scoped governance and enterprise database |
| NetBox relationship | DDI-owned prefix/address publication, explicit VRF mapping, previews, before/after review, provenance, ETag guards and unknown-outcome reconciliation | Reverse import, object/site authority, DCIM, VLAN/site/tenant/interface mappings, scheduler and deletion reconciliation |
| Threat intelligence | Local exact/suffix/network rules and asynchronous bounded MISP cache; URL-path evidence retained | General feed schedulers, RPZ interoperability, sharing policy and richer source quality controls |
| Exfiltration risk | Explainable, uncalibrated short-window monitor signals | Representative benign/malicious datasets, baselines, long-window models, DGA and production calibration |
| Security operations | Persisted bounded timeline search, analyst dispositions, JSON export and policy rollback | SIEM streaming, external immutable audit, long retention, richer investigations and response integrations |
| DNS capabilities | Existing authoritative/recursive DNS, DNSSEC and encrypted transports retained | New-policy transport/performance matrix, sinkholes and SVCB/HTTPS hints |
| IPv6 DDI | IPv6 IPAM, DNS and indicator matching | DHCPv6 service and synchronized allocation |
| Product UX | Project-wide contract, responsive console/dialogs, accessible controls and feedback, theme contrast, structured DNS/security evidence and progressive disclosure; all primary sections audited at desktop and narrow widths | Continued quality gates for new workflows and deployment-specific/third-party application screens |

Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-ddi.ps1` after building the pinned sibling library dependencies. The test suite covers real DHCP offer/ACK hooks, three-owner partitions, three native HTTPS servers, API permission boundaries, fresh administrative sessions, MISP/NetBox mocks and UI read workflows. It does not establish enterprise throughput, geographically distributed failover, or production threat-detection accuracy. Administrative credentials and access controls on production servers have not been changed. Fresh-session tests exercised loopback; other authorized source paths remain untested.

Automatic ownership takeover depends on a configured fencing platform. Planned handoff requires the current owner's durable surrender; an unreachable owner still requires manual recovery and verified fencing.

The [two-VM local Hyper-V lab](HYPERV-LAB.md) additionally exercised native Linux cluster enrollment and peer certificate handling, synchronized UDP/TCP DNS, real broadcast DHCP OFFER/ACK exchanges, shared lease-attributed security signals, IPAM inventory synchronization, and service-outage/restart recovery. Both services remain available for local testing. This does not qualify simultaneous network partitions, automatic fenced takeover, WAN deployments, or production scale.
