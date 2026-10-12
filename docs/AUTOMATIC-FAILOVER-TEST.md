# Automatic failover test results

Tested on the two local Hyper-V guests on 2026-10-12, running fork commit `8aea1bbb`. **Full automatic failover did not pass.** New-client DHCP continuity and DNS resolver fallback worked, but existing-lease takeover is absent and abrupt VM power loss exposed an acknowledged-lease durability failure.

## Test environment

The existing management network remained `172.30.71.0/24`. A separate private Hyper-V switch, `Technitium-DDI-HA-Test`, connects guest bridges at `172.30.72.11` and `172.30.72.12`. Their bridge MAC addresses match the Hyper-V adapters. The `HA-Test` scope uses addresses `172.30.72.100–149`, the same immutable allocation epoch on both nodes, and an ordered local primary/secondary pair. A temporary Linux client namespace attached through a bridged veth on VM 02 could reach both servers without relying on either server's management interface.

The DHCP wire client selected the first received OFFER, broadcast its REQUEST with that server identifier, and validated the returned ACK. It did not select the survivor manually in the outage tests. The client's namespace resolver listed both DNS addresses; actual `getent ahostsv4` calls resolved `probe.clients.ddi.test` through the OS resolver after each acquisition. Only the partition's secondary-allocation check explicitly selected the secondary to exercise both owners while the preferred primary remained online.

## Observed results

| Scenario | Selected DHCP server | Address | DHCP acquisition | Result |
| --- | --- | --- | --- | --- |
| Both services available | Primary | 172.30.72.119 | 0.047 s | Preferred primary selected; DNS resolved |
| Primary DDI service stopped | Secondary | 172.30.72.140 | 0.505 s | Automatic first-offer selection; DNS resolved |
| Primary service restored | Primary | 172.30.72.133 | 0.043 s | New client preferred primary again; DNS resolved |
| Primary VM abruptly powered off | Secondary | 172.30.72.104 | 0.509 s | Automatic first-offer selection; DNS resolved |
| Primary VM booted again | Primary | 172.30.72.147 | 0.042 s | New client preferred primary again; DNS resolved |
| Peer links filtered, primary allocation | Primary | 172.30.72.111 | 0.010 s | Client received primary's own allocation |
| Peer links filtered, explicit secondary selection | Secondary | 172.30.72.118 | 0.509 s | Client received secondary's own allocation |

Acquisition times measure the client exchange from DISCOVER through ACK after the fault was introduced. They are not failure-detection or automatic promotion times. The seven observed addresses were distinct; this bounded exercise is not an exhaustive split-brain or pool-exhaustion qualification.

For the partition, each guest temporarily dropped outbound traffic to the other guest's configured management/replication IP. Private client access and host management access remained available. Both original SSH sessions stayed open; separate fresh SSH sessions proved login and sudo, and fresh HTTPS sessions proved administrative read/modify privileges from host source `172.30.71.1`. The exact temporary filtering rules were removed afterward. Both nodes ultimately held equal HA replica snapshots, with the crash-damaged entry described below.

## Failed and missing behavior

**Existing lease takeover:** while the primary service was stopped, the original client broadcast a rebind REQUEST for `172.30.72.119`. No ACK arrived during the four-second observation window. The secondary did not acquire authority to renew that primary-owned address. This matches the current authority boundary; automatic fenced lease takeover has not been implemented.

**Automatic cluster promotion:** the secondary remained a Secondary during the primary outage. The current implementation does not automatically promote it. New DHCP allocation from its own sequence and serving existing DNS zone data must not be described as full cluster takeover.

**Acknowledged lease lost after abrupt power loss:** the client received a validated primary ACK for `172.30.72.133` immediately before the primary VM was powered off. After reboot, the authoritative entry was `Offer`, rather than `Lease`; both nodes converged on that entry. The primary's retained journal contained its OFFER but no lease commit for that address. The required seven-lease convergence check failed and timed out.

This is a production blocker: after the short offer protection expires, an acknowledged address could become available while a real client still considers its lease valid. No duplicate lease was observed in this test. The code flushes temporary files before replacement, but does not sync the containing Linux directory after the snapshot/watermark renames. That is a candidate durability mechanism, not a validated root-cause conclusion. Repair and repeated crash-at-write testing are required before relying on this path.

## Final state and evidence

Both VMs and DDI services are running. Original `Default` scopes remain as configured before these tests. `HA-Test` is disabled on both nodes pending the durability repair; its authority and evidence are retained. Client namespaces and peer filtering were removed, and MAC spoofing is Off on both private adapters. The private switch, bridged interfaces, and private DNS listeners remain available for further testing. Fresh administrative access was rechecked after cleanup.

The ignored `artifacts/hyperv-lab` directory contains `validation-automatic-failover.json`, final per-node state snapshots, and the test scripts. No credentials or private keys were committed. Other management source paths, full operating-system DHCP lease lifecycle behavior, long partitions, pool exhaustion, forced ownership transfer, fencing, and regional recovery remain unqualified.
