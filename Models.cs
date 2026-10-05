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
    public bool AlwaysOnTop { get; set; }
    /// <summary>True = systems side-by-side (each a column). False = stacked (each a row).</summary>
    public bool HorizontalLayout { get; set; }
    /// <summary>True = borderless window (no title bar). Drag anywhere to move; edges resize.</summary>
    public bool HideTitleBar { get; set; }
    /// <summary>Card text and metric scale. 1.0 is the design size.</summary>
    public double UiScale { get; set; } = 1.0;
    public List<MachineConfig> Machines { get; set; } = new();

    public const double UiScaleMin = 0.7;
    public const double UiScaleMax = 2.0;
    public const double UiScaleStep = 1.1;

    /// <summary>Missing, 0, NaN, and infinities are 1.0. Anything else is clamped to [0.7, 2.0].</summary>
    public static double NormalizeUiScale(double scale)
    {
        if (!double.IsFinite(scale) || scale == 0d)
            return 1d;
        return Math.Clamp(scale, UiScaleMin, UiScaleMax);
    }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "GPU";
    public int Index { get; set; }
    public string Uuid { get; set; } = "";
    public double? VramPct { get; set; }
    public long? VramTotalBytes { get; set; }
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
    public long? TotalVramBytes { get; set; }
    public int? ContextTokens { get; set; }
    public string? Error { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
}
