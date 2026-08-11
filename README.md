# AI 内存管家 / AiMemoryManager

面向 Windows 10/11 的开源内存与磁盘管理工具，支持实时监控、进程管理、安全清理、C 盘瘦身、跨盘迁移和可选的大模型分析。

## 主要功能

- 内存仪表盘、进程列表、工作集清理和清理历史
- 进程白名单/黑名单、前台保护和操作审计
- C 盘空间扫描、候选分类、回收站清理和安全迁移
- 迁移校验、junction 创建、异常中断保护和回退
- 无 API Key 时使用本地规则；LLM 仅作为可选增强
- 默认不上传遥测，不包含开发者托管的 API Key

## 安全边界

- 删除操作仅进入 Windows 回收站，不降级为永久删除
- 系统目录、应用目录、junction/reparse point 和当前程序目录默认受保护
- LLM 不参与路径安全决策，也不能绕过本地保护规则
- 跨盘迁移必须完成源/目标快照、文件数和字节数校验
- 需要管理员权限的操作通过明确的 UAC 流程执行

## 构建与运行

需要 Windows、.NET 8 SDK 和 Windows 10/11 SDK（MSIX 打包时需要 SDK 工具）。

```powershell
dotnet test .\AiMemoryManager.sln --configuration Release
dotnet run --project .\src\AiMemoryManager\AiMemoryManager.csproj
```

## 生成无需证书的便携版

```powershell
.\packaging\build-portable.ps1 -Version 1.0.0.7 -Runtime win-x64
```

输出：

```text
artifacts/portable/AiMemoryManager_1.0.0.7_portable_win-x64.zip
```

解压后直接运行 `AiMemoryManager.exe`，不需要安装证书或 .NET Runtime。

## Microsoft Store 版

商店版使用 MSIX，由 Partner Center 提供正式 Identity/Publisher 并由 Microsoft Store 签名。提交前需要替换占位身份、发布正式隐私政策、采集无个人信息的截图并通过 Windows App Certification Kit。

```powershell
.\packaging\build-msix.ps1 -Version 1.0.0.7 -Runtime win-x64 -SelfContained -StoreCompatible
```

商店兼容构建默认关闭需要最高权限计划任务的高级待机列表清理，仅保留普通用户可理解、可确认的内存和磁盘功能；完整版仍可通过 GitHub 便携版使用。

## 隐私

本地配置、白名单、黑名单、分析缓存和清理历史默认保存在本机。只有用户主动配置并调用 LLM 时，必要的摘要数据才会发送到用户选择的端点。详细内容见 `packaging/privacy-policy.md`。

## 许可证

本项目使用 [MIT License](LICENSE)。第三方依赖按其各自许可证提供。
