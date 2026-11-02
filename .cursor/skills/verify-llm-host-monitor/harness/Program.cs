// verify-llm-host-monitor harness: runs the app's real MetricsClient / MachineStore
// code (compiled by link from the repo root) headlessly on Linux and prints JSON.
//   verify-harness probe <machines.json>   -> [HostStatus...]   (MetricsClient.ProbeAsync)
//   verify-harness parse <metrics.txt>     -> [GpuInfo...]      (MetricsClient.ParsePrometheus)
//   verify-harness store                   -> AppConfig + ConfigPath (MachineStore.Load; set XDG_CONFIG_HOME)
using System.Text.Json;
using LlmHostMonitor;

var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
if (args.Length == 0) { Console.Error.WriteLine("usage: verify-harness probe|parse|store [file]"); return 2; }
switch (args[0])
{
    case "probe":
        var machines = JsonSerializer.Deserialize<List<MachineConfig>>(File.ReadAllText(args[1]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var results = new List<HostStatus>();
        foreach (var m in machines)
            results.Add(await MetricsClient.ProbeAsync(m, CancellationToken.None));
        Console.WriteLine(JsonSerializer.Serialize(results, opts));
        return 0;
    case "parse":
        Console.WriteLine(JsonSerializer.Serialize(MetricsClient.ParsePrometheus(File.ReadAllText(args[1])), opts));
        return 0;
    case "store":
        var cfg = MachineStore.Load();
        Console.WriteLine(JsonSerializer.Serialize(new { config_path = MachineStore.ConfigPath, config = cfg }, opts));
        return 0;
    default:
        Console.Error.WriteLine("unknown mode " + args[0]);
        return 2;
}
