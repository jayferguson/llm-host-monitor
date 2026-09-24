# LLM Host Monitor

Resizable Windows WinForms app that shows the GPU status dashboards GPU header strip as **vertical machine cards**.

## Run
```

```
Or from source:
```
cd 
dotnet run -c Release
```

## Defaults
| Machine | LLM | GPU metrics |
|---------|-----|-------------|
| 
| 

Config persists at `%AppData%\LlmHostMonitor\machines.json`.

## Features
- Vertical stack of host cards (ACTIVE / IDLE / DOWN)
- Model label from `/v1/models`
- Per-GPU VRAM, util, temp, power from Prometheus exporter
- Add / Edit / Remove machines
- Auto-refresh every N seconds (default 5)

