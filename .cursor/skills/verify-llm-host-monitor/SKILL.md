---
name: verify-llm-host-monitor
description: "Prove llm-host-monitor changes on Linux without touching real hosts. The skill runs the app's real polling/parsing and settings code (MetricsClient.cs, MachineStore.cs, compiled into a headless .NET harness) and host/check.sh against stub llama-server and GPU-exporter endpoints on spare 127.x addresses, and gives a checker-side gate verdict. Use before claiming an llm-host-monitor change works or when reviewing one. The WinForms window itself is build-checked only."
---

# Verify llm-host-monitor

This is a project-local control CLI for this repo. The app is a Windows WinForms window and cannot run on Linux, so the skill proves the layer that decides what each card shows:

- `MetricsClient.ProbeAsync` and `ParsePrometheus`
- `MachineStore.Load`
- the operator script `host/check.sh`

It launches stub hosts that serve fixture payloads, runs the real code against them, compares the results with expectations, captures evidence, and gives a gate verdict.

The engine is `verify.py`. Everything app-specific is data:

- `checks/app.toml`: stub hosts, ports, harness links and gate commands
- `checks/fixtures/*.toml`: `/v1/models` and `/metrics` payloads
- `checks/scenarios/<feature>.toml`: one journey per feature

When the app changes, edit the data, not the engine.

**Hard rules**

- Stubs only. Drive and gate talk only to `127.x` addresses this skill launched, and `http_get` refuses any other URL. Never run `host/install-*.sh` from here.
- `probe-live HOST --yes-read-only` is the only path to a real host. It sends two GETs (`/v1/models`, `/metrics`), changes nothing, and never runs in drive or gate.
- Don't edit product code from this skill. Only `.cursor/skills/verify-llm-host-monitor/` changes; the gate's `scope_check` enforces this.
- **This repo is public.** Don't put real host names, tailnet names, IPs or GPU UUIDs in fixtures. Docs use `YOUR_HOST`. `scan_diff.py` is a gate check.

## Launch

```bash
cd <llm-host-monitor checkout>
python3 .cursor/skills/verify-llm-host-monitor/verify.py launch --json     # --plan prints the address plan only
```

Launch starts one background process (its own process group). It serves each fixture host from `[[hosts]]` on a spare `127.77.0.N` address, on both of the app's fixed ports (8080 and 9835). `host/check.sh` hard-codes those ports, so each stub needs its own address. The `down` host gets a spare address with nothing listening.

Launch writes `scratch/instance.state` (pid, host→address map) and `scratch/stubs.log` (every request). Running it again while the stubs are alive reuses them.

## Doctor

```bash
python3 .cursor/skills/verify-llm-host-monitor/verify.py doctor --json
```

Doctor is read-only:

- `dotnet` (`VERIFY_DOTNET`, else `~/.dotnet/dotnet`, else PATH), `bash` and `curl` are present.
- Every stub endpoint returns its fixture byte-for-byte with the fixture's status (the `degraded` llama-server returns 500 on purpose).
- The `down` address really has nothing listening.

Drive runs doctor first and refuses to continue if it fails.

## Drive

```bash
python3 .cursor/skills/verify-llm-host-monitor/verify.py features                       # map + consistency
python3 .cursor/skills/verify-llm-host-monitor/verify.py drive probe-active --json      # one journey
python3 .cursor/skills/verify-llm-host-monitor/verify.py drive all --json               # all journeys
python3 .cursor/skills/verify-llm-host-monitor/verify.py drive host-check --plan        # print steps only
python3 .cursor/skills/verify-llm-host-monitor/verify.py compose all --json             # launch -> doctor -> drive -> cleanup
```

Drive first builds the harness into `scratch/harness/`, in a few seconds once cached. The harness is a net9.0 console app that compiles `MetricsClient.cs`, `Models.cs` and `MachineStore.cs` by link, with warnings treated as errors. Drive then runs each step.

| Feature | Journey (checks/scenarios/) | Proves |
|---|---|---|
| `probe-active` | `probe-active.toml` | ACTIVE; "Qwen3-Coder-30B-A3B"; 32768 ctx; 40 GiB total; GPU rows sorted by index with VRAM %, util, temp and power |
| `probe-idle` | `probe-idle.toml` | IDLE thresholds; GPU name from legacy code 3090; "Gemma 4" |
| `probe-failures` | `probe-failures.toml` | No GPUs, llama-server 500 and host down each give their own error; partial data survives; instant-power fallback |
| `host-check` | `host-check.toml` | `host/check.sh` exits 0 with OK on healthy hosts and 1 with PROBLEMS FOUND on degraded, no-GPU and down hosts |
| `config-store` | `config-store.toml` | `machines.json` default, missing, empty and corrupt cases fall back; UiScale clamped to 0.7–2.0 (0 becomes 1.0); camelCase keys load |
| `prometheus-parse` | `prometheus-parse.toml` | CRLF, comments, uuid-less series, `N/A`, percent-vs-ratio utilization, unknown name code |

Step vocabulary (checked by the gate):

- `probe`: run `ProbeAsync` over stub hosts.
- `parse`: run `ParsePrometheus` on a fixture.
- `check_sh`: run the real script against a stub address.
- `store`: run `MachineStore.Load` with a throwaway `XDG_CONFIG_HOME`.
- `expect_http`: GET a stub.

Expectations name harness JSON fields; `_contains`, `_absent` and `gpu_count` are supported. Every step may carry `why` and `timeout_s`.

**Dry-run** is always on. Nothing writes outside `scratch/` and `evidence/`. Store steps use a throwaway config folder that is deleted afterwards, and each `store` step asserts `config_path_in_sandbox`.

**Not covered:** the window itself (cards, colors, menus, text size, title bar, Add Machine dialog). On Linux the gate only proves that it builds with 0 warnings. Report UI changes as build-checked only.

## Evidence

`.cursor/skills/verify-llm-host-monitor/evidence/<feature>/` holds:

- `result.json`: each step's expected-vs-observed detail and the observed values
- the raw `stepN-probe.json`, `stepN-store.json` and `stepN-check_sh.txt` outputs

`evidence/gate/result.json` holds the last gate verdict. `evidence/` and `scratch/` are gitignored, and the gate fails if either is tracked.

## Cleanup

```bash
python3 .cursor/skills/verify-llm-host-monitor/verify.py cleanup --json
```

Cleanup kills only the process group in `scratch/instance.state`, and only if its cmdline still contains `verify.py _serve`. It never kills by name. `evidence/` is kept; the output reports `evidence_present`.

## Gate (checker-side)

```bash
python3 .cursor/skills/verify-llm-host-monitor/verify.py gate --json
python3 .cursor/skills/verify-llm-host-monitor/verify.py gate --diff-base HEAD~1 --allow-path 'MetricsClient.cs' --json   # reviewing a product change
```

The gate reports:

- `verdict`
- `failed_check`
- `suggestion`: what to fix first, naming the failing journey step
- `checks`
- `warnings`

It never fixes code. A false pass is worse than a false fail: a missing dotnet, a harness build error, a crashed step, a stub launch failure or a missing merge-base all count as fails.

1. `tests`: every journey against freshly launched stubs, which are always torn down. The repo has no test project.
2. `typecheck`:
   - `dotnet build LlmHostMonitor.csproj -c Release -p:EnableWindowsTargeting=true` must print `0 Warning(s)` and `0 Error(s)`. This is the only check on the WinForms UI code.
   - `bash -n` on `host/*.sh`.
   - `compile()` on the skill's Python.
   - The scenario schema: known steps and keys, known hosts and fixtures, an assertion in every journey, every feature file driven and linked, and the four H2s.
   - The harness build (warnings as errors) runs as part of `tests`.
3. `lint`: `dotnet format whitespace LlmHostMonitor.csproj --verify-no-changes`.
4. `private_scan`: `scan_diff.py` checks every line the branch adds against `checks/private_scan.toml`. It looks for:
   - emails, IPv4 addresses (loopback and documentation ranges allowed) and `*.ts.net` names
   - real-looking GPU UUIDs (only the all-zero fixture pattern is allowed)
   - token shapes and home paths
   - 9+ digit runs (only the three synthetic VRAM totals are allowed)
   - `machines.json`, `*.local.*` and `_shots/`

   Set `PRIVATE_PATTERNS_FILE` to a file outside the repo with extra regexes (host names) for a stricter scan.
5. `scope_check`:
   - Changes against `merge-base HEAD origin/main` must stay under this skill dir or an `--allow-path` glob.
   - `evidence/` and `scratch/` must not be tracked.
   - The gate must not dirty the tree (`gate_side_effects`).

   Pushing straight to `main` is allowed in this repo, so after pushing, review a commit with `--diff-base <commit>~1`.

Red proofs (2026-11-02, a throwaway clone, deleted afterwards):

- Lowering `ActiveUtilPct` from 2.0 to 0.5 in `MetricsClient.cs` failed `tests` at `probe-idle` step 0 (`active=True, expected False`), and `scope_check` listed `MetricsClient.cs`.
- Breaking the `n_ctx` grep in `host/check.sh` failed `tests` at `host-check` step 0.
- Adding a tailnet-style host name to a fixture failed `private_scan`.

Green on `main` 43136f3 plus this skill: all 5 checks passed, 6/6 journeys, and the app build reported 0 warnings and 0 errors.

## Helpers

`verify.py --help` and `verify.py <cmd> --help` list every flag; all subcommands take `--json`/`-j`. Run `scan_diff.py --help` for the leak scan. The feature map is in `features/README.md`. Keep the map honest with `/maintain-verification-skill`.
