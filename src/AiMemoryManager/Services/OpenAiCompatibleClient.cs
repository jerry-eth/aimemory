using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class OpenAiCompatibleClient : ILlmClient
{
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(HttpMessageHandler? handler = null)
        => _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<LlmResponse> ChatAsync(LlmProfile profile, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        using var req = BuildRequest(profile, HttpMethod.Post, Base(profile.BaseUrl) + "/chat/completions");
        var payload = new
        {
            model = profile.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            response_format = new { type = "json_object" },
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM API 错误 {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        int input = 0, output = 0;
        if (doc.RootElement.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt)) input = pt.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var ctok)) output = ctok.GetInt32();
        }
        return new LlmResponse(content, new LlmUsage(input, output));
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmProfile profile, CancellationToken ct = default)
    {
        using var req = BuildRequest(profile, HttpMethod.Get, Base(profile.BaseUrl) + "/models");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM API 错误 {(int)resp.StatusCode}: {Truncate(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .ToList();
    }

    private static HttpRequestMessage BuildRequest(LlmProfile profile, HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        var key = SecretProtector.Unprotect(profile.EncryptedApiKey);
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    private static string Base(string baseUrl) => baseUrl.TrimEnd('/');
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
