# Failure modes

When an endpoint misbehaves, the card shows an error string and partial data instead of crashing. Each endpoint fails on its own.

## Sub-features

- `no GPUs`: the exporter answers with no `nvidia_smi_*{uuid=...}` series. The card gets `metrics_ok=false` and "no GPUs in metrics", while the model still shows.
- `llm 500`: llama-server returns HTTP 500. The card gets `llm_ok=false` and an error containing "llm:" and "500", while the GPU rows still show.
- `power fallback`: with only `power_draw_instant_watts`, power still reads 55 W. That makes the host **ACTIVE** even though the model failed to load: the state reflects the GPUs, not llama-server.
- `down`: nothing listening. Both flags are false and the error is "metrics: … ; llm: …".

## How to get to it (user POV)

Stop the exporter, break the model load, or power off a host. Each card shows its own error line.

## Driving it with verify.py

- `verify.py compose probe-failures --json`. A single `probe` step covers the `nogpu`, `degraded` and `down` stub hosts.
- Evidence: `evidence/probe-failures/step0-probe.json` (the raw error strings).

## Gotchas

- Error text comes from .NET's `HttpClient` (e.g. "Connection refused", "Response status code does not indicate success: 500"). The scenarios match only stable fragments.
- The "down" host is an unused 127.77.0.N address, so the connection is refused immediately rather than timing out after 3 s.
