using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class DigitalSignatureServiceTests
{
    [Fact]
    public void 空路径或不存在路径返回未知()
    {
        Assert.Equal(ProcessSignatureStatus.Unknown, DigitalSignatureService.GetStatus(null));
        Assert.Equal(ProcessSignatureStatus.Unknown, DigitalSignatureService.GetStatus(@"C:\does-not-exist\missing.exe"));
    }

    [Fact]
    public void 无签名的普通文件不会抛异常()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aimemory-signature-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            Assert.Equal(ProcessSignatureStatus.Unsigned, DigitalSignatureService.GetStatus(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
