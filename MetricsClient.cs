using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmHostMonitor;

public static class MetricsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private const double ActiveUtilPct = 2.0;
    private const double ActivePowerW = 40.0;

    private static readonly Dictionary<string, string> ModelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemma4"] = "Gemma 4",
        ["qwen3.5-27b"] = "Qwen 3.5-27B",
        ["qwen3.5-9b"] = "Qwen 3.5-9B",
        ["qwen3-coder-30b-a3b"] = "Qwen3-Coder-30B-A3B",
        ["qwen3-coder"] = "Qwen3-Coder",
    };

    private static readonly Dictionary<int, string> GpuNameByCode = new()
    {
        [3090] = "RTX 3090",
        [5060] = "RTX 5060 Ti",
    };

    private static readonly Regex LineRe = new(
        @"^(nvidia_smi_[A-Za-z0-9_]+)\{([^}]*)\}\s+([^\s]+)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex UuidRe = new(@"uuid=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex NameRe = new(@"name=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex IndexRe = new(@"index=""([^""]+)""", RegexOptions.Compiled);

    public static async Task<HostStatus> ProbeAsync(MachineConfig m, CancellationToken ct)
    {
        var status = new HostStatus
        {
            MachineId = m.Id,
            Name = m.Name,
            CheckedAt = DateTimeOffset.Now,
        };
        var errors = new List<string>();

        try
        {
            var text = await Http.GetStringAsync(m.MetricsUrl.Trim(), ct);
            status.Gpus = ParsePrometheus(text);
            status.MetricsOk = status.Gpus.Count > 0;
            status.Active = status.Gpus.Any(IsActive);
            var vramSum = status.Gpus.Where(g => g.VramTotalBytes is > 0).Sum(g => g.VramTotalBytes!.Value);
            if (vramSum > 0) status.TotalVramBytes = vramSum;
            if (!status.MetricsOk) errors.Add("no GPUs in metrics");
        }
        catch (Exception ex)
        {
            errors.Add("metrics: " + ex.GetBaseException().Message);
        }

        try
        {
            var baseUrl = m.LlmBaseUrl.Trim().TrimEnd('/');
            var json = await Http.GetStringAsync(baseUrl + "/v1/models", ct);
            using var doc = JsonDocument.Parse(json);
            status.LlmOk = true;
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    if (row.TryGetProperty("id", out var idEl))
                    {
                        status.Model = idEl.GetString() ?? "";
                        if (row.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                        {
                            if (meta.TryGetProperty("n_ctx", out var nCtx) && nCtx.TryGetInt32(out var ctx))
                                status.ContextTokens = ctx;
                            else if (meta.TryGetProperty("n_ctx", out var nCtx64) && nCtx64.TryGetInt64(out var ctx64))
                                status.ContextTokens = (int)ctx64;
                        }
                        break;
                    }
                }
            }
            status.ModelLabel = LabelFor(status.Model);
        }
        catch (Exception ex)
        {
            errors.Add("llm: " + ex.GetBaseException().Message);
        }

        if (errors.Count > 0)
            status.Error = string.Join("; ", errors);
        return status;
    }

    private static string LabelFor(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "";
        if (ModelLabels.TryGetValue(modelId.Trim(), out var label)) return label;
        foreach (var kv in ModelLabels)
            if (modelId.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return modelId;
    }

    private static bool IsActive(GpuInfo g) =>
        (g.UtilPct is double u && u >= ActiveUtilPct) ||
        (g.PowerW is double p && p >= ActivePowerW);

    public static List<GpuInfo> ParsePrometheus(string text)
    {
        var byUuid = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = LineRe.Match(line);
            if (!m.Success) continue;
            var metric = m.Groups[1].Value;
            var labels = m.Groups[2].Value;
            var val = m.Groups[3].Value;
            var um = UuidRe.Match(labels);
            if (!um.Success) continue;
            var uuid = um.Groups[1].Value;
            if (!byUuid.TryGetValue(uuid, out var rec))
            {
                rec = new Dictionary<string, object> { ["uuid"] = uuid };
                byUuid[uuid] = rec;
            }
            if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
                rec[metric] = num;
            else
                rec[metric] = val;

            var nm = NameRe.Match(labels);
            if (nm.Success) rec["info_name"] = nm.Groups[1].Value;
            var ix = IndexRe.Match(labels);
            if (ix.Success && int.TryParse(ix.Groups[1].Value, out var idx))
                rec["info_index"] = idx;
        }

        return byUuid.Values
            .Select(Summarize)
            .OrderBy(g => g.Index)
            .ThenBy(g => g.Name)
            .ToList();
    }

    private static GpuInfo Summarize(Dictionary<string, object> raw)
    {
        double? used = AsDouble(raw, "nvidia_smi_memory_used_bytes");
        double? total = AsDouble(raw, "nvidia_smi_memory_total_bytes");
        double? vramPct = (used is double u && total is double t && t > 0) ? 100.0 * u / t : null;
        double? util = AsDouble(raw, "nvidia_smi_utilization_gpu_ratio");
        double? utilPct = util is double ur ? (ur <= 1.5 ? ur * 100.0 : ur) : null;
        double? power = AsDouble(raw, "nvidia_smi_power_draw_watts")
            ?? AsDouble(raw, "nvidia_smi_power_draw_instant_watts");
        double? temp = AsDouble(raw, "nvidia_smi_temperature_gpu");
        var nameCode = AsDouble(raw, "nvidia_smi_name");
        string name = "GPU";
        if (raw.TryGetValue("info_name", out var iname) && iname is string prettyName && prettyName.Length > 0)
            name = ShortGpuName(prettyName);
        else if (nameCode is double nc)
        {
            var code = (int)nc;
            name = GpuNameByCode.TryGetValue(code, out var pretty) ? pretty : $"GPU {code}";
        }

        var index = 0;
        if (raw.TryGetValue("info_index", out var iobj) && iobj is int ii) index = ii;
        else if (AsDouble(raw, "nvidia_smi_index") is double di) index = (int)di;

        return new GpuInfo
        {
            Name = $"{index}  {name}",
            Index = index,
            Uuid = raw.TryGetValue("uuid", out var uo) ? uo?.ToString() ?? "" : "",
            VramPct = vramPct is double vp ? Math.Round(vp, 1) : null,
            VramTotalBytes = total is double tb && tb > 0 ? (long)tb : null,
            UtilPct = utilPct is double up ? Math.Round(up, 1) : null,
            TempC = temp is double tc ? Math.Round(tc, 0) : null,
            PowerW = power is double pw ? Math.Round(pw, 1) : null,
        };
    }

    private static string ShortGpuName(string raw)
    {
        var s = raw.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Trim();
        return string.IsNullOrWhiteSpace(s) ? "GPU" : s;
    }

    private static double? AsDouble(Dictionary<string, object> raw, string key)
    {
        if (!raw.TryGetValue(key, out var v) || v is null) return null;
        if (v is double d) return d;
        if (v is float f) return f;
        if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
    }
}
