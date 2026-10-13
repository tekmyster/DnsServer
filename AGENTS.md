# Project requirements

Apply the [product UX contract](docs/PRODUCT-UX.md) to every user-facing feature, including the existing console, dialogs, integrations, error messages and public DNS setup page. Keep service/API identifiers and behavior separate from presentation. Complete the quality gate in that contract before declaring a UI change complete.

## Administrative access changes

Standing user requirement: "ACL or credential changes will require a successful new-session check before I close the original session."

Keep the original working administrative session open while changing ACLs, management-access firewall rules, or credentials. After the change, establish a separate fresh session from the affected authorized source using the intended credentials, and verify authentication and required administrative privileges before closing the original session. When source restrictions matter, verify the source address seen by the target and test the required authorized access paths; explicitly report any untested paths.

An existing session, an open TCP port, or an SSH banner does not satisfy the new-session check. A successful login alone does not prove that configuration changes or the intended repair succeeded; verify and report those outcomes separately.

If the fresh-session check fails, retain the original session and correct or safely roll back the change before disconnecting. Do not intentionally close the original session while access validation remains unsuccessful.

## Verified change delivery

Standing user requirement: compile changes, update GitHub and update the production Tectanium deployment as changes are made. Deliver each coherent change set after the relevant build and checks pass. Stage verified immutable binaries, back up current configuration and lease authority, maintain DNS availability through a validated DNS-only standby where needed, preserve DNS/DHCP settings, and verify fresh administrative access and service behavior after deployment. Do not run the fresh-install packages over an existing production installation. The administrative-session requirement above continues to apply to any access or credential change.
