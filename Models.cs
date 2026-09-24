namespace LlmHostMonitor;

public sealed class MachineConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "";
    public string MetricsUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AppConfig
{
    public int PollSeconds { get; set; } = 5;
    public List<MachineConfig> Machines { get; set; } = new();
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "GPU";
    public int Index { get; set; }
    public string Uuid { get; set; } = "";
    public double? VramPct { get; set; }
    public double? UtilPct { get; set; }
    public double? TempC { get; set; }
    public double? PowerW { get; set; }
}

public sealed class HostStatus
{
    public string MachineId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool MetricsOk { get; set; }
    public bool LlmOk { get; set; }
    public bool Active { get; set; }
    public string Model { get; set; } = "";
    public string ModelLabel { get; set; } = "";
    public string? Error { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
}

