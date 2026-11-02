# Prometheus parser

`MetricsClient.ParsePrometheus` turns exporter text into GPU rows. It is the most input-sensitive code in the app, so it gets its own edge-case journey.

## Sub-features

- Skips comments, blank lines, series without a `uuid` label, and anything outside `nvidia_smi_*`.
- Handles CRLF line endings.
- Drops non-numeric values (e.g. `N/A`).
- Reads utilization above 1.5 as a percent already, and anything at or below as a ratio.
- A total VRAM of 0 means unknown, so no VRAM %.

## How to get to it (user POV)

Not visible directly. It decides what each GPU row on a card shows.

## Driving it with verify.py

- `verify.py drive prometheus-parse --json`. `parse` steps feed fixture text straight to the parser; no stubs are needed, though drive still requires a launched instance.
- A pass means `edge-metrics` gives exactly one GPU, "2  GPU 4090", at 87.0% util, with no temperature, VRAM total or VRAM %. The served `active` fixture parses to two GPUs, ordered by index.
- Evidence: `evidence/prometheus-parse/result.json`.

## Gotchas

- `edge-metrics` is never served. It only feeds `parse` steps.
