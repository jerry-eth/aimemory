using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>读取进程可执行文件是否带有 Authenticode 证书。
/// 这里展示的是“是否签名”状态，不把证书存在误报为微软或受信任发布者。
/// </summary>
public static class DigitalSignatureService
{
    public static ProcessSignatureStatus GetStatus(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ProcessSignatureStatus.Unknown;

        try
        {
            if (!File.Exists(path))
                return ProcessSignatureStatus.Unknown;

            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate is null
                ? ProcessSignatureStatus.Unsigned
                : ProcessSignatureStatus.Signed;
        }
        catch (CryptographicException)
        {
            return ProcessSignatureStatus.Unsigned;
        }
        catch (FileNotFoundException)
        {
            return ProcessSignatureStatus.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return ProcessSignatureStatus.Unknown;
        }
        catch (IOException)
        {
            return ProcessSignatureStatus.Unknown;
        }
    }
}
