using System.Net;
using System.Net.Http;
using System.Text;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class OpenAiCompatibleClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return fn(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LlmProfile Profile() => new()
    {
        Id = "t", Name = "t", BaseUrl = "https://api.test/v1",
        EncryptedApiKey = SecretProtector.Protect("sk-test"), Model = "m1"
    };

    [Fact] public async Task Chat_发送正确请求并解析内容与usage()
    {
        var handler = new FakeHandler(_ => Json("""
            {"choices":[{"message":{"content":"{\"suggestions\":[]}"}}],
             "usage":{"prompt_tokens":123,"completion_tokens":45}}
            """));
        var client = new OpenAiCompatibleClient(handler);
        var resp = await client.ChatAsync(Profile(), "sys", "user");

        Assert.Equal("{\"suggestions\":[]}", resp.Content);
        Assert.Equal(123, resp.Usage.InputTokens);
        Assert.Equal(45, resp.Usage.OutputTokens);

        // 请求侧:URL、鉴权、json_object、模型名、两条消息
        Assert.Equal("https://api.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("\"model\":\"m1\"", handler.LastBody);
        Assert.Contains("json_object", handler.LastBody);
        Assert.Contains("\"role\":\"system\"", handler.LastBody);
        Assert.Contains("\"role\":\"user\"", handler.LastBody);
    }

    [Fact] public async Task Chat_无密钥档案不发Authorization头()
    {
        var handler = new FakeHandler(_ => Json("""{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));
        var client = new OpenAiCompatibleClient(handler);
        var p = Profile() with { EncryptedApiKey = "" };
        await client.ChatAsync(p, "s", "u");
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact] public async Task Chat_API错误时抛出带状态码的异常()
    {
        var handler = new FakeHandler(_ => Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized));
        var client = new OpenAiCompatibleClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(Profile(), "s", "u"));
        Assert.Contains("401", ex.Message);
    }

    [Fact] public async Task Chat_usage缺失时Token记0()
    {
        var handler = new FakeHandler(_ => Json("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var client = new OpenAiCompatibleClient(handler);
        var resp = await client.ChatAsync(Profile(), "s", "u");
        Assert.Equal(0, resp.Usage.InputTokens);
    }

    [Fact] public async Task ListModels_返回模型Id列表()
    {
        var handler = new FakeHandler(_ => Json("""{"object":"list","data":[{"id":"deepseek-v4-flash"},{"id":"deepseek-v4-pro"}]}"""));
        var client = new OpenAiCompatibleClient(handler);
        var models = await client.ListModelsAsync(Profile());
        Assert.Equal(new[] { "deepseek-v4-flash", "deepseek-v4-pro" }, models);
        Assert.Equal("https://api.test/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }
}
