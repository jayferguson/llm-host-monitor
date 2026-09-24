using System.Text.Json;

namespace LlmHostMonitor;

public static class MachineStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LlmHostMonitor", "machines.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg?.Machines is { Count: > 0 }) return cfg;
            }
        }
        catch { }
        return DefaultConfig();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    public static AppConfig DefaultConfig() => new()
    {
        PollSeconds = 5,
        Machines =
        [
            new MachineConfig
            {
                Id = "llm-1",
                Name = "llm-1",
                LlmBaseUrl = "http://127.0.0.1:8080",
                MetricsUrl = "http://127.0.0.1:9835/metrics",
            },
        ],
    };
}
