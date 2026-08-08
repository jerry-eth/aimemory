# 黑名单自动终止需求草案

## Introduction

在现有白名单与防误杀名单基础上，增加独立的“黑名单自动终止”能力。用户可以维护进程黑名单，并在明确开启总开关后，由应用监听新进程启动事件；命中黑名单的普通进程将被立即终止并写入终止记录。

本功能是高风险操作，默认关闭，并且系统关键进程、应用自身进程和防误杀名单始终不得被自动终止。

## Requirements

### Requirement 1 - 黑名单维护

**User Story:** 用户希望维护一份独立于白名单的黑名单。

#### Acceptance Criteria

1. When 用户在白名单页面打开“黑名单”区域时，系统 shall 显示黑名单列表、添加输入框、删除操作和当前监控开关状态。
2. When 用户输入进程名并点击添加时，系统 shall 去除可选的 `.exe` 后缀、忽略大小写和首尾空格后保存唯一的进程名。
3. When 用户删除黑名单项时，系统 shall 立即持久化删除结果。
4. When 用户尝试将系统关键进程、应用自身进程或防误杀名单中的进程加入黑名单时，系统 shall 拒绝添加并显示原因。
5. When 黑名单与白名单同时包含同一进程时，系统 shall 保留两份名单的独立语义，并在界面提示该配置可能导致自动终止；防误杀名单和系统关键进程保护优先级高于黑名单。

### Requirement 2 - 自动监控开关

**User Story:** 用户希望明确控制是否启用自动终止。

#### Acceptance Criteria

1. While 自动终止开关为关闭状态，when 任意黑名单进程启动时，系统 shall 不终止该进程。
2. When 用户打开自动终止开关时，系统 shall 持久化开关状态，并开始监听后续启动的进程。
3. When 用户关闭自动终止开关时，系统 shall 停止自动终止处理；关闭开关不得影响已有黑名单内容。
4. When 应用启动且持久化开关为开启时，系统 shall 在完成保护名单加载后自动恢复监控。
5. When 应用退出时，系统 shall 释放进程启动监听资源。

### Requirement 3 - 命中后的自动终止

**User Story:** 用户希望黑名单进程一启动就被终止。

#### Acceptance Criteria

1. While 自动终止开关开启，when 新进程名称命中黑名单时，系统 shall 在可行的最短时间内尝试终止该进程。
2. When 黑名单进程已退出、PID 已复用或终止失败时，系统 shall 忽略异常、不导致主程序退出，并记录失败原因。
3. When 进程命中黑名单但属于系统关键进程、应用自身进程、防误杀名单或受前台保护的进程时，系统 shall 跳过自动终止并记录跳过原因。
4. When 自动终止成功时，系统 shall 写入现有终止记录，至少包含时间、PID、进程名、路径、结果和触发来源“黑名单自动终止”。
5. When 自动终止发生且系统通知开关开启时，系统 shall 通过现有托盘通知机制提示用户；通知关闭时仍须保留记录。

### Requirement 4 - 可观察性与误操作防护

**User Story:** 用户希望知道哪些进程被自动终止，并避免误杀重要程序。

#### Acceptance Criteria

1. When 黑名单自动终止开启时，系统 shall 在黑名单区域显示明确的风险提示和“仅对之后启动的进程生效”的说明。
2. When 用户开启自动终止时，系统 shall 显示一次确认提示，说明该功能会自动结束命中的进程。
3. When 自动终止跳过、成功或失败时，系统 shall 在页面状态区域显示最近一次结果；详细结果写入终止日志。
4. When 用户查看终止历史时，系统 shall 能区分人工终止、规则终止和黑名单自动终止。
5. When 用户关闭自动终止或移除黑名单项时，系统 shall 不再对之后匹配的启动事件执行该规则。

### Requirement 5 - 与既有名单的关系

**User Story:** 用户希望理解白名单、黑名单和防误杀名单之间的区别。

#### Acceptance Criteria

1. The system shall label the existing `ExcludedProcesses` as memory-cleaning/analysis exclusion and shall not silently reinterpret it as an auto-kill rule.
2. The system shall label the existing `NoKillProcesses` as a safety override that always blocks automatic blacklist termination.
3. The system shall keep system-critical process protection unconditional.
4. The system shall not allow the blacklist feature to terminate the app's own process.

## UI design direction

- 复用现有 WhitelistPage 的 Fluent UI 卡片布局、圆角卡片、主题资源和中英文本地化方式。
- 黑名单使用独立卡片，采用风险色提示，但不使用持续闪烁或高对比大面积红色。
- 自动终止开关放在黑名单卡片标题行，默认关闭；开启前需要确认。
- 列表中的每一项显示标准化进程名，并提供明确的删除按钮。

## Additional requirements - 对话式分析与执行建议

### Requirement 6 - 分析报告

**User Story:** 用户希望在分析完成后得到一份易读的报告，而不是只看到零散建议卡片。

#### Acceptance Criteria

1. When 大模型分析完成时，系统 shall 生成并显示报告，至少包含分析时间、使用模型、系统内存概况、候选进程、风险级别、建议动作、理由和预估影响。
2. When 分析来自缓存时，系统 shall 在报告中明确标注“来自缓存”，并显示缓存结果对应的时间。
3. When 分析失败时，系统 shall 保留上一次成功报告，并在界面显示本次失败原因，不得清空已有报告。
4. When 用户切换到其他菜单再返回时，系统 shall 保留报告、分析状态和对话记录。
5. The system shall not present model-generated suggestions as completed actions; report shall distinguish “建议” and “已执行结果”.

### Requirement 7 - 分析对话框

**User Story:** 用户希望基于当前报告继续询问大模型。

#### Acceptance Criteria

1. When 用户点击“继续提问/打开对话”时，系统 shall 打开对话框或对话面板，并自动带入最近一次报告的上下文。
2. When 用户输入问题时，系统 shall 将当前报告、必要的最新进程快照和对话历史作为上下文发送给当前大模型档案。
3. When 大模型返回回答时，系统 shall 显示回答、时间、模型和本次 Token 用量；网络或解析失败不得导致窗口退出。
4. When 用户询问“哪些进程/线程适合清理”时，系统 shall 基于当前快照给出可解释的候选列表；对不支持的线程级操作必须明确说明当前实际执行单位是进程或内存工作集。
5. When 用户切换页面时，系统 shall 保留当前对话内容和正在进行的请求状态，返回后继续显示。
6. The system shall provide a “清空当前对话”操作，并在清空前不删除最近一次分析报告。

### Requirement 8 - 对话驱动的清理建议

**User Story:** 用户希望通过自然语言让大模型协助清理内存。

#### Acceptance Criteria

1. When 用户通过对话表达清理意图时，系统 shall 先将意图转换为结构化执行计划，至少包含操作类型、目标进程/范围、预计影响和风险。
2. When 执行计划涉及清理工作集、清空待机列表或终止进程时，系统 shall 在执行前显示明确的确认界面；大模型不得直接静默执行破坏性操作。
3. When 用户确认执行计划时，系统 shall 重新获取实时进程快照，重新应用白名单、系统关键进程、防误杀名单和前台保护规则，然后仅执行仍然满足条件的目标。
4. When 用户拒绝或关闭确认界面时，系统 shall 不执行任何清理动作，并保留对话记录。
5. When 执行完成时，系统 shall 显示成功、失败、跳过数量和释放量，并写入现有清理/终止历史，来源标记为“对话执行”。
6. When 大模型无法确定目标、目标不存在或请求超出当前能力时，系统 shall 只给出解释或澄清问题，不执行操作。
7. The system shall never allow natural-language requests to bypass existing safety filters or directly terminate system-critical, self, protected-foreground, or NoKill processes.

### Requirement 9 - 执行范围和用户确认

**User Story:** 用户希望清理动作可控且可追溯。

#### Acceptance Criteria

1. When 对话计划包含多个目标时，系统 shall 允许用户逐项查看并取消目标后再执行。
2. When 计划包含终止进程时，系统 shall 复用现有终止确认对话框的未保存工作提示和风险提示。
3. When 计划包含低风险内存清理时，系统 shall 显示预计释放量，但仍须用户显式确认。
4. When 任何执行项失败时，系统 shall 保留逐项结果并允许用户继续对话询问原因。

## Clarifications for confirmation

- 本期“清理线程”按现有能力解释为：清理进程工作集、清空待机列表，或在用户确认后终止进程；不直接终止单个线程。
- 对话可以帮助用户形成并执行清理计划，但所有会改变系统状态的动作均需要用户确认。
- 报告、对话、执行计划和执行结果都应复用现有中英文 Fluent UI 风格和本地化机制。
