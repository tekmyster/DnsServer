# DDI security module requirements

Provide protective DNS, threat intelligence enrichment, behavioral analytics, and device attribution as open source capabilities without paid software feature gates. Threat intelligence integrations are optional, and the resolver must remain usable without a commercial feed or reachable enrichment service. This module is specified here; the new analytics and MISP connector are not yet implemented.

## Threat intelligence and enforcement

Normalize domain, hostname, URL, IPv4, IPv6, and CIDR indicators with source, confidence, category, expiry, and provenance. DNS matching operates on hostnames and returned addresses. Preserve a URL's full value for enrichment, but extracting its host does not automatically justify blocking the whole domain. DNS cannot inspect an HTTPS URL path or prove that a client connected to the resolved destination.

Check query names, CNAME targets, and relevant A/AAAA answers, including cached responses. Define supported SVCB/HTTPS address hint handling. Avoid treating unrelated additional-section glue as a client destination. Support exact and explicit suffix matches, allow exceptions with expiry, monitor, block, and sinkhole actions, and policies by network view, site, subnet, and client group. Shared hosting and CDN addresses require careful IP policy scope to avoid blocking unrelated services.

Feed updates need bounded downloads and parsing, atomic activation, retractions, expiry, freshness status, and rollback. Keep the last valid policy locally on every service node. Enrichment HTTP calls and feed refresh must never execute synchronously inside the DNS response path. A feed or MISP outage does not interrupt resolution.

Existing Technitium built in blocking and Advanced Blocking require a unified precedence contract. The DNS Block List app serves DNSBL queries; its presence does not by itself enforce resolved IP reputation on recursive replies. Existing rebinding protection, DNSSEC, and query rate limits are useful foundations to integrate into the security interface.

## MISP enrichment

Use authenticated, paginated background MISP searches for configured indicator types and permitted events. Retain event and attribute UUIDs, tags, threat context, sightings, timestamps, and available indicator decay information. Respect sharing restrictions, deleted attributes, revocations, and `to_ids` semantics. An enrichment match is evidence rather than an unconditional block instruction.

Warninglists and explicit operator exceptions inform false-positive review; they are not universal proof that an indicator is harmless. Maintain source freshness and confidence separately from behavioral risk. Return optional sightings to MISP only when the operator enables that integration, with controls over disclosed client information. Avoid sending full internal DNS telemetry to external enrichment services by default.

Sources: [MISP API specification](https://misp.software/openapi/), [MISP indicator decay](https://www.misp-project.org/2019/09/12/Decaying-Of-Indicators.html/), and [MISP tools](https://www.misp-project.org/tools/).

## Explainable exfiltration scoring

Aggregate observations by network context, client identity where known, and registrable domain across short and long windows. Use a maintained public suffix dataset so public suffixes and hosted-domain boundaries are handled deliberately. Resolve device identity from lease history at event time; a reused client IP must not transfer another device's history.

Candidate features include label lengths, character entropy, encoding-like structure, unique subdomain count and ratio, query rate, repeated query payload patterns, query type distribution, response sizes, NXDOMAIN patterns, destination novelty, and changes relative to the client's normal behavior. Track sustained low-rate activity as well as bursts. TXT queries or high entropy alone must not establish exfiltration; legitimate services frequently use machine-generated labels.

Produce a configurable 0 to 100 risk score with feature contributions, evidence window, baseline status, model version, and confidence. The score is a heuristic priority until calibrated against representative labeled traffic; it is not the probability of data theft. DNS label volume is an observable encoding-capacity signal rather than verified bytes stolen. Separate indicator reputation from behavioral exfiltration score in investigations.

Start in monitor mode, require corroborating evidence for escalation, and measure false positives across legitimate CDNs, endpoint telemetry, DNS verification, mail security, and approved tunnels. Blocking thresholds require evaluated data and operator policy. Use decaying windows, bounded per-client state, sampling visibility, and explicit event loss counters.

## Additional security capabilities

| Capability | Intended behavior |
| --- | --- |
| DGA detection | Correlate lexical patterns, query failures, destination novelty, and client behavior across time |
| Command and control signals | Detect repeated beacon-like timing and unusual persistent destinations; label the result as suspicion |
| Lookalike domains | Compare approved organization domains against typos, homographs, and suspicious IDN forms |
| Newly observed domains | Track first-seen locally; distinguish this from externally verified registration age |
| Fast flux signals | Monitor abnormal IP/ASN churn and TTL patterns with exceptions for legitimate infrastructure |
| DNS rebinding | Protect private and link-local destinations while preserving explicitly configured internal services |
| Resolver integrity | Surface DNSSEC failures, suspicious response anomalies, and upstream behavior; retain existing protocol validation |
| Infrastructure abuse | Recursion access controls, query and response rate controls, random-subdomain flood detection, and amplification reduction |
| Zone protection | Restrict transfers and dynamic updates, use TSIG where appropriate, and audit authoritative changes |
| Device correlation | Attach site, view, VLAN, lease-time MAC/client evidence, intended owner, and NetBox references when available |
| DHCP anomalies | Detect pool exhaustion patterns, suspicious lease churn, conflicting identities, and possible rogue DHCP through an appropriate network sensor |
| IPAM drift | Identify observed addresses outside approved intent and changes to DNS or DHCP that bypass the provisioning workflow |
| SOC workflow | Searchable timelines, evidence, SIEM export, webhooks, deduplication, analyst disposition, and optional response integrations |
| Policy operations | Simulation, exception expiry, revision rollout, rollback, and per-node enforcement health |

DNS query logs cannot establish that a client bypassed the resolver through a third-party DoH service or direct IP traffic. Those controls need network or endpoint integrations. Rogue DHCP visibility likewise requires a suitable observation point; a resolver log alone is insufficient.

Capability references: [Infoblox Threat Defense](https://www.infoblox.com/products/threat-defense/), [EfficientIP DNS Guardian](https://efficientip.com/products/dns-guardian/), [BlueCat network security](https://bluecatnetworks.com/products/network-security/), and [Technitium DNS Server](https://technitium.com/dns/). These references establish product categories rather than detection accuracy for this implementation.

## Delivery order

1. Normalize feeds and decision evidence, match domain/CNAME and resolved IP indicators, and publish local policy snapshots with exceptions and rollback.
2. Add asynchronous MISP enrichment, cached evidence, expiry, and source health.
3. Implement monitor-only exfiltration and DGA analytics with replay tests and representative benign baselines.
4. Add device and historical lease correlation, investigations, and SIEM export.
5. Evaluate behavioral enforcement, rate controls, and integration-driven containment with deployment-specific access checks.

No stage automatically changes administrative ACLs, management firewall rules, or credentials without retaining the original session and completing a successful fresh-session check from the affected authorized source. Response automation must have explicit policy authority, reversible actions, and recorded outcomes.
