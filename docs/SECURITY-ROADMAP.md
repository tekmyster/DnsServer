# Tectanium security direction

Combine public capability patterns into an independently implemented, openly available security system. Vendor descriptions inform requirements; they do not establish that Tectanium reproduces vendor algorithms or achieves equivalent accuracy.

| Capability pattern | Tectanium delivery | Next work |
| --- | --- | --- |
| Infoblox: predictive intelligence and asset context | Existing local indicators, asynchronous MISP enrichment and current DHCP device context | Source confidence/age/sightings, infrastructure relationships, first-seen and registration-age separation, historical lease attribution, site/view/VLAN and NetBox context |
| BlueCat: explainable behavioral indicators | Rolling five-minute and one-hour tunneling analysis, cross-domain DGA-pattern suspicion and periodic DNS observations | Representative traffic replay, learned baselines, configurable detector policies and false-positive measurements |
| EfficientIP: transaction inspection and adaptive responses | Existing reachable CNAME/A/AAAA inspection and shared expiring DNS/DHCP evidence | More record/transport coverage, graduated reversible containment, recovery conditions and verified enforcement results |
| Optional local or network LLM | On-demand event explanations over a Chat Completions-compatible endpoint | Evidence-grounded multi-event investigations, evaluated answers and investigation retention |

## Detection and model boundaries

The new analyzer is `heuristic-v2`, not a trained machine-learning model. It retains up to 512 observations per client/domain and per client for one hour, with at most 4096 streams of each kind. Five-minute and one-hour windows advance with observations. Samples expire, clock rollback resets affected streams, and capacity eviction can discard history. Fixed-size name fingerprints avoid retaining encoded subdomains in analyzer state. Limited coverage is included in findings and shown in evidence views. The state is node-local and in memory; shared qualifying device findings still use the existing signal distribution path.

Tunneling escalation requires both unusual label structure and changing subdomains. TXT, query volume, failures or entropy alone do not establish exfiltration. The corroboration cap is shown as a negative point adjustment when needed. DGA suspicion requires at least 30 queries, diverse registered domains, diverse high-entropy registered labels, and predominantly NXDOMAIN results over one hour. Periodicity is a low-priority observation; legitimate polling can look identical. These rules are uncalibrated and can miss attacks or identify legitimate activity.

The selected behavioral priority is the highest finding, rather than a sum of correlated findings. Source indicator confidence remains separate in matched evidence. Existing explicit device policy may use shared priority; this update does not introduce automatic LLM enforcement or enable protection on an existing deployment.

## Predictive intelligence and learned models

Predictive intelligence needs observations beyond a local DNS query: indicator provenance and decay, passive DNS/infrastructure relationships, and reliable temporal context. First seen locally must never be labeled domain registration age. An LLM explanation is not predictive threat intelligence.

Before shipping learned classifiers, build versioned benign and malicious replay datasets with source permissions, privacy controls and separate training/evaluation partitions. Measure precision, recall, false positives per device/day, delayed detection and throughput. Evaluate CDNs, DNS verification, endpoint telemetry, mail security and approved tunnels. Package models with feature schemas, signatures, version/rollback and deterministic fallback. Report when a baseline is absent, stale or affected by sparse/sampled data. Trained models, learned baselines and validated predictive intelligence are not implemented by this increment.

## Optional model assistance

See [model connection guide](SECURITY-LLM.md). Explanations are advisory and requested by an administrator. They cannot call tools, change a policy, allocate an address or block traffic. A model outage does not interrupt DNS/DHCP. Model output is displayed as plain text and may be incorrect; operator decisions remain tied to observed evidence.

## Public references

- [Infoblox Threat Defense](https://www.infoblox.com/products/threat-defense/)
- [BlueCat Edge threat indicators](https://docs.bluecatnetworks.com/r/BlueCat-Edge-User-Guide/Identifying-threat-activity?contentId=Z4l24x_FRM2F8ebhQc4ksw)
- [EfficientIP DNS Guardian](https://efficientip.com/products/dns-guardian/)

These references describe capabilities, not comparative test results.
