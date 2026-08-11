# MIT 开源与 Microsoft Store 版技术设计

## 1. 版本分层

| 版本 | 构建方式 | 高级待机列表 | 分发方式 |
|---|---|---|---|
| 完整版 | 默认 `dotnet publish` | 保留 | GitHub/NAS ZIP 或测试 MSIX |
| Store 兼容版 | `StoreCompatible=true` | 明确禁用 | Partner Center MSIX |

两种版本共享业务服务和安全边界，只通过编译常量、服务实现和清单模板隔离商店限制。

## 2. 代码策略

- `IL2Executor` 增加可用性声明。
- Store 模式注入 `UnavailableL2Executor`，不创建计划任务、不触发 UAC、不调用 `schtasks /create`。
- RuleEngine 在 L2 不可用时自动退回 L1，避免自动规则触发不可用操作。
- Dashboard 隐藏 L2 按钮；分析动作执行器对 `purge_standby` 返回明确失败结果。
- 完整版仍注入现有 `ElevatedL2Service`，保持行为兼容。

## 3. 打包策略

- `build-portable.ps1` 生成 self-contained ZIP，不签名、不安装。
- `build-msix.ps1 -StoreCompatible` 使用 `Package.Store.appxmanifest`，清单不包含 `allowElevation` 和自动启动扩展。
- Store 清单仍保留 WPF 桌面应用所需的 full-trust 声明，并使用 Partner Center 提供的 Identity/Publisher 替换占位值。
- 测试证书只保存在被忽略的 `artifacts/certs/`，不得进入 Git。

## 4. 隐私和发布材料

- 根目录 `README.md` 说明功能、安全边界、构建方式和 MIT 许可证。
- `packaging/privacy-policy.md` 保留数据流、DPAPI、BYOK 和第三方端点说明，并把占位联系信息列为提交阻断项。
- `packaging/store-listing.md` 增加商店版与完整版差异、权限说明、审核备注和截图要求。

## 5. 测试策略

- 单元测试：L2 可用性、Store 模式自动降级、动作拒绝和规则级别选择。
- 脚本测试：便携 ZIP 必含主程序/助手/说明；Store 清单不存在 `allowElevation` 和 startupTask。
- 构建测试：Debug/Release、完整版 MSIX、Store MSIX、便携 ZIP。
- 发布审计：`git ls-files` 敏感文件扫描、`git diff --check`、MakeAppx 解包、签名仅作为本机测试项。
- 真机审核：WACK、安装/升级/卸载、普通用户、UAC 取消、目标盘拔出和数据恢复。
