# Idle host

A host that is up but quiet shows IDLE. Older exporters have no `gpu_info` series, so the app names the GPU from the numeric `nvidia_smi_name` code and takes its index from `nvidia_smi_index`.

## Sub-features

- `idle`: util < 2% and power < 40 W on every GPU.
- `legacy name`: code 3090 maps to "RTX 3090" through `GpuNameByCode`; an unknown code becomes "GPU <code>".
- `label`: the exact match `gemma4` becomes "Gemma 4".

## How to get to it (user POV)

Point a card at a host whose model is loaded but not generating. The card reads IDLE, "Gemma 4", 8192 ctx.

## Driving it with verify.py

- `verify.py compose probe-idle --json`.
- A pass means `active = false`, both `_ok` flags true, and "0  RTX 3090" at 4.2% VRAM, 1.0% util, 39 °C, 22.5 W.
- Evidence: `evidence/probe-idle/step0-probe.json`.

## Gotchas

- The thresholds (2%, 40 W) are constants in `MetricsClient.cs`. If they change, update this fixture's numbers so they still sit on the intended side.
