# Optional model assistance

teQnatium connects to an independently operated model server through the widely implemented OpenAI-compatible Chat Completions HTTP interface. This is a compatibility interface, not a claim of a formally standardized protocol. No vendor SDK or model subscription is required by teQnatium. The model server handles CPU/GPU selection and model loading; teQnatium does not provision a GPU or install model weights.

## Connect a model

1. Run a model server that supports `/v1/chat/completions`, such as [Ollama](https://github.com/ollama/ollama/blob/main/docs/api/openai-compatibility.mdx) or [vLLM](https://docs.vllm.ai/en/stable/serving/online_serving/openai_compatible_server/). Choose a model that fits the server's available memory.
2. In Security > Protection settings > Model assistance, enter the complete endpoint and the exact served model name. A same-host example is `http://127.0.0.1:11434/v1/chat/completions`. HTTP requires a literal loopback address; a separate GPU server requires HTTPS and Allow a network endpoint. DNS names such as localhost do not qualify for insecure loopback access.
3. If the server requires authentication, set a `DDI_LLM_*_API_KEY` environment variable on the teQnatium service and enter its name under Credential reference. The policy stores only that reference, never the key. Configure credentials on each node that will request analysis. Configuring a reference does not set an environment variable.
4. Enable event explanations and save. An administrator can choose View evidence > Explain event for a recent event. Model analysis is on demand; there is no automatic telemetry upload.

By default, requests omit full query names, domains, client addresses, MAC addresses, DHCP client identifiers, hostnames, site names and raw threat-source evidence. They include selected detector features and scores, sample/coverage metadata, protocol outcome and whether DHCP attribution exists. Include registered domains is an explicit opt-in and still omits full query names. Requests go to the configured endpoint; Allow a network endpoint authorizes that destination, including a separate LAN model server.

## Operation

The connector sends a non-streaming request with `model`, system/user messages and `max_tokens: 600`. It accepts a textual `choices[0].message.content` response. Custom API prefixes and tool-call-only responses are unsupported. There is no tool execution, policy mutation or DNS/DHCP hook in the connector. Endpoint redirects and system proxies are disabled. HTTPS certificate validation remains enabled.

At most two analyses run concurrently per server. Requests are limited to 32 KiB, responses to 64 KiB and 16000 text characters, and each call to 30 seconds. No automatic retry repeats a possibly billable request. Keys are read only from permitted environment references and sent only in a Bearer header. Endpoint credentials, query strings and fragments are rejected. The API requires Settings view and Administration modify permission; changing or rolling back connection settings requires Administration modify in addition to policy modify permission. Successful analysis requests are logged by actor and event reference; model results are not persisted in the investigation store in this increment.

Connection settings travel with security policy backup and synchronization; model credentials and model weights do not. The source and destination must be reviewed during a deployment migration. An explanation uses the current connection settings and a recent node-local event; archived-only events cannot yet be analyzed.

## Validation

Synthetic detector regressions, mocked provider request/response tests and native API permission tests exercise this implementation. They do not establish accuracy, a live Ollama/vLLM connection, GPU compatibility, GPU capacity or any hosted model's privacy/retention behavior. No production provider is configured or called during those tests. Trained classifiers and predictive intelligence are separate delivery items in [the roadmap](SECURITY-ROADMAP.md).

### Local verification on 2026-10-13

The Release application/test builds passed. The complete DDI regression run passed 401 checks, including 76 authenticated API checks and an end-to-end HTTP request to an isolated loopback model fixture. An additional invalid-credential-header case brought the focused analytics/connector run to 31 passing checks; that focused suite and presentation checks were rerun after the final connector change.

Desktop event evidence, a successful advisory explanation and the 390-pixel model settings view were visually inspected. The checked page had no horizontal overflow, all six new controls had labels, raw event data remained collapsed and provider HTML-like text was displayed literally. Additional observation windows are disclosed separately. Provider failures were tested through code; browser loading, provider-error and reader-role states were not all visually exercised. Screenshots are kept locally under `artifacts/security-v2/`. Production services and security settings were not changed.
