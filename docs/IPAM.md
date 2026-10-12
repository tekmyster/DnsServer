# IPAM source of truth and implementation

IPAM owns intended network and address configuration. DNS records, DHCP leases, reservations, discovery results, and external inventory are observations or explicitly owned integration objects. Reconciliation identifies disagreement without silently replacing intent.

## Implemented first slice

The module persists network views, IPv4 and IPv6 subnet and container prefixes, site and VLAN metadata, intended address assignments, and revisioned change history with before and after values. Independent network views allow overlapping private space. Subnets cannot overlap within a view unless a containing prefix is explicitly an organizational container. Containers cannot carry assignments or service links.

Assignments have hostname, owner, description, and Assigned, Reserved, or Deprecated state. Each address is unique within its view. Network and broadcast addresses are excluded for ordinary IPv4 subnets; /31 and /32 are supported. IPv6 capacity and address arithmetic use arbitrary precision, and available address suggestions do not enumerate large networks.

Explicit links connect a subnet to a local DHCP scope and DNS zone. Inspection combines current leases, reservations, and A/AAAA records with intended assignments, reporting unmanaged addresses, hostname drift, duplicate client lease evidence, and assignments without observations. Absence of DNS or DHCP evidence does not prove an address is free or a device is offline. DNS records do not prove a live connection.

Writes require an inventory revision and are serialized inside one process. Invalid or stale writes leave the previous state intact. File replacement follows a flushed temporary snapshot. Settings backups include `ipam.json`, and restore validates and loads that inventory. Cluster configuration transfer includes the same inventory, and API mutations are restricted to the existing cluster primary. Actual multi-node convergence and failover are not yet validated.

The web console includes an IPAM tab for inventory, creation and editing of subnets and assignments, address suggestions, observations, utilization, and recent changes. APIs reuse Settings view, modify, and delete permissions. Reading linked DHCP or DNS observations additionally requires the corresponding existing source permissions. Dedicated per-view delegation remains future work.

## API contract

Use the existing authenticated API transport and bearer session token. Mutations use POST form data. The usual API status envelope contains results under `response`.

| Endpoint | Method | Parameters |
| --- | --- | --- |
| `/api/ipam/list` | GET or POST | None |
| `/api/ipam/views/set` | POST | `revision`, `name`, optional `id`, `description` |
| `/api/ipam/subnets/set` | POST | `revision`, `name`, `prefix`, optional `id`, `viewId` defaulting to `default`, `site`, `vlan`, `container`, `dhcpScope`, `dnsZone` |
| `/api/ipam/assignments/set` | POST | `revision`, `subnetId`, `address`, optional `id`, `hostName`, `owner`, `status`, `description` |
| `/api/ipam/delete` | POST | `revision`, `kind` equal to `view`, `subnet`, or `assignment`, and `id` |
| `/api/ipam/subnets/inspect` | GET or POST | `subnetId` |
| `/api/ipam/addresses/nextAvailable` | GET or POST | `subnetId` |

Available address responses are suggestions, not atomic live DHCP claims. IPAM Reserved state records intended reservation; it does not create a DHCP reservation. This slice does not prevent another provisioning system or a DHCP allocator from using an intended address. Automated provisioning requires a separate coordinated workflow with allocation holds, service validation, acknowledgement, and rollback.

## NetBox relationship

NetBox models prefixes in VRFs, distinguishes container prefixes, and represents interface IPs with masks and roles. Preserve these semantics rather than flattening inventory into DNS records. Source ownership must be selected before a live connector writes to NetBox or imports intent that can alter service configuration.

Support explicit authority modes: this platform owns IPAM and exports to NetBox; NetBox owns intent and this platform provisions services; or ownership is assigned by object type and site. Under every mode, preserve stable external object IDs, source revision, last applied values, and provenance. Conflict detection must precede synchronization. Retry using idempotent operations and never resolve disagreement by blindly applying the most recent timestamp.

The connector needs a preview of creates, updates, deletions, and conflicts; dry run; bounded paginated API calls; TLS verification; protected credentials; and independent permissions. Prefix containers, sites, VLANs, VRFs, tenants, device interfaces, address masks, and roles need version-specific mappings. Live integration is not implemented in this slice.

Sources: [NetBox prefixes](https://netboxlabs.com/docs/netbox/models/ipam/prefix/), [NetBox VRFs](https://netboxlabs.com/docs/netbox/models/ipam/vrf/), and [NetBox addresses](https://netboxlabs.com/docs/netbox/models/ipam/ipaddress/).

## Commercial capability references and remaining work

All software capabilities are intended to ship in the open source edition without subscription gates. Use documented capabilities to set acceptance criteria, not to claim unmeasured feature parity or superior performance.

| Capability | Target behavior | Current status |
| --- | --- | --- |
| Address planning | Hierarchy, views, IPv4/IPv6, ownership, site/VLAN metadata, conflict validation | First slice implemented |
| Full network model | VRF identifiers, tenants, site hierarchy, VLAN objects/groups, devices/interfaces, ranges, roles, custom fields and tags | Pending |
| Utilization and reconciliation | Intended versus observed usage with provenance and drift findings | Local linked service observations implemented; historical ingestion and scheduled discovery pending |
| Address and prefix allocation | Atomic intent allocation, exclusion-aware available prefixes, live allocation holds | Address suggestions implemented; atomic provisioning and prefix allocation pending |
| DNS and DHCP orchestration | Validate, preview, deploy, acknowledge, compensate failed changes, and track applied revisions | Explicit read integration implemented; deployment workflow pending |
| Discovery | Passive lease events and approved active/cloud/device collectors with confidence and timestamps | Pending |
| Governance | Per-object RBAC, delegated views, approvals, scheduled changes, immutable external audit export | Existing Settings permission reuse and local change history implemented; delegation and workflow pending |
| Integration | NetBox source ownership, external IDs, diff preview, idempotency, conflict queues, webhooks | Contract defined; connector pending |
| Availability and scale | Transactional indexed storage, controlled multi-node ownership, recovery, pagination, load tests | Single-process snapshot store implemented; enterprise storage and availability pending |
| Migration | Preview imports, validation, reversible mappings, bulk APIs and vendor export adapters | Pending |

References: [Infoblox DDI](https://www.infoblox.com/products/ddi/) describes unified inventory, discovery, fingerprinting, and metadata; [EfficientIP SOLIDserver](https://efficientip.com/products/solidserver-ddi/) describes IPAM and service integration across network objects; [BlueCat Integrity](https://bluecatnetworks.com/products/integrity/) describes discovery, governance, and granular management permissions.

## Validation

Run `dotnet run --project tests/Ipam.Tests/Ipam.Tests.csproj -c Release` for model checks. Run the same command with `-- --api` for an isolated authenticated API lab. The lab uses unique temporary configuration, loopback listeners on high ports, and disabled default DHCP service. It verifies DNS and DHCP observations, permissions, settings backup/restore, and a separate fresh administrative session with successful read and modify operations before disposing the original session. It does not test remote access paths or live network deployment.
