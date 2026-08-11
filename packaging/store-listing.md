# Microsoft Store listing draft

## App name

AI 内存管家 / AI Memory Manager

## Short description

本地查看 Windows 内存、进程和磁盘状态，在用户确认下安全清理并管理 C 盘空间。

## Full description

AI 内存管家是一款面向 Windows 10/11 的本地资源管理工具。它提供实时内存仪表盘、进程排序、工作集清理、清理历史、规则自动化、托盘常驻、C 盘瘦身、回收站清理、安全迁移和迁移回退。

软件默认不上传遥测数据。用户可以使用白名单保护重要进程，并通过前台保护、系统关键进程保护、路径边界和操作确认降低误操作风险。删除类操作优先使用 Windows 回收站；迁移操作在完成校验前不会删除源文件，失败时保留回退信息。

### 可选大模型分析

用户可以配置自己的 OpenAI 兼容接口或本机 Ollama。大模型只生成解释和结构化建议，不直接获得清理权限；任何改变系统状态的计划都必须由用户确认。未配置 API Key 时，软件仍可使用本地规则完成主要功能。

### Store 版功能范围

Microsoft Store 版为商店兼容构建：

- 保留普通内存清理、进程管理、C 盘扫描、回收站清理、安全迁移和回退；
- 不声明 `allowElevation`，不创建最高权限待机列表计划任务；
- 高级待机列表清理按钮会隐藏，自动规则在需要时降级为普通清理；
- 不包含 MSIX 自动启动扩展，设置页不会提供该开关。

需要高级待机列表清理的用户可从开源仓库获取完整版便携 ZIP。两个版本共享同一套路径安全、白名单、确认和审计原则。

## Store assets

已准备基础图标：

- `Assets/StoreLogo.png`
- `Assets/Square44x44Logo.png`
- `Assets/Square150x150Logo.png`
- `Assets/Square310x310Logo.png`
- `Assets/Wide310x150Logo.png`

正式提交前需要在干净的 Windows 10/11 环境中采集以下截图：

1. 仪表盘：内存百分比、时间线和普通清理按钮。
2. 进程页：排序、白名单和确认终止流程。
3. 智能分析：分析报告和执行计划确认，截图中不得出现 API Key。
4. C 盘瘦身：扫描结果、回收站删除、迁移记录和回退入口。
5. 设置页：本地规则、通知、热键和 Store 版不显示高级自启动开关的状态。

截图不得包含真实 API Key、用户目录、个人文件名、机器名或其他个人信息。

## Capability and certification notes

- `runFullTrust`：用于当前 WPF 桌面进程、托盘和 Win32 本地功能；提交说明应限定为桌面应用运行所需能力。
- Store 版不声明 `allowElevation`，不注册最高权限计划任务，也不包含 `windows.startupTask` 扩展。
- 用户自带模型端点（BYOK）：数据仅发送到用户配置的端点，不提供开发者托管模型服务。
- 发布前必须提供公开 HTTPS 隐私政策，并填写真实运营主体和隐私联系邮箱。

## Certification checklist

- [ ] Partner Center Identity、Publisher 和版本号已替换占位值。
- [ ] 使用 Store 兼容构建并检查清单不存在 `allowElevation` 和 `startupTask`。
- [ ] `dotnet test --configuration Release` 全部通过。
- [ ] 在干净普通用户账户完成安装、首次启动、升级、卸载和重新安装测试。
- [ ] 测试 UAC 取消、权限不足、磁盘拔出/目标目录不可用、迁移中断和回退。
- [ ] 使用 Windows App Certification Kit（WACK）检查安装、清单、文件和运行行为。
- [ ] 审核包、截图和日志不含 API Key、Token、个人路径或证书私钥。
