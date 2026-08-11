using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class OpenAiCompatibleClient : ILlmClient
{
    /// <summary>LLM 生成可能较慢，默认允许最多等待 5 分钟，避免 60 秒硬超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "LLM 请求超时时间必须大于 0。");

        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = _timeout };
    }

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

        using var resp = await SendAsync(req, ct);
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
        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM API 错误 {(int)resp.StatusCode}: {Truncate(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var seconds = Math.Round(_timeout.TotalSeconds);
            throw new TimeoutException($"LLM 请求超过 {seconds:0} 秒仍未返回，请稍后重试或检查模型服务。");
        }
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
