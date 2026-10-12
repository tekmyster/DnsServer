# Local Hyper-V test deployment

Deployed on 2026-10-12 from fork commit `8aea1bbb`, using two Ubuntu 24.04 guests and a self-contained `linux-x64` Release publish. This is a development lab, not a production deployment.

| VM | Role | Address | Console |
| --- | --- | --- | --- |
| DDI-Lab-01 | Cluster primary | 172.30.71.11 | http://172.30.71.11:5380/ |
| DDI-Lab-02 | Cluster secondary | 172.30.71.12 | http://172.30.71.12:5380/ |

Both guests use generation 2, two virtual CPUs, 4 GiB startup memory, and 32 GiB virtual disks. Hyper-V manages their dynamic memory with a 4 GiB maximum per VM. The `Technitium-DDI-Lab` internal switch connects only the host and these guests; its host address is `172.30.71.1/24`. No NAT or external uplink was added, and forwarding on the new host interface is disabled. Existing VMs, switches, services, credentials, and management firewall rules were left unchanged.

The Ubuntu image came from Canonical's [dated cloud image release](https://cloud-images.ubuntu.com/releases/noble/release-20260926/) and matched its published SHA-256 checksum. QEMU converted the generic QCOW image to VHDX. Each guest has its own differencing disk and cloud-init instance identity. Preserve `ubuntu-base.vhdx` while either guest depends on it.

## Access and operation

Local artifacts live in `C:\Users\dmessana\Technitium\artifacts\hyperv-lab`, which is ignored by Git. `LAB-ACCESS.txt` holds console credentials and SSH paths. `ddi-lab-ed25519` is the dedicated SSH private key; `known_hosts` contains the enrolled guest keys. Do not commit these files.

HTTPS is available on port 53443 with lab self-signed certificates. Initial native cluster enrollment explicitly accepted the primary's self-signed certificate. Subsequent native peer communication exercised the server's certificate/DANE handling; no reflection-based membership or replacement HTTP clients were used in these VMs.

The guest service is `ddi-lab.service`, with application files under `/opt/ddi-lab` and configuration under `/var/lib/ddi-lab`. The service starts on guest boot. VMs do not automatically start with the Windows host.

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

Temporary client namespaces were deleted, and Hyper-V MAC spoofing was restored to Off. Both VMs and DDI services remain running. Machine-readable check results are in the ignored lab directory as `validation-*.json`.

## Remaining validation

The initial deployment checks were followed by [automatic failover tests](AUTOMATIC-FAILOVER-TEST.md) using a separate private client network. New-client continuity, resolver fallback, and a short peer-link partition worked, but existing-lease takeover and automatic promotion are absent. Abrupt primary VM power loss exposed an acknowledged lease restored as an Offer; the additional `HA-Test` scope is disabled pending repair. Both VMs and the original scopes remain running. Live membership migration, pool exhaustion/overflow, WAN conditions, load/soak behavior, IPv6 DHCP, real MISP/NetBox integrations, and production detection accuracy remain unqualified. DHCP packets came from a purpose-built wire client; a full operating-system DHCP lease lifecycle and an actual relay appliance remain to be tested. Only the local host management source was verified; other authorized paths remain untested. The [feature matrix](FEATURE-MATRIX.md) still governs delivery status.
