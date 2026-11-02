# llm-host-monitor verification map

The maintained source for proving llm-host-monitor's behavior. The app is a Windows WinForms window, so on Linux this skill proves the layer under the cards: the polling and parsing code (`MetricsClient.cs`), the settings store (`MachineStore.cs`, `Models.cs`) and the host probe script (`host/check.sh`). Read this index first, then the feature file you need.

## Baseline preconditions

- Never point drive or gate at a real host. `verify.py launch` starts stub hosts on spare loopback addresses (`127.77.0.10`–`.60`). Each serves a fixture from `checks/fixtures/` on the app's fixed ports: `/v1/models` on 8080 and `/metrics` on 9835. One host is left with nothing listening, to stand in for DOWN.
- The stub addresses are needed because `host/check.sh` hard-codes both ports. Using a fresh 127.x address per host avoids clashing with anything already bound to 8080 on 127.0.0.1.
- The app code runs in a headless .NET 9 harness (`harness/`). The harness is built in `scratch/` and compiles `MetricsClient.cs`, `Models.cs` and `MachineStore.cs` by link, so nothing in the repo changes.
- The only way to reach a real host is `verify.py probe-live HOST --yes-read-only`. It sends two GETs, never runs inside drive or gate, and is not needed for proof.

## Driving conventions

- Run from the repo root. Feature ids are the file stems under `checks/scenarios/`. Each scenario is TOML `[[steps]]` built from `probe`, `parse`, `check_sh`, `store` and `expect_http`.
- Expectations compare against the harness's JSON field names (`metrics_ok`, `llm_ok`, `active`, `model_label`, `context_tokens`, `total_vram_bytes`, `gpus[].vram_pct`, ...). Supported suffixes are `_contains`, `_absent` and `gpu_count`.
- When the UI's meaning changes, edit fixtures and scenarios, not `verify.py`.

## Proof and skip reporting

- A pass means every step ran and every expectation held. Evidence is in `evidence/<id>/`:
  - `result.json`, with the observed values for each step;
  - `stepN-probe.json`, `stepN-store.json` and `stepN-check_sh.txt`, the raw outputs.
- A harness build failure, crash or timeout counts as a fail, never a skip.
- The window itself (card layout, colors, menus, text size, title bar) is **not verified** on Linux. The gate only proves that it still builds, with 0 warnings. Say so whenever a change touches `MainForm.cs`, `MachineCard.cs` or `AddMachineForm.cs`.

## Feature entry contract

Each feature file has an H1 and a paragraph, then exactly four H2s: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.py`, `Gotchas`.

## Features

- [Active host](./probe-active.md): ACTIVE state, model label, context, per-GPU VRAM/util/temp/power, sorted by index.
- [Idle host](./probe-idle.md): IDLE thresholds, GPU name from the legacy numeric code.
- [Failure modes](./probe-failures.md): no GPUs, llama-server 500, host down. Each error surfaces and nothing crashes.
- [Host probe script](./host-check.md): `host/check.sh` prints OK or PROBLEMS FOUND with the right exit code.
- [Settings store](./config-store.md): `machines.json` defaults and UiScale normalization.
- [Prometheus parser](./prometheus-parse.md): edge cases in exporter text.

## Not yet mapped

- Card rendering and the window (Windows only). A Windows runner with UI automation would be needed.
- Poll timing and the 3 s HTTP timeout. A slow-stub fixture could cover this.
- Add/edit machine dialog validation.
- `host/install-*.sh`. These change a host and must never run here; only `bash -n` covers them.
