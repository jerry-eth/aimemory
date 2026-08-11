# 发布与 Microsoft Store 材料

## 文件说明

- `Package.appxmanifest`：完整版 MSIX 清单，包含 `runFullTrust`、`allowElevation` 和自动启动扩展。
- `Package.Store.appxmanifest`：商店兼容清单，移除 `allowElevation` 和 `windows.startupTask`，只保留桌面应用所需的 `runFullTrust`。
- `build-msix.ps1`：支持完整版和 `-StoreCompatible` 两种构建；未提供证书时生成未签名包，提供证书时自动签名。
- `build-portable.ps1`：生成无需证书、无需安装的 self-contained ZIP。
- `create-test-certificate.ps1`：仅用于本机验证的开发证书脚本，生成的证书和密码不得提交 Git。
- `privacy-policy.md`：隐私政策发布前模板。
- `store-listing.md`：商店描述、版本差异、截图和审核清单。

## 生成完整版 MSIX

```powershell
.\packaging\build-msix.ps1 -Version 1.0.0.7 -Runtime win-x64 -SelfContained
```

输出：

```text
artifacts/msix/AiMemoryManager_1.0.0.7_win-x64.msix
```

## 生成 Microsoft Store 兼容 MSIX

```powershell
.\packaging\build-msix.ps1 `
  -Version 1.0.0.7 `
  -Runtime win-x64 `
  -SelfContained `
  -StoreCompatible `
  -Publisher "CN=Partner Center 分配的发布者" `
  -IdentityName "Partner Center 分配的 Identity"
```

输出：

```text
artifacts/msix/AiMemoryManager_1.0.0.7_store_win-x64.msix
```

商店兼容构建会：

- 使用 `Package.Store.appxmanifest`；
- 通过 `/p:StoreCompatible=true` 编译；
- 不复制 `AiMemoryManager.ElevatedHelper.exe`；
- 隐藏高级待机列表按钮，不创建最高权限计划任务；
- 自动规则在 L2 不可用时降级为 L1；
- 隐藏不受 Store 清单支持的自动启动设置。

Partner Center 会在正式提交时提供真实 `Identity`、`Publisher`、签名和分发信任，不要把个人测试证书当作正式发布证书。

## 生成无需证书的便携版

```powershell
.\packaging\build-portable.ps1 -Version 1.0.0.7 -Runtime win-x64
```

输出：

```text
artifacts/portable/AiMemoryManager_1.0.0.7_portable_win-x64.zip
```

便携版无需证书或 .NET 8 Runtime，解压后运行 `AiMemoryManager.exe`。它不提供 MSIX 的快捷方式、自动更新和商店集成；完整功能可使用该版本。

## 本机签名验证

未签名 MSIX 不能直接作为可信安装包分发。开发验证可使用临时证书：

```powershell
$env:AMM_TEST_CERT_PASSWORD = "仅本机临时密码"
.\packaging\create-test-certificate.ps1
.\packaging\build-msix.ps1 -Version 1.0.0.7 -SelfContained `
  -CertificatePath ".\artifacts\certs\AiMemoryManager-Test.pfx" `
  -CertificatePassword $env:AMM_TEST_CERT_PASSWORD
```

测试证书只用于本机，`.pfx`、私钥和密码不得提交 Git。跨设备测试优先复制无需证书的便携 ZIP；不要把私钥复制给测试人员。

## Store 提交前阻断项

- [ ] 用 Partner Center 分配的 `Identity Name`、`Publisher` 和正式版本号替换占位值。
- [ ] 将 `privacy-policy.md` 发布到真实可访问的 HTTPS 地址，并替换运营主体和联系邮箱。
- [ ] 采集不含个人信息、API Key、Token、证书和本机路径的真实截图。
- [ ] 检查 Store 清单没有 `allowElevation` 和 `windows.startupTask`。
- [ ] 通过 WACK，并完成普通用户安装、升级、卸载、UAC 取消、权限不足和迁移回退测试。
- [ ] 仅提交 Store 兼容包；完整版通过开源仓库和便携 ZIP 分发。

## 安全审计

```powershell
dotnet test .\AiMemoryManager.sln --configuration Release
dotnet build .\AiMemoryManager.sln --configuration Release
git diff --check
git ls-files | Select-String -Pattern '(^|/)(\.env|.*\.pfx|.*\.pem|.*\.key)$'
```

仓库不得包含 `.env`、API Key、Token、密码、`.pfx`、`.pem`、`.key` 或其他私钥材料。
