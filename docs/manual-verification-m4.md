# M4 上架前人工验证清单

> 当前清单对应 2026 年 8 月 10 日的 M4 工程产物。自动化验证已完成，涉及桌面交互、UAC 和真实商店发布者信息的项目仍需上架前人工确认。

## 1. MSIX 构建与安装

- [x] `packaging/build-msix.ps1` 可生成 framework-dependent MSIX。
- [x] `packaging/build-msix.ps1 -SelfContained` 可生成自包含 MSIX（约 77 MB）。
- [x] Windows SDK `makeappx.exe` 校验清单并成功打包。
- [x] 使用本机临时证书签名后，`Add-AppxPackage` 安装成功，包状态为 `Ok`。
- [x] 已安装包可通过 `AppsFolder` 激活，进程路径位于 `C:\Program Files\WindowsApps\...`，主窗口可响应。
- [x] 使用正式 Partner Center Publisher/Identity 替换测试身份后重新打包。（2026-08-21 已完成：Identity `yklh.AI`、Publisher `CN=314D666A-7A85-4F13-A7D5-0AD3D8A7D3B0`，Store 版 MSIX `AiMemoryManager_1.0.0.0_store_win-x64.msix` 77.1 MB，包内清单已核对）
- [ ] 在干净的 Windows 10/11 设备上验证安装、升级、卸载和数据保留策略。

## 2. 能力声明与权限

- [x] 清单包含 `runFullTrust`。
- [x] 清单包含 `allowElevation`。
- [x] App Certification Kit 的清单、资源、UAC、特殊用途功能等关键检查通过。
- [x] 使用正式签名和 Partner Center 账户提交能力说明，解释托盘常驻、WPF 全信任和用户确认后的管理员级清理用途。（2026-08-21 已在提交选项中填写 runFullTrust 中英文说明并保存；正式签名由商店提交流程处理）
- [ ] 在标准用户账户上人工确认 L2/L3 操作仍会显示 UAC，并且取消 UAC 不会导致软件退出。

## 3. StartupTask / 开机自启

- [x] 清单声明 `desktop:Extension Category="windows.startupTask"`，TaskId 为 `AiMemoryManagerStartup`。
- [x] 未打包运行时保留 HKCU Run 回退逻辑。
- [x] MSIX 运行时使用 `Windows.ApplicationModel.StartupTask`，不把打包态错误写入 HKCU Run。
- [ ] 在已安装 MSIX 的设置页打开开机自启，确认状态变为 Enabled。
- [ ] 关闭开机自启，确认状态变为 Disabled，并确认重启登录后行为符合预期。

## 4. 商店素材与隐私政策

- [x] 已准备五种基础图标资产：StoreLogo、Square44、Square150、Square310、Wide310x150。
- [x] 已准备商店标题、简介、功能描述和能力说明草稿。
- [x] 已准备隐私政策初稿，说明 BYOK 数据传输、DPAPI、遥测、提权和第三方服务责任。
- [x] 采集五张截图：仪表盘、进程、智能分析、C 盘瘦身、设置。（2026-08-21 本机采集于 `artifacts/storeshots/`，已对用户目录路径打马赛克脱敏；干净环境重采可在过审后随意愿更新）
- [x] 将隐私政策中的运营主体、联系邮箱和 HTTPS 公开地址替换为真实信息。（运营主体 yklh（个人开发者）、li_snow@qq.com，已发布至 https://jerry-eth.github.io/aimemory/privacy-policy.html ）
- [x] 在 Partner Center 填写年龄分级、隐私政策地址、能力说明和商店素材。（2026-08-22 提交 1152921505701713636 六分区全部完成后**已点击「提交进行认证」，状态「正在认证」**：定价免费/CNY/全球、属性（实用工具+工具/隐私声明+URL/生成式AI声明）、IARC 年龄分级 3+/Everyone、MSIX 包已 Validated+Desktop 设备族、zh-CN 与 en-US 双语一览各 5 张截图、runFullTrust 说明、认证说明；IARC 证书 ID 待定，认证结果邮件发至 li_snow@qq.com）

## 5. 自动化证据

- Release 构建：0 警告、0 错误。
- Release 测试：187 通过、2 跳过（真实 LLM 端点测试未配置 `AMM_TEST_LLM_KEY`）、0 失败，共 189 项（含后悔药按钮命令命名回归契约测试）。
- App Certification Kit：退出码 0；清单、资源、UAC 等关键检查通过。报告为 WARNING，原因是测试证书/自包含运行时及应用确实使用 `Process.Start`、系统组件包含进程启动 API；该结果不替代正式签名和商店认证。
- MSIX 产物和测试证书均位于 `artifacts/`，已通过 `.gitignore` 排除，不得提交。
