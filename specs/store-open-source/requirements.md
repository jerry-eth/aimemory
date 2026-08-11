# MIT 开源与 Microsoft Store 版需求文档

## Introduction

本需求将 AiMemoryManager 同时完善为：

1. 使用 MIT License 的公开源码项目；
2. 无证书、可直接解压运行的自包含便携版；
3. 可按 Microsoft Store 要求提交的商店兼容 MSIX 版本。

完整版和商店版允许存在功能差异，但不得降低普通清理、C 盘瘦身、迁移保护和数据安全边界。

## Requirements

### Requirement 1 - MIT 开源授权

**User Story:** 作为使用者和贡献者，我希望明确知道代码可以如何使用、修改和再分发。

#### Acceptance Criteria

1. 当用户打开仓库根目录时，项目应提供标准 `LICENSE` 文件，内容为 MIT License，版权主体和年份明确。
2. 当用户查看项目说明时，系统应说明第三方依赖按其自身许可证提供，不得声称依赖属于本项目。
3. 当用户构建发布包时，发布包不得包含 API Key、密码、私钥或开发者个人凭据。

### Requirement 2 - 无证书便携版

**User Story:** 作为不希望安装证书的用户，我希望复制压缩包到其他设备后直接运行。

#### Acceptance Criteria

1. 当用户运行便携版打包脚本时，系统应生成 self-contained win-x64 ZIP，并包含主程序、提权助手、资源和使用说明。
2. 当用户解压便携包时，系统应无需安装证书或 .NET Runtime 即可启动主程序。
3. 当便携版发布时，系统应明确说明其不提供 MSIX 的快捷方式、自动更新和商店集成。

### Requirement 3 - Store 兼容构建

**User Story:** 作为发布者，我希望生成一个不声明不必要受限能力、可进入商店审核的 MSIX 构建。

#### Acceptance Criteria

1. 当使用 Store 兼容参数构建时，MSIX 清单不得声明 `allowElevation`，且不得启用非必要的自动启动扩展。
2. 当 Store 兼容构建运行时，系统应隐藏或拒绝依赖最高权限计划任务的待机列表清理，并明确返回不可用原因。
3. 当 Store 兼容构建运行普通清理、C 盘扫描、回收站删除、迁移和回退时，行为和安全保护应与完整版一致。
4. 当使用完整版构建时，现有高级待机列表清理功能必须保持可用，不得因商店版改造回归。

### Requirement 4 - 商店发布材料

**User Story:** 作为商店提交者，我希望材料真实、完整且不会暴露个人数据。

#### Acceptance Criteria

1. 当提交商店版时，项目应提供商店描述、功能差异、权限说明、隐私政策和截图采集清单。
2. 当隐私政策中仍有运营主体、邮箱或公开地址占位符时，文档应明确提示提交前必须替换，不得伪造联系信息。
3. 当商店版启用 LLM 时，隐私政策应说明数据只发送到用户选择的端点，并受第三方服务条款约束。
4. 当提交包时，项目应提供清单审计和 Windows App Certification Kit 验证步骤。

### Requirement 5 - 回归与审计

**User Story:** 作为维护者，我希望商店改造不影响现有完整版的可靠性。

#### Acceptance Criteria

1. 当运行全量测试时，现有测试不得失败，并应新增 Store 模式、L2 禁用、清单和便携包结构测试。
2. 当生成发布包时，MSIX、ZIP 和源码仓库均不得包含 `.env`、API Key、Token、私钥或 `.pfx`。
3. 当完成一个发布改动时，系统应提交中文 commit，并同步 NAS 与 GitHub，工作区保持干净。
