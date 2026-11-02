# Settings store

`MachineStore.Load()` reads `machines.json` from the app-data folder. If the file is missing, empty of machines, or corrupt, it falls back to a one-machine default (`llm-1` on 127.0.0.1). It also clamps `UiScale` to the range [0.7, 2.0], with 0 or a non-finite value meaning 1.0.

## Sub-features

- `default`: no file gives one machine, `llm-1`, polling every 5 s, scale 1.0.
- `round-trip`: camelCase keys (`pollSeconds`, `uiScale`, `horizontalLayout`, `machines[].llmBaseUrl`) load into the config.
- `normalize`: 5.0 becomes 2.0, 0 becomes 1.0, 0.3 becomes 0.7.
- `fallback`: an empty `machines` list or invalid JSON gives the default config.

## How to get to it (user POV)

Settings persist across launches. Editing `%AppData%\LlmHostMonitor\machines.json` by hand to an out-of-range text size snaps it back on the next start.

## Driving it with verify.py

- `verify.py compose config-store --json`. Each `store` step writes a `machines.json` into a throwaway `scratch/store-*` folder, runs the harness with `XDG_CONFIG_HOME` pointed at that folder, and deletes it afterwards. On Linux, .NET's ApplicationData is `$XDG_CONFIG_HOME`.
- `config_path_in_sandbox = true` proves the harness never read a real settings file.
- Evidence: `evidence/config-store/stepN-store.json`.

## Gotchas

- Scenario configs are written in TOML with snake_case keys and converted to the app's camelCase JSON. Use `raw = "..."` to test exact bytes.
- Use `YOUR_HOST` or example names only. This repo is public.
