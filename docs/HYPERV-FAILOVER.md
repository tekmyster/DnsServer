# Hyper-V fenced failover

The opt-in host controller supports this isolated two-VM Linux lab. It requires pinned VM identities, SSH host keys and HTTPS certificates, an administrative management source and the failed node’s recoverable authoritative disk. It is not a redundant regional controller.

## Recovery sequence

1. Three consecutive management HTTPS failures trigger investigation. Certificate and authentication failures do not authorize takeover.
2. The controller applies and verifies persistent Hyper-V extended ACLs on every donor adapter. Only TCP SSH and console management between the VM and the authorized host remain allowed; all other inbound/outbound traffic is denied.
3. If necessary, the donor boots under that fence. Its original SSH session remains open until a separate fresh SSH session proves authentication, sudo and the expected source. Fresh HTTPS sessions prove administrative read and modification access.
4. Donor scopes are disabled. The controller obtains the native formula’s owned addresses and durably surrenders them in bounded, idempotent batches, preserving leases, offers and quarantine. It refuses borrowed authority or grants to another receiver.
5. The privileged recovery endpoint imports only complete, permanent grants from that owner to the survivor, with matching existing plans and epochs. The controller stops the donor service, promotes the survivor and retains the donor’s Fenced identity.
6. Completed recovery keeps verifying physical fences. It never automatically removes a fence, returns grants or rejoins a former owner.

The recovery API trusts the authorized external administrator to attest physical fencing; it does not independently verify Hyper-V or accept a cryptographic fencing receipt. It requires direct HTTPS, DHCP modification and administrative deletion privileges; cluster service tokens cannot invoke it. Failure to read the authoritative journal or verify the fence blocks takeover. Stale replicas and reachability timeouts do not convey ownership.

## Installation and operation

`scripts/hyperv-ddi-controller.py` uses the pinned configuration in the ignored lab directory. Install its pinned dependency from `scripts/hyperv-ddi-requirements.txt` into an isolated Python environment. The controller enforces a single host writer and durably records each recovery stage. `scripts/fence-hyperv-ddi.ps1` verifies VM UUIDs and reads back each adapter’s ACL. Its process-scoped execution-policy option does not change the host’s policy.

The deployed `Technitium DDI Lab Failover` task runs hidden at the current administrator’s logon. It is not a service that starts before login. Credentials, keys, configuration and evidence remain in ignored `artifacts/hyperv-lab`; do not commit them. The survivor console is http://172.30.71.12:5380/ and the former primary’s application is stopped.

Use `scripts/package-ddi-linux.ps1` to create a self-contained Linux archive and complete file manifest. `scripts/deploy-ddi-linux-lab.py` verifies the manifest in a new immutable directory and synchronizes the filesystem before activation. Deployment rollback changes executable paths, never DHCP authority journals or permanent grants. Fresh SSH/sudo and HTTPS administrative checks follow activation.

## Limits and verified results

The bounded controller supports at most 65,536 owned addresses per scope. Automatic unfencing, failback, grant reclamation and rejoin are not implemented. A fenced identity cannot be removed while it anchors a DHCP allocation plan. Recovery requires a separately designed ownership migration; restarting a former primary does not restore its authority.

Both service failure and abrupt VM power loss passed local fenced takeover tests, including renewal of an existing lease and native cluster promotion. Full VM recovery took about 92 seconds including boot and administrative checks. Three abrupt post-ACK power cuts retained leases. Linux directory durability is exercised here; physical power loss on Windows/macOS, donor disk destruction, exhaustive crash points, long partitions, regional deployments, other fencing providers and redundant controller coordination remain unqualified. The two VMs share this host, so they cannot provide availability during host failure.

Administrative access was verified only from 172.30.71.1. Keep original sessions open for any subsequent ACL or credential change until fresh authorized sessions prove login and required privileges. See [test evidence](AUTOMATIC-FAILOVER-TEST.md) and [delivery status](FEATURE-MATRIX.md).
