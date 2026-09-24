# LLM Host Monitor

Resizable Windows (.NET 9 WinForms) dashboard for local LLM hosts: model status plus per-GPU VRAM, utilization, temperature, and power.

## Requirements

- Windows
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (to build)

## Build and run

```powershell
dotnet build -c Release
.\bin\Release\net9.0-windows\LlmHostMonitor.exe
```

Or:

```powershell
dotnet run -c Release
```

## Endpoints expected per machine

| Role | Typical URL |
|------|-------------|
| LLM base (OpenAI-compatible) | `http://HOST:8080` |
| Chat | `http://HOST:8080/v1/chat/completions` |
| Models | `http://HOST:8080/v1/models` |
| Health | `http://HOST:8080/health` |
| GPU metrics (Prometheus / nvidia_smi exporter) | `http://HOST:9835/metrics` |

Ships with one example machine pointing at `127.0.0.1`. Use **Add** to register more hosts.

Config is stored at `%AppData%\LlmHostMonitor\machines.json` (not committed).

## Features

- Vertical stack of resizable host cards
- Header: machine name, model, ACTIVE / IDLE / DOWN
- GPU rows (one per device): index, name, temp, power, VRAM, util
- Add / Edit / Remove machines (icon toolbar)
- Poll interval configurable in config (default 5s)

## License

MIT
