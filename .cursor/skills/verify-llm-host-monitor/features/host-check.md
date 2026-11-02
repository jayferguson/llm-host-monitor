# Host probe script

`host/check.sh HOST` is the operator's quick check that a host serves what the app expects. It prints the model id, `n_ctx`, the GPU count and each metric, then ends with `OK` (exit 0) or `PROBLEMS FOUND` (exit 1).

## Sub-features

- `llama-server`: model id and n_ctx from `/v1/models`. An HTTP error or no answer prints "FAILED: no response".
- `exporter`: counts the GPUs, then prints `ok` or `missing` for five metric names.
- `verdict`: fails when the model id is missing or there are 0 GPUs. A missing metric name is reported but does not fail.

## How to get to it (user POV)

On any machine that can reach the host, run `./host/check.sh YOUR_HOST`.

## Driving it with verify.py

- `verify.py compose host-check --json`. The real script runs against each stub host address.
- A pass means:
  - `active`: exit 0 with the model id, `n_ctx:    32768` and `GPUs:     2`;
  - `idle`: exit 0;
  - `degraded`: exit 1, "FAILED: no response" and "missing   nvidia_smi_power_draw_watts";
  - `nogpu`: exit 1 with `GPUs:     0`;
  - `down`: exit 1.
- Evidence: `evidence/host-check/stepN-check_sh.txt` (full output and exit code).

## Gotchas

- The script hard-codes ports 8080 and 9835, which is why the stubs live on separate 127.77.0.N addresses.
- Output columns are space-aligned and the scenarios match them exactly ("GPUs:     2"). A cosmetic change to the script needs a scenario update.
