# Backup and new-deployment migration

Open Settings > General. Backup Settings preserves selected configuration, identity and operational data for recovery. New Deployment creates a portable package for a fresh standalone destination.

## Move to a new deployment

1. On the source, choose New Deployment > Download Package. Store the ZIP securely: DNS keys and client lease data can be included.
2. Install and sign in to a fresh standalone destination using its intended credentials and management addresses.
3. Open New Deployment, select the package and choose Preview Import. Review zone/scope counts, portable DNS settings, inventory, security policy and checksum status.
4. Choose Import Package. DHCP scopes stay disabled. Test DNS records, settings, security and a separate fresh administrative login before any live cutover.
5. Review authoritative NS/SOA records, secondary-zone upstreams, destination interfaces and external integration settings. Transfer distributed DHCP authority or stop/fence the former standalone owners before activating client service. Join the intended cluster separately.

Packages include current DNS zones/keys, resolver/cache/filtering settings, DHCP options/reservations/lease snapshots, IPAM intent and security policy. Current runtime zones/scopes include edits awaiting delayed saves. Cluster catalog identity is omitted; imported members are detached from the former cluster catalog. Zone authority types and upstream settings are retained. External MISP configuration is cleared and remains disabled; configure enrichment separately on the destination. Applications, TSIG trust, management configuration, external integration credentials, event/enrichment history and allocation journals use separate backups or explicit setup.

The destination keeps its credentials, management ports/certificates, server identity, DNS listening/source addresses, optional protocol listeners and cluster identity. Copied leases do not surrender source allocation authority. An active source must not share those pools with the destination merely because a snapshot was imported.

## Review and recovery

Migration requires an interactive administrator with Settings and Administration deletion privileges. Cluster service tokens cannot invoke it. The destination must be outside a cluster, have no user zones or configured scopes, no active scopes/leases and no distributed allocation plans. An unused disabled Default scope is permitted. Stop other configuration work during migration; the destination fingerprint is checked immediately before applying the review. Independent APIs are not covered by a global configuration transaction.

Reviews belong to the reviewing administrator, expire after ten minutes and are consumed on import. A new review replaces that administrator's previous one. At most four packages are held. ZIP uploads are limited to 16 MiB, 2,048 entries and 128 MiB expanded total; each expanded file is limited to 16 MiB. Unsafe/duplicate paths and checksum mismatches are rejected. Checksums detect changes; they do not authenticate the sender or encrypt the ZIP.

Existing Technitium backups can supply zones, scopes and filtering files. They have no migration checksum manifest and retain destination resolver settings when portable settings are absent. Old cluster catalog archives require a new-deployment export. Unsupported data is not converted into authority.

Before applying changes, a native recovery ZIP named migration-before-<identifier>.zip is flushed in the destination's configuration folder. Linux synchronizes its directory and restricts the file to its owner. A caught import failure attempts to restore pre-import data/settings and reports whether recovery succeeded. Keep the recovery backup through validation. Abrupt process/power failure mid-import is not automatically resumed: inspect the destination and use the retained backup before activation. Online cluster replacement, scheduled/encrypted backups, disk-loss recovery and exhaustive crash-point qualification remain outside this workflow.

For ordinary restores affecting authentication, management ACLs or credentials, keep the original administrative session until a separate fresh session from each required source proves login and required privileges. Existing sessions and reachable ports are insufficient.

## Verification

The dedicated migration suite has 36 checks: native export/preview/import, manifest tampering, unsafe paths, anonymous rejection, stale runtime previews, legacy backups, failed-import rollback, retained identity/listeners, disabled scopes, exact lease identity/expiration snapshots, DNS records, IPAM/security policy, catalog detachment and fresh administrative modification. The console was inspected at desktop and 390-pixel widths. Browser import confirmed named controls, initial dialog focus, readable previews, success feedback, refreshed settings and disabled replay controls. Full ZIP export responses passed API checks. The in-app browser did not expose Blob download completion through its automation event, so filesystem download completion was not verified through that browser.
