# Microsoft Store listing draft

## App name

AI 内存管家 / AI Memory Manager

## Short description

清晰查看 Windows 内存和进程状态，在用户确认下安全执行清理；可选接入用户自己的大模型服务进行分析。

## Full description

AI 内存管家是一款面向 Windows 的本地内存管理工具。它提供实时内存仪表盘、进程排序、工作集清理、系统缓存清理、清理历史、规则自动化和托盘常驻功能。

软件支持白名单和黑名单。白名单用于保护重要进程，黑名单可以在用户开启功能后监控指定进程并在启动时自动结束。所有高风险进程操作均经过保护过滤和用户确认。

用户还可以配置 OpenAI 兼容接口或本机 Ollama，使用智能分析生成可解释的建议和结构化清理计划。大模型不会直接执行清理，任何会改变系统状态的操作都必须由用户确认。

## Store assets

已准备基础图标：

- `Assets/StoreLogo.png`
- `Assets/Square44x44Logo.png`
- `Assets/Square150x150Logo.png`
- `Assets/Square310x310Logo.png`
- `Assets/Wide310x150Logo.png`

正式提交前还需要在干净的 Windows 10/11 环境中采集并替换以下截图：

1. 仪表盘：内存百分比、时间线和清理按钮。
2. 进程页：排序、白名单和确认终止流程。
3. 智能分析：分析报告、对话和执行计划确认。
4. C 盘瘦身：扫描结果、回收站删除和迁移记录。
5. 设置页：大模型配置、通知、热键和开机自启。

截图不得包含真实 API Key、用户目录、个人文件名或其他个人信息。

## Certification notes

- `runFullTrust`：用于 WPF 桌面进程和托盘常驻。
- `allowElevation`：用于用户确认后的深度清理和管理员级进程操作。
- `StartupTask`：用于用户开启的开机自启。
- 大模型功能 BYOK：数据仅发送到用户选择的端点，不提供开发者托管模型服务。
