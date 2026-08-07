using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class SecretProtectorTests
{
    [Fact] public void 加密后可解密还原()
    {
        var cipher = SecretProtector.Protect("sk-test-key-123");
        Assert.NotEqual("sk-test-key-123", cipher);
        Assert.Equal("sk-test-key-123", SecretProtector.Unprotect(cipher));
    }

    [Fact] public void 空串往返仍是空串()
    {
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.Equal("", SecretProtector.Unprotect(""));
    }

    [Fact] public void 坏密文解密返回空串不抛异常()
    {
        Assert.Equal("", SecretProtector.Unprotect("!!!not-base64!!!"));
    }

    [Fact] public void 密文不含明文()
    {
        var cipher = SecretProtector.Protect("sk-super-secret");
        Assert.DoesNotContain("sk-super-secret", cipher);
    }
}
