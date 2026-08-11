# M4 MSIX 打包与商店材料

## 目录

- `Package.appxmanifest`：MSIX 清单，包含 `runFullTrust`、`allowElevation` 和 `StartupTask` 声明。
- `build-msix.ps1`：先发布 WPF 主程序，再生成 MSIX；未提供证书时生成未签名包，提供证书时自动签名。
- `create-test-certificate.ps1`：仅用于本机验证的开发证书脚本。生成的 `.pfx`、密码和 `.cer` 不得提交 Git。
- `Assets/`：应用图标和商店素材基础图。
- `privacy-policy.md`：商店隐私政策初稿，发布前需替换联系邮箱和隐私政策公开地址。
- `store-listing.md`：商店标题、简介、功能说明和截图清单。

## 构建 MSIX

在仓库根目录执行：

```powershell
# 只生成未签名包
.\packaging\build-msix.ps1 -Version 1.0.0.0 -SelfContained

# 使用已有签名证书
.\packaging\build-msix.ps1 `
  -Version 1.0.0.0 `
  -Publisher "CN=你的 Partner Center 发布者名称" `
  -CertificatePath "C:\secure\AiMemoryManager.pfx" `
  -CertificatePassword $env:AMM_CERT_PASSWORD `
  -SelfContained
```

脚本会自动寻找 Windows SDK 中的 `makeappx.exe` 和 `signtool.exe`。输出位于 `artifacts/msix/`。签名证书、私钥和密码只允许从本机安全位置或环境变量传入。

## 本机安装验证

未签名包不能直接安装。开发验证可以生成本机证书：

```powershell
$env:AMM_TEST_CERT_PASSWORD = "只用于本机验证的临时密码"
.\packaging\create-test-certificate.ps1
.\packaging\build-msix.ps1 -Version 1.0.0.0 -SelfContained `
  -CertificatePath ".\artifacts\certs\AiMemoryManager-Test.pfx" `
  -CertificatePassword $env:AMM_TEST_CERT_PASSWORD
```

然后在目标设备上**以管理员身份**将对应 `.cer` 导入“本地计算机\受信任的根证书颁发机构”，再双击 MSIX 安装。例如（管理员 PowerShell）：`Import-Certificate -FilePath .\\artifacts\\certs\\AiMemoryManager-Test.cer -CertStoreLocation Cert:\\LocalMachine\\Root`。仅导入当前用户证书存储可能导致 `0x800B0109`，无法通过 AppX 部署信任校验。复制到其他设备时只复制 `.msix` 和 `.cer`，不要复制包含私钥的 `.pfx`。开发证书不能用于商店发布。

## 生成无需证书的开源便携版

MSIX 需要签名证书；如果不希望处理证书，可以分发自包含 ZIP 便携版。该版本无需安装，解压后直接运行，适合开源项目和跨设备测试：

```powershell
.\packaging\build-portable.ps1 -Version 1.0.0.7 -Runtime win-x64
```

输出：`artifacts/portable/AiMemoryManager_1.0.0.7_portable_win-x64.zip`。
便携版不提供 MSIX 的快捷方式、自动更新和商店集成；正式发布前仍应提供开源许可证文件（例如 MIT 或 Apache-2.0）。
## Store 提交前必须替换的内容

- 使用 Partner Center 分配的 `Identity Name`、`Publisher` 和正式版本号。
- 提供正式签名证书，不要使用开发证书。
- 将 `privacy-policy.md` 发布到可公开访问的 HTTPS 地址，并在 Partner Center 填写地址。
- 使用真实应用截图替换商店素材清单中的待采集项。
- 在 Partner Center 对 `runFullTrust`、`allowElevation` 和进程级内存管理用途进行能力说明。
- 完成 Microsoft Store Certification Kit、安装/卸载/升级和 Windows 10/11 真机验证。
