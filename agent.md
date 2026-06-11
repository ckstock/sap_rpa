# SAP RPA V2 Agent 规则

本仓库是 SAP RPA V2，不是旧的纯静态 `sap-rpa://` 页面方案。
目标运行环境是公司 Windows Server：本地 API、SQLite 运行数据、SapWebLauncher 执行器、SAP GUI + VBS 自动化、页面读取 API，并且 SAP 密码只在服务器 Windows 执行账号本地通过 DPAPI 或 Windows 凭据管理器保护。

## 必须使用多 Agent 模式

非简单实现任务必须按“主 agent + 并行 worker + QA”的方式执行。
不要把复杂功能单线程顺序做完；如果 subagent 工具不可用，必须明确说明，并改用“并行任务计划 + 分阶段执行 + 状态表”的方式模拟，不能假装已经 spawn subagents。

| 角色 | 职责 | 默认写入范围 |
| --- | --- | --- |
| 主 agent | 拆任务、分配 owner、协调、合并、最终验收 | 项目级协调文件；其他文件必须先明确 owner |
| Worker A | 后端、API、SQLite、迁移、执行队列集成 | `backend/`、`tests/backend/` |
| Worker B | 前端页面、交互、API 消费 | `frontend/`、`tests/frontend/` |
| Worker C | VBS、ZFI072A、运行参数传入 | `sap/vbs/`、`sap/zfi072a/` |
| QA | 测试、边界、回归、敏感信息检查 | `tests/`、`docs/qa-report.md` |

如果当前仓库尚未重组为上述目标目录，worker 不得擅自修改旧目录。必须先报告实际文件位置，并由主 agent 或用户显式授权旧结构兼容写入范围。

## 任务启动规则

1. 主 agent 在 spawn 前必须输出任务拆分表。
2. 当任务横跨后端、前端、VBS、验证时，主 agent 必须明确 spawn Worker A、Worker B、Worker C、QA。
3. 每个 subagent 必须有独立职责和独立文件范围。
4. 不允许两个 worker 同时修改同一批文件。
5. subagent 不得 revert、覆盖或混改其他 worker 的改动。
6. subagent 工作期间，主 agent 不重复实现同一份功能，只做协调、审查和非冲突工作。
7. 如果无法真正 spawn subagents，必须明确说明，并用可见状态表模拟并行流程。

## V2 架构规则

1. SQLite/数据库是可配置业务范围的主配置源。
2. 页面必须通过本地 API 读取工厂、工厂组、事务码、事务规则、通知机器人元数据、运行历史。
3. VBS 脚本只接收执行器传入的运行参数；ZFI072A 的 plants 不得在 VBS 中硬编码。
4. SAP GUI 执行默认保持串行，不得并发操作同一个交互式桌面会话。
5. 浏览器页面不得收集、保存、接收 SAP 密码。
6. 通知机器人 webhook/secret 不得明文返回前端。
7. SQLite 数据库、日志、导出文件、本机配置、真实凭据密文等运行产物不得提交到源码仓库。
8. 迁移公司服务器时，源码走 GitHub；机器本地 SAP 登录配置必须在目标 Windows 执行账号下重新生成。

## 敏感信息规则

禁止硬编码以下敏感或环境相关信息：

- SAP 账号或密码。
- SAP client。
- 公司代码。
- SAP 服务器地址。
- 钉钉或 webhook secret。
- GitHub、Netlify 或其他 token。
- 个人本机路径。

敏感或环境相关参数必须通过环境变量、`.env.local`、`config.local.json`、Windows DPAPI/凭据管理器或运行时参数传入。

## 基础配置功能规则

基础配置页面应采用类似 Django Admin / Navicat 的后台管理风格：信息密度高、便于维护数据，不做花哨展示页。

导航保持：

- 工作台
- 执行任务
- 定时任务
- 基础配置

基础配置至少覆盖：

- 工厂配置。
- 工厂组配置。
- 事务码配置。
- 事务码与工厂/工厂组规则。
- 通知机器人。

推荐数据库表：

- `plants`
- `plant_groups`
- `plant_group_members`
- `transactions`
- `transaction_plant_rules`
- `notification_robots`
- `notification_robot_bindings`
- `schema_migrations`
- `runs`
- `run_logs`
- `run_files`

业务配置表应尽量保留迁移友好的元数据字段：

- `id`
- `code`
- `name`
- `is_active`
- `sort_order`
- `created_at`
- `updated_at`
- `created_by`
- `updated_by`

## 验收清单

这类功能的最终主 agent 汇总必须包含：

1. 变更摘要。
2. 每个 worker 修改了哪些文件。
3. 数据库表/字段设计。
4. API 列表。
5. 验证命令。
6. 测试结果。
7. 需要人工确认的点。
8. 是否建议提交/推送到当前 PR 分支。

期望验证覆盖：

- `dotnet build` 通过。
- 如支持，执行 `SapWebLauncher.exe test`。
- `index.html` 或前端入口内联 JavaScript 语法检查通过。
- SQLite 初始化/迁移可跑通。
- API `health`、`config`、`schema` 能返回数据。
- QA 检查空库、有配置、无 plants、禁用规则、secret 不泄露。
