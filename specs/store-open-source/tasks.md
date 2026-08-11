# MIT 开源与 Microsoft Store 实施任务

- [x] 1. MIT 授权与仓库说明
  - 添加 MIT LICENSE、根 README 和第三方依赖说明。
  - _Requirement: 1_

- [x] 2. Store 模式运行时隔离
  - 增加 L2 可用性接口和商店禁用实现。
  - 自动规则、分析动作和 Dashboard 对不可用 L2 给出安全降级。
  - _Requirement: 3_

- [x] 3. Store MSIX 清单和构建参数
  - 增加 Store 清单模板，移除 allowElevation/startupTask。
  - 让 MSIX 脚本支持 StoreCompatible 参数并保持默认完整版兼容。
  - _Requirement: 3_

- [x] 4. 发布材料完善
  - 更新隐私政策、商店描述、权限解释、截图清单和 WACK 验证说明。
  - _Requirement: 4_

- [x] 5. 测试、打包和审计
  - 增加 Store/L2/清单/ZIP 结构测试。
  - 运行全量测试并生成完整版、Store 版和便携版产物。
  - 审计敏感信息，提交并推送 NAS/GitHub。
  - _Requirement: 5_
