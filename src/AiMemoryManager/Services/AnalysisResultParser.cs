using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public static class AnalysisResultParser
{
    public static IReadOnlyList<AnalysisSuggestion> Parse(string llmContent)
    {
        try
        {
            int start = llmContent.IndexOf('{');
            int end = llmContent.LastIndexOf('}');
            if (start < 0 || end <= start) return Array.Empty<AnalysisSuggestion>();
            using var doc = JsonDocument.Parse(llmContent[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr)) return Array.Empty<AnalysisSuggestion>();

            var list = new List<AnalysisSuggestion>();
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("process", out var pn) || pn.GetString() is not { Length: > 0 } name) continue;
                string action = item.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                string reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                string risk = item.TryGetProperty("risk", out var rk) ? rk.GetString() ?? "" : "";
                list.Add(new AnalysisSuggestion(name, NormAction(action), reason, NormRisk(risk)));
            }
            return list;
        }
        catch { return Array.Empty<AnalysisSuggestion>(); }
    }

    private static string NormAction(string a) => a.ToLowerInvariant() switch
    { "compress" => "compress", "terminate" => "terminate", "keep" => "keep", _ => "keep" };

    private static string NormRisk(string r) => r.ToLowerInvariant() switch
    { "low" => "low", "medium" => "medium", "high" => "high", _ => "medium" };
}
