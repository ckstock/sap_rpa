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
8. 如果发现“临时方案”和“上线方案”不一致，主 agent 必须先向用户说明差异、风险和推荐方案，得到确认后再写代码。
9. 涉及 Git 提交、推送、部署包、README、示例配置时，`appKey`、`appSecret`、`agentId`、token、密码、服务器私有地址等敏感或环境相关值必须全部使用占位符，例如“请填写真实AppKey”“请填写真实AppSecret”“请填写真实AgentId”或 `<...>`；真实值只允许写入本机不提交 Git 的 `config.local.json`、环境变量或受保护的本地凭据。
10. `config.local.json` 默认不得明文提交到 Git。Git 里只允许提交占位模板（例如 `config.local.example.json`）或已经加密且不可直接使用的配置文件；如果确实需要把配置文件纳入版本库，必须先用 DPAPI、Windows 凭据管理器或公司密钥管理方案加密，严禁提交真实 `appKey`、`appSecret`、`agentId`、token、密码、服务器地址等明文值。

## V2 架构规则

1. SQLite/数据库是可配置业务范围的主配置源。
2. 页面必须通过本地 API 读取工厂、工厂组、事务码、事务规则、通知机器人元数据、运行历史。
3. VBS 脚本只接收执行器传入的运行参数；ZFI072A 的 plants 不得在 VBS 中硬编码。
4. SAP GUI 执行默认保持串行，不得并发操作同一个交互式桌面会话。
5. VBS 源码必须保持 ASCII/WSH 安全格式，避免 UTF-8 BOM 或不可控中文编码导致 `cscript.exe` 无法执行；如需输出中文，优先用 `ChrW(...)` 组合或由后端生成中文文案。
6. 浏览器页面不得收集、保存、接收 SAP 密码。
7. 通知机器人 webhook/secret 不得明文返回前端。
8. SQLite 数据库、日志、导出文件、本机配置、真实凭据密文等运行产物不得提交到源码仓库。
9. 迁移公司服务器时，源码走 GitHub；机器本地 SAP 登录配置必须在目标 Windows 执行账号下重新生成。
10. 钉钉通知不再走 SAP Gateway OData；由后端直连 DingTalk OpenAPI。接口根地址、appKey、appSecret、agentId 必须来自服务器环境变量或本地 config，不得写死到前端、VBS 或源码文档示例里；当前 `Ddid=11464769` 只允许作为联调临时值，上线必须改为登录态/配置中的真实钉钉用户 ID。
11. 提交前必须检查 `git status` 和 `git diff`，确认没有把本机 `config.local.json`、数据库、日志、导出文件或任何真实密钥混入提交；发现明文配置时先停止提交并改成占位模板或加密存储。

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

## 一键部署包设计规则

后续可以制作服务器一键安装包，但安装包必须按“可迁移、可回滚、可现场配置”的方式设计，不能把当前开发机路径当成上线前提。

1. 安装目录必须支持安装时选择、命令行参数或环境变量覆盖，例如 `SAP_RPA_HOME`。`D:\sap_ai` 只允许作为当前开发机默认示例，正式部署脚本不得假设服务器一定存在 D 盘。
2. 安装前必须检测目标磁盘和目录权限；如果默认盘符不存在，应让管理员选择目录，或回退到如 `C:\SAP_RPA`、`C:\ProgramData\SapRpa` 这类服务器可用路径。
3. 如果服务器根目录、源码/发布包根目录、端口、域名、账号或密钥不确定，不得自行猜测并写死；必须让用户或服务器管理员在安装 UI、命令行参数、环境变量或本机配置文件中填写。
4. 一键安装器必须同时考虑图形界面和命令行静默安装。GUI 适合首次上线和人工排错；CLI 适合后续自动化升级。
5. 安装器入口必须能在 Windows Server 上稳定运行，失败时不能一闪而过，必须保留日志和退出码。若服务器执行策略要求签名脚本，应提示使用签名脚本或编译 EXE 入口，不要把绕过执行策略当作正式上线方案。
6. 安装包应复制应用文件、前端页面、VBS 脚本、SQLite 迁移脚本、配置模板、启动脚本、健康检查脚本和部署说明；不得复制开发机数据库、日志、导出文件、真实本机配置或个人路径。
7. SQLite 数据库初始化必须区分“新装”和“升级”。新装时创建空库并执行迁移；升级时先备份现有数据库，再执行迁移，不得无确认覆盖生产库。
8. `config.local.json` 只能在目标服务器本机生成或由管理员现场填写。Git 和安装包里只能放 `config.local.example.json` 这类占位模板。
9. SAP GUI 自动化依赖交互式 Windows 桌面会话，不能按普通 Windows Service 在 Session 0 中直接执行 VBS。部署时必须使用固定 Windows 执行账号登录桌面，或使用“用户登录后启动”的计划任务承载 SAP GUI 执行器。
10. 本地 API 可以做后台常驻进程或服务；SAP GUI/VBS 执行必须绑定到可交互桌面，并保持串行队列，避免多个任务同时操作同一个 SAP GUI 会话。
11. 安装过程应检测 `.NET Runtime/SDK`、SAP GUI、SAP GUI Scripting、SQLite 数据目录权限、API 端口占用、Windows 凭据/DPAPI 可用性。
12. 安装完成后必须自动执行健康检查：API `/api/health`、配置 API、SQLite 读写、VBS 脚本目录存在、SapWebLauncher 基础自检。如 SAP GUI 未登录或不可控，应明确提示管理员现场处理。
13. 升级包必须支持备份和回滚：至少备份当前程序目录、`config.local.json`、SQLite 数据库和关键脚本；失败时不得留下半升级状态。
14. 卸载逻辑不得默认删除 SQLite 生产数据库、日志和本机凭据。删除数据必须单独确认。
15. 部署文档必须包含“安装程序点击步骤”和“命令行静默安装参数”两种路径，方便管理员手工上线和后续自动化上线。
16. 如果一键部署临时方案与最终上线方案不一致，必须先向用户说明差异和风险，得到确认后再实现。

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
