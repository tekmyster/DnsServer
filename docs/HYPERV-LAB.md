# Local Hyper-V test deployment

Deployed on 2026-10-12 from fork commit `8aea1bbb`, using two Ubuntu 24.04 guests and a self-contained `linux-x64` Release publish. This is a development lab, not a production deployment.

| VM | Role | Address | Console |
| --- | --- | --- | --- |
| DDI-Lab-01 | Fenced former primary; DDI stopped | 172.30.71.11 | SSH from host only |
| DDI-Lab-02 | Active cluster primary | 172.30.71.12 | http://172.30.71.12:5380/ |

Both guests use generation 2, two virtual CPUs, 4 GiB startup memory, and 32 GiB virtual disks. Hyper-V manages their dynamic memory with a 4 GiB maximum per VM. The `Technitium-DDI-Lab` internal switch connects only the host and these guests; its host address is `172.30.71.1/24`. No NAT or external uplink was added, and forwarding on the new host interface is disabled. Existing VMs, switches, services, credentials, and management firewall rules were left unchanged.

The Ubuntu image came from Canonical's [dated cloud image release](https://cloud-images.ubuntu.com/releases/noble/release-20260926/) and matched its published SHA-256 checksum. QEMU converted the generic QCOW image to VHDX. Each guest has its own differencing disk and cloud-init instance identity. Preserve `ubuntu-base.vhdx` while either guest depends on it.

## Access and operation

Local artifacts live in `C:\Users\dmessana\Technitium\artifacts\hyperv-lab`, which is ignored by Git. `LAB-ACCESS.txt` holds console credentials and SSH paths. `ddi-lab-ed25519` is the dedicated SSH private key; `known_hosts` contains the enrolled guest keys. Do not commit these files.

HTTPS is available on port 53443 with lab self-signed certificates. Initial native cluster enrollment explicitly accepted the primary's self-signed certificate. Subsequent native peer communication exercised the server's certificate/DANE handling; no reflection-based membership or replacement HTTP clients were used in these VMs.

The guest service is `ddi-lab.service`, with application files in immutable `/opt/ddi-lab-build-*` directories and configuration under `/var/lib/ddi-lab`. The service is enabled on guest boot; the fenced former primary is intentionally stopped. Do not restart or unfence it as a failback procedure. VMs do not automatically start with the Windows host.

Use module-qualified commands because this workstation also has VMware commands with the same names:

```powershell
Hyper-V\Start-VM -Name DDI-Lab-01,DDI-Lab-02
Hyper-V\Stop-VM -Name DDI-Lab-01,DDI-Lab-02
```

DNS binds each guest's lab address on port 53. The guest's existing systemd-resolved loopback service remains enabled. The disabled default DHCP scopes were aligned before installing distributed authority; both now serve only the isolated `172.30.71.100–199` pool. The cluster domain is `ddi.test`, with `clients.ddi.test` in its synchronized catalog. A synthetic domain indicator blocks `bad.clients.ddi.test`; `probe.clients.ddi.test` resolves to `192.0.2.42`. A synthetic address indicator provides monitor evidence for IPAM.

## Verified behavior

- Separate fresh SSH sessions authenticated with the intended key and proved `sudo` privileges while original sessions remained open. The guests saw source `172.30.71.1`.
- Fresh HTTPS administrative sessions proved authentication and actual read/modify permissions after enrollment and service restarts.
- Native cluster initialization and joining completed, followed by connected heartbeats on both guests.
- Catalog zone synchronization delivered records to both guests; UDP and TCP queries returned the expected answer.
- Both peers installed the same immutable DHCP authority epoch with matching scope configuration.
- Isolated Linux client namespaces performed real DHCP broadcast DISCOVER/OFFER/REQUEST/ACK exchanges. Primary and secondary committed distinct addresses, `172.30.71.177` and `172.30.71.144`; both nodes received both lease replicas.
- Malicious DNS queries from those leased addresses were refused and produced device-attributed security signals visible on both nodes.
- IPAM inventory synchronized and reconciled observed leases and synthetic address threat evidence.
- Stopping the secondary DDI service left primary DNS, security enforcement, and new DHCP allocation working. Its new lease was `172.30.71.111`, distinct from the existing leases. Restart restored the original authority epoch and reconciled the missed lease.
- Both guest services restarted successfully with the three leases, IPAM inventory, and security policy retained.
- Both VMs shut down cleanly and booted again; their services started automatically, reconnected, retained state, and passed fresh administrative read/modify checks.

Temporary client namespaces were deleted, and Hyper-V MAC spoofing was restored to Off. Both VMs remain running; VM 02 serves DDI while VM 01 is fenced with its service stopped. Machine-readable check results are in the ignored lab directory as `validation-*.json`.

## Remaining validation

The [automatic failover retest](AUTOMATIC-FAILOVER-TEST.md) corrected the initial Linux lease durability failure and passed fenced takeover after service failure and abrupt VM power loss. The host controller runs as the `Technitium DDI Lab Failover` scheduled logon task. See [the runbook](HYPERV-FAILOVER.md) before recovering the fenced node. The private HA test network uses 172.30.72.0/24; separate power-loss fixtures also added 172.30.73.11/.12 guest aliases.

Live membership migration, automatic failback/rejoin, pool exhaustion, WAN conditions, load/soak behavior, IPv6 DHCP, real MISP/NetBox integrations and production detection accuracy remain unqualified. DHCP packets came from a purpose-built wire client; a full operating-system lease lifecycle and an actual relay appliance remain to be tested. Only the local host management source was verified; other authorized paths remain untested. The [feature matrix](FEATURE-MATRIX.md) governs delivery status.
