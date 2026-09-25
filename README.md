# LLM Host Monitor

A small Windows desktop app that shows the status of your local LLM hosts as a vertical stack of cards. Each card shows the host state (ACTIVE / IDLE / DOWN), the loaded model, its context size, total VRAM, and per-GPU VRAM use, utilization, temperature and power.

It is built for Linux boxes running [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` on NVIDIA GPUs.

## How it works

The Windows app polls two HTTP endpoints on each host (every 5 seconds by default):

| Endpoint | Served by | Used for |
|----------|-----------|----------|
| `http://YOUR_HOST:8080/v1/models` | llama.cpp `llama-server` | model name (`data[0].id`) and context size (`data[0].meta.n_ctx`) |
| `http://YOUR_HOST:9835/metrics` | [nvidia_gpu_exporter](https://github.com/utkuozdemir/nvidia_gpu_exporter) | per-GPU metrics (below) |

From the exporter it reads these metrics, grouped by the GPU `uuid` label:

- `nvidia_smi_memory_used_bytes`, `nvidia_smi_memory_total_bytes` (VRAM %, total VRAM)
- `nvidia_smi_utilization_gpu_ratio` (utilization)
- `nvidia_smi_temperature_gpu` (temperature)
- `nvidia_smi_power_draw_watts`, or `nvidia_smi_power_draw_instant_watts` if that is missing (power)
- `nvidia_smi_index` and `nvidia_smi_name`, plus the `index` / `name` labels on `nvidia_smi_gpu_info` when present (GPU order and name)

Nothing runs on the Windows side except the app. There is no agent to install on Windows and nothing to install on the hosts beyond the two services above.

## Host setup (Linux)

Scripts and a unit template are in [`host/`](host/). They assume Ubuntu or another systemd-based distro on x86_64.

### Prerequisites

- NVIDIA driver installed, with `nvidia-smi` working.
- A CUDA build of llama.cpp, for example in `/opt/llama.cpp` (binary at `/opt/llama.cpp/build/bin/llama-server`).
- A GGUF model, for example `/models/qwen3-coder-30b-a3b-q4_k_m.gguf`.
- A service account for llama-server, for example: `sudo useradd -r -m llm` (it needs read access to the model file).

### 1. GPU exporter

```bash
sudo ./host/install-gpu-exporter.sh
```

This downloads nvidia_gpu_exporter v1.14.0 (linux x86_64) from GitHub, checks it against the release checksums, installs it to `/usr/local/bin`, and sets up the `nvidia-gpu-exporter` systemd service on port 9835. It is safe to re-run. Set `VERSION=...` to use a different release.

### 2. llama-server

Either render the template with the install script:

```bash
sudo MODEL=/models/qwen3-coder-30b-a3b-q4_k_m.gguf \
     LLAMA_BIN=/opt/llama.cpp/build/bin/llama-server \
     ALIAS=qwen3-coder-30b CTX=32768 RUN_USER=llm \
     ./host/install-llama-server.sh
```

Add `TENSOR_SPLIT=50,50` to split the model across two GPUs.

Or edit it by hand: copy [`host/llama-server.service.example`](host/llama-server.service.example) to `/etc/systemd/system/llama-server.service`, replace the `@PLACEHOLDERS@`, then run:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now llama-server
```

The template binds `0.0.0.0:8080` and uses full GPU offload, flash attention, a q4_0 KV cache and one slot. Adjust the flags for your model and VRAM. The alias you set is the model name the app shows.

### 3. Firewall

Open TCP 8080 and 9835 to the machine running the app. With ufw:

```bash
sudo ufw allow 8080/tcp
sudo ufw allow 9835/tcp
```

Neither endpoint has authentication. Only expose them on a trusted LAN or VPN, for example `sudo ufw allow from 192.0.2.0/24 to any port 8080 proto tcp`.

### 4. Verify

```bash
./host/check.sh YOUR_HOST
```

It checks that `/v1/models` returns a model id and `n_ctx`, and that `/metrics` lists at least one GPU with the metrics above. Or check by hand:

```bash
curl -s http://YOUR_HOST:8080/v1/models
curl -s http://YOUR_HOST:9835/metrics | grep '^nvidia_smi_memory_total_bytes'
```

## Client setup (Windows)

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

Clone this repository and run from its root folder:

```powershell
dotnet run -c Release
```

Or publish an exe (needs the .NET 9 Desktop Runtime on the target PC):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
.\publish\LlmHostMonitor.exe
```

### Using it

- **Add machine** (+): enter a name, the LLM base URL (`http://YOUR_HOST:8080`) and the GPU metrics URL (`http://YOUR_HOST:9835/metrics`).
- **Edit** / **Remove**: select a card first.
- **Refresh**: poll all hosts now.
- **Pin**: toggles always-on-top. The setting is saved.

On first run the app adds one example machine pointing at `127.0.0.1`. Edit or remove it.

### Config

Settings are saved to `%AppData%\LlmHostMonitor\machines.json`:

```json
{
  "pollSeconds": 5,
  "alwaysOnTop": false,
  "machines": [
    {
      "id": "a1b2c3d4",
      "name": "gpu-box",
      "llmBaseUrl": "http://YOUR_HOST:8080",
      "metricsUrl": "http://YOUR_HOST:9835/metrics",
      "enabled": true
    }
  ]
}
```

`pollSeconds` can only be changed in this file (minimum 2). Restart the app after editing it.

## Troubleshooting

- **DOWN** means neither endpoint answered within 3 seconds. Check the host is up, both services are running (`systemctl status llama-server nvidia-gpu-exporter`), and the firewall allows 8080 and 9835.
- **IDLE** means at least one endpoint answered and no GPU is busy. A card is **ACTIVE** when any GPU is at 2% or more utilization or draws 40 W or more. If only one endpoint answers, the card still shows IDLE or ACTIVE with an error line (`llm: ...` or `metrics: ...`) naming the one that failed.
- **No model name**: llama-server is not reachable, or is still loading the model. Large models can take a minute; watch `journalctl -u llama-server -f`.
- **"no GPUs in metrics"**: the exporter answered but returned no `nvidia_smi_*` lines with a `uuid` label. Run `nvidia-smi` on the host as a check; if it fails, fix the driver. Check `journalctl -u nvidia-gpu-exporter`. Make sure the metrics URL ends in `/metrics` and points at port 9835, not llama-server's own `/metrics` on 8080.

## License

MIT. See [LICENSE](LICENSE).
