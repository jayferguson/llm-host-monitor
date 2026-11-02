# Agent rules — llm-host-monitor

Small Windows desktop app (C# WinForms, .NET 9) that shows local LLM hosts as cards: state (ACTIVE / IDLE / DOWN), loaded model, context size and per-GPU VRAM, utilization, temperature and power. It polls two HTTP endpoints per host: llama.cpp `llama-server` `/v1/models` on 8080 and nvidia_gpu_exporter `/metrics` on 9835. `README.md` is the user reference.

## Layout

- `Program.cs`, `MainForm.cs`: entry point and main window (right-click menu, layout, text size, title bar).
- `MachineCard.cs`: one host card. `AddMachineForm.cs`: add/edit dialog.
- `MetricsClient.cs`: polls the two endpoints and parses them. `Models.cs`: data types. `MachineStore.cs`: loads/saves `%AppData%\LlmHostMonitor\machines.json`.
- `LlmHostMonitor.csproj`: `net9.0-windows`, WinForms.
- `host/`: Linux host setup: `install-gpu-exporter.sh`, `install-llama-server.sh`, `llama-server.service.example`, `check.sh`.

## Run

Windows, from the repo root (needs the .NET 9 SDK):

```text
dotnet run -c Release
```

## Tests

There is no test project. The checks are a build and the host probe.

Build (also works on Linux, where it compiles but cannot run the UI):

```text
dotnet build -c Release -p:EnableWindowsTargeting=true
```

Expect `Build succeeded. 0 Warning(s) 0 Error(s)`. On Windows the `-p:` flag is not needed.

Host probe, against a host running both services:

```text
./host/check.sh HOST
```

Prints the model id, `n_ctx`, GPU count and each metric, and ends `OK` (exit 0) or fails. Shell scripts should pass `bash -n host/*.sh`.

## Conventions

- Keep the Windows side dependency-free: nothing to install on Windows but the app, and nothing on hosts beyond the two services.
- Settings persist in `machines.json` under `%AppData%`; it is git-ignored, as are `*.local.*` and `_shots/` (screenshot scratch). Do not commit real host names or addresses; docs use `YOUR_HOST`.
- Keep `README.md` in step with menu items, shortcuts and config keys when they change.

## Git

Pushing straight to `main` is OK for this repo. Build first, push normally (no force), keep hooks on.

## Verify

A `verify-llm-host-monitor` skill is being added by the Verify bot under `.cursor/skills/` (pending; not in the repo yet). Once it lands, prove every change with it before calling the work done.
