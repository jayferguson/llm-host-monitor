# Active host

The card for a working host: state ACTIVE, the friendly model label, context size, and one row per GPU (VRAM %, utilization, temperature, power). The values come from `MetricsClient.ProbeAsync`.

## Sub-features

- `state`: ACTIVE when any GPU has util ≥ 2% or power ≥ 40 W.
- `model`: the first `data[].id` from `/v1/models`. `ModelLabels` maps it to a label, first by exact match and then by substring (`qwen3-coder-30b-a3b-q4_k_m` becomes "Qwen3-Coder-30B-A3B").
- `context`: `meta.n_ctx`.
- `gpus`: grouped by `uuid` and sorted by index. The name comes from `gpu_info{name=...}` with "NVIDIA GeForce " stripped. VRAM % is used/total, rounded to 0.1. Utilization is the ratio × 100.
- `total VRAM`: the sum of the totals.

## How to get to it (user POV)

Add a machine whose host runs llama-server and the GPU exporter. The card shows ACTIVE, "Qwen3-Coder-30B-A3B", 32768 ctx, and two GPU rows (RTX 3090 first).

## Driving it with verify.py

- `verify.py compose probe-active --json`.
- A pass means all of the following match the fixture:
  - `active` and both `_ok` flags are true;
  - the label and `context_tokens = 32768` are right;
  - `total_vram_bytes = 42949672960`;
  - `gpus[0]` is "0  RTX 3090" at 50.0% VRAM, 87.0% util, 66 °C, 301.2 W;
  - `gpus[1]` is "1  RTX 5060 Ti".
- Evidence: `evidence/probe-active/step0-probe.json`.

## Gotchas

- GPU names are `"{index}  {name}"` with two spaces.
- The fixture lists GPU 1 first on purpose. If sorting breaks, `gpus[0]` changes.
