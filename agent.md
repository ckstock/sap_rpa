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
| Worker B | 前端页面、交互、API 消费 | 当前旧结构：`index.html`、`assets/js/**`；目标重构后：`frontend/`、`tests/frontend/` |
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

## 前端架构与回归规则

SAP RPA V2 的页面已经包含工作台、执行任务、定时任务、基础配置、统计报表、API 队列状态、SQLite 配置消费和 VBS 入参生成。当前旧结构已把 `index.html` 中的大段内联 JS 拆到 `assets/js/*.js`，后续开发必须按模块职责修改，不得为了小改动重新把逻辑内联回 `index.html`。

当前前端模块职责：

- `index.html`：页面骨架、CSS、HTML 容器和脚本加载顺序。
- `assets/js/portal-state.js`：默认状态、fallback 配置、页面共享状态。
- `assets/js/portal-utils.js`：日期、格式化、数组去重、DOM 等通用工具。
- `assets/js/portal-api.js`：本地 API 请求、配置归一化、运行/队列/报表/配置刷新。
- `assets/js/portal-render.js`：页面渲染、表格、卡片、弹窗、日志面板。该文件仍偏大，下一轮建议继续按工作台、执行页、基础配置、统计报表、定时任务拆细。
- `assets/js/portal-actions.js`：按钮事件、保存、删除、执行提交、轮询、导出、toast。
- `assets/js/main.js`：启动入口和初始化顺序。

1. 新增中型以上前端功能前，必须先设计模块边界：配置归一化、页面渲染、表单状态、API client、执行 payload、队列轮询、日志合并、统计报表、基础配置 CRUD 应逐步拆成独立模块或独立函数组。优先在现有 `assets/js` 模块内定位，不要回到单文件大改。
2. 修改公共状态或公共函数前，先查 `git status`、`git diff` 和最近改动；可用 GitNexus 时优先用 GitNexus 定位最近变更和影响范围。
3. 不要只为了一个展示文案改动去重写 API、数据库或 VBS 传参链路。展示名、payload 字段、数据库字段、VBS 参数必须分清职责。
4. 事务码执行范围必须按事务码参数类型展示：工厂型显示“执行工厂”并传 `plants`；业务范围型显示“执行业务范围”并传 `businessAreas`。
5. 每次修改工作台、执行任务、事务码规则、基础配置或 payload 相关前端逻辑后，必须至少验证四个回归点：工作台事务码卡片“提交”能跳到执行任务；执行页事务码下拉切换后执行范围立即刷新；ZFI019NL 显示“执行业务范围”且传 `businessAreas`；普通工厂型事务码显示“执行工厂”且传 `plants`。
6. 前端回归不能只跑语法检查。至少检查 `assets/js/*.js` 语法、`index.html` 脚本引用顺序和页面加载；能自动化时优先用 Playwright/浏览器点击流。如果浏览器环境被拦截，必须说明未跑真实点击，并由用户刷新 `file:///D:/sap_ai/index.html` 人工确认。
7. 对 `index.html` 的补丁必须保持最小范围；通常只改容器、样式或脚本引用。状态、API、payload、轮询、配置归一化、渲染和事件逻辑应改对应 `assets/js` 模块；如果一次改动需要同时触碰多个模块，应先拆任务并评估是否继续拆 `portal-render.js` 等大文件。

## V2 架构规则

1. SQLite/数据库是可配置业务范围的主配置源。
2. 页面必须通过本地 API 读取工厂、工厂组、事务码、事务规则、通知机器人元数据、运行历史。
3. VBS 脚本只接收执行器传入的运行参数；ZFI072A 的 plants 不得在 VBS 中硬编码。
4. SAP GUI 执行默认保持串行，不得并发操作同一个交互式桌面会话。
5. VBS 源码必须保持 ASCII/WSH 安全格式，避免 UTF-8 BOM 或不可控中文编码导致 `cscript.exe` 无法执行；如需输出中文，优先用 `ChrW(...)` 组合或由后端生成中文文案。
6. 事务码多值参数不得默认一次性塞给 VBS。接入或修改脚本前必须确认脚本是否真正支持 CSV 多值；若脚本只取首值或只填单值字段，后端必须按 ZFI072A 父子 run 模式拆成 work items/子任务串行逐个调用 VBS，并由父 run 汇总状态、失败项和通知。
7. 浏览器页面不得收集、保存、接收 SAP 密码。
8. 通知机器人 webhook/secret 不得明文返回前端。
9. SQLite 数据库、日志、导出文件、本机配置、真实凭据密文等运行产物不得提交到源码仓库。
10. 迁移公司服务器时，源码走 GitHub；机器本地 SAP 登录配置必须在目标 Windows 执行账号下重新生成。
11. 钉钉通知不再走 SAP Gateway OData；由后端直连 DingTalk OpenAPI。接口根地址、appKey、appSecret、agentId 必须来自服务器环境变量或本地 config，不得写死到前端、VBS 或源码文档示例里；当前 `Ddid=11464769` 只允许作为联调临时值，上线必须改为登录态/配置中的真实钉钉用户 ID。
12. 提交前必须检查 `git status` 和 `git diff`，确认没有把本机 `config.local.json`、数据库、日志、导出文件或任何真实密钥混入提交；发现明文配置时先停止提交并改成占位模板或加密存储。

## SapWebLauncher C# 编排与设计模式规则

SapWebLauncher 后端要按可维护、可运维、可扩展的设计模式实现 SAP 报表调用和事务码调用，不得把报表、事务码、错误判断、重跑规则堆成硬编码 `if/else` 或单个巨大方法。参考当前 ABAP Gateway 里用 `action -> sap_class -> sap_method` 路由表分发的思路，C# 侧也应建立清晰的路由、策略和适配器模型。

1. C# 调用 ALV 报表、`ZFM_NCO_SUBMIT`、sap-report-fetch、本地 VBS、SAP GUI 事务码时，必须通过统一接口或抽象层进入，例如 `ISapActionHandler`、`IReportFetchStrategy`、`ITransactionExecutor`、`IResultValidator`、`IErrorClassifier`，具体命名可按代码风格调整。
2. 每种动作类型必须有明确 handler/strategy：`NCO_REPORT`、`ALV_REPORT_FETCH`、`CALC_RESULT`、`VALIDATE_RESULT`、`SAP_GUI_VBS`、`MANUAL_REVIEW` 等。新增事务码或报表时优先新增配置和 handler，不要在主执行器里追加大量分支。
3. 后端编排模型必须支持四级追踪：`chainRun -> stepRun -> actionRun/transactionRun -> phaseEvent`。一个业务步骤如果要调用 10 个事务码，不能作为一个黑盒 step 记录，必须拆成 10 个可见的 `actionRun/transactionRun`，每个都有独立状态、输入快照、输出摘要、SAP 状态栏、开始结束时间、错误码和错误消息。
4. `phaseEvent` 用于记录事务码内部关键阶段，例如 `prepare_input`、`open_transaction`、`fill_field`、`press_button`、`wait_result`、`read_status_bar`、`export_file`、`validate_output`、`cleanup_session`。失败时必须能定位到第几个事务码、哪个阶段、哪个字段或按钮、SAP 返回了什么。
5. 用户可见错误必须讲清楚业务影响，例如“步骤 3 失败：第 6/10 个事务码 ZFI072A 在读取状态栏阶段返回 E - xxx；后续 4 个事务码已跳过；可从 ZFI072A 重跑”。开发者日志必须同时能看到 actionKey、transactionCode、phase、inputSnapshotId、resultId/resultVersion、VBS outputFile、SAP MessageType/Text。
6. 错误处理必须统一分类，至少覆盖：参数缺失、配置缺失、SAP 登录/会话失败、NCo/RFC 失败、报表无 spool/list 输出、ALV 捕获失败、VBS 运行失败、SAP 状态栏错误、结果校验失败、文件读写失败、超时、人工审核拒绝、敏感信息拦截。
7. 每个 handler 必须返回结构化结果对象，包含 `ok`、`status`、`errorCode`、`safeMessage`、`sapStatusType`、`sapStatusText`、`resultId`、`resultFile`、`outputFiles`、`diagnosticRef`。不得只返回字符串或只靠 exit code 判断成败。
8. 调用链中要使用可测试的策略/适配器/工厂/注册表模式：动作路由表决定调用哪个 handler；错误分类器决定错误类型和用户文案；result validator 决定结果是否可作为下游输入；retry policy 决定是否允许重跑。
9. 重跑必须基于不可变版本：每次 actionRun/transactionRun 执行都生成新的 attempt/resultVersion。重跑第 N 个事务码时，必须记录前 N-1 个结果是复用旧版本还是重新执行；不得默认取“最新成功 result”。
10. SAP GUI/VBS 动作继续保持串行，不得并发操作同一个交互式桌面会话；NCo/后台报表动作后续可按依赖 DAG 并行，但第一版优先串行确保可追踪。
11. 代码注释要写在策略边界、错误分类、重跑版本、敏感信息脱敏、VBS 参数生成、SAP 状态栏解析等容易误改的位置。注释要说明“为什么这样设计”和“误改会造成什么风险”，不要写无意义的逐行解释。
12. 新增事务码或报表接入时，必须同步补充：动作配置示例、handler 选择规则、输入字段说明、成功/失败判定、可重跑边界、敏感字段过滤、页面展示文案和最小测试用例。

## 链路详情页与消息推送规则

可编排链路的页面详情和消息推送必须服务于同一个目标：用户能看懂业务卡在哪里，开发者能直接定位到具体 step/action/phase/result 版本。不要只显示“失败”或只推送一段笼统报错。

1. 链路详情页必须采用嵌套时间线：第一层展示 `chainRun` 整体状态；第二层展示 `stepRun`；第三层展示 `actionRun/transactionRun`；第四层在详情抽屉或日志区展示 `phaseEvent`。如果一个 step 调用 10 个事务码，页面必须能展开看到 1/10 到 10/10 每个事务码的状态。
2. 详情页顶部必须展示链路名称、runId、链路版本、触发人、触发方式、开始/结束时间、总耗时、整体状态、执行策略、当前失败点、是否可重跑。
3. 每个 step/action 节点至少展示：序号、名称、动作类型、事务码或报表名、状态、开始/结束时间、耗时、依赖关系、输入摘要、输出摘要、SAP 状态栏、错误摘要、跳过原因、resultId/resultVersion、输出文件摘要和重跑入口。
4. 用户视角文案必须明确业务影响，例如“步骤 3 失败：第 6/10 个事务码 ZFI072A 在读取状态栏阶段返回 E - xxx；后续 4 个事务码因依赖失败已跳过；可从 ZFI072A 重跑”。开发者视角必须能继续展开看到 actionKey、phase、inputSnapshotId、VBS outputFile、safe log、diagnosticRef。
5. 页面上的日志必须分页或按游标加载，默认只显示脱敏后的 `safeMessage/safeDetails`。原始日志、服务器绝对路径、SAP 连接信息、token、secret、密码和大 result 原文不得直接进入前端。
6. 输出文件区域只展示文件名、类型、大小、生成时间、来源 step/action 和受控下载入口；不得展示真实服务器绝对路径、共享盘凭据或未脱敏业务明细。
7. 重跑按钮必须由后端返回 `retryable`、`retryMode`、`retryReason`、`affectedSteps`、`reuseResults`、`newResultVersions` 决定，前端不得只靠状态字段自行判断。点击重跑前必须展示将重跑哪些 step/action、复用哪些 result 版本、哪些后续节点会失效或重新生成。
8. 消息推送必须分层降噪：链路开始最多推送一次；链路结束推送一次汇总；中间步骤失败默认写日志和页面状态，不对每个 action 刷屏，除非配置了关键失败立即通知。
9. 链路结束通知必须包含：整体状态、runId、链路名称、耗时、成功 step/action 数、失败 step/action、失败原因摘要、跳过数量与原因、输出文件摘要、是否可重跑、建议处理动作。通知中不得包含 SAP 密码、token、secret、服务器路径、大 result 原文或未脱敏敏感业务数据。
10. 通知必须展示本次任务实际传给 VBS/handler 的关键执行入参，不能盲目展示 payload 里所有字段。展示白名单以事务码配置 `params_json`、脚本头部 `@params`、实际 VBS 入参提示和 action 输入快照为准：工厂型事务码只展示工厂/日期等实际入参，业务范围型事务码只展示业务范围/日期等实际入参，同时需要把父子 run 批次项合并进去。工厂、业务范围、业务范围组、年度、周次、期间、开始/截止日期、字段入参等要按业务含义合并去重后展示；空值不展示；失败项要按参数类型写成“失败工厂”“失败业务范围”或“失败动作”。不得因为前端或定时任务 payload 顺带携带 `businessAreas/plants` 就在不消费该参数的事务码通知里展示多余入参；如果 VBS 只填 `S_WERKS/plant`，通知和脚本日志都不得显示“业务范围”；如果 VBS 只填 `S_GSBER/businessArea`，通知和脚本日志都不得显示“工厂”；如果 VBS 实际写入 `S_BUDAT-LOW/HIGH`，即使日期由脚本按系统日期自动计算，通知也必须展示最终执行日期范围；如果日期字段只是计算或日志且未写 SAP 选择屏幕，则不得冒充执行日期。SAP 密码、token、secret、appKey、appSecret、agentId、服务器路径、SAP client/user/system 等敏感或环境字段不得进入通知。
11. 钉钉 markdown 样式必须克制：标题只用普通加粗文本，不用过大的 `#`/`##` 标题；正文按聊天字号使用普通列表，只有状态、失败项和关键值适度加粗。
12. 失败通知必须能定位到最小失败单元：`stepKey/actionKey/transactionCode/phase/errorCode/sapStatusType/sapStatusText`。如果是依赖失败或条件跳过，通知要写清“未执行”而不是写成“失败”。
13. 消息推送必须幂等。以 `chainRunId + notifyStage` 或 `chainRunId + stepRunId + notifyStage` 做唯一键，避免 API 重试、执行器重启、页面重复触发导致重复通知。
14. 通知发送结果必须写入 `run_events` 或通知日志，记录发送阶段、接收对象摘要、发送状态、错误码和脱敏错误消息。通知失败不得改变 SAP 执行结果，但必须在页面和日志中可追溯。
15. 人工审核类链路要有专门通知：进入 `WAITING_MANUAL_REVIEW` 时推送待审核摘要；审核通过、驳回、超时都必须记录并推送结果。超时不得默认通过。
16. 页面和通知的状态词必须统一，至少区分 `PENDING`、`RUNNING`、`SUCCEEDED`、`FAILED`、`PARTIAL_SUCCEEDED`、`SKIPPED`、`WAITING_MANUAL_REVIEW`、`CANCELLED`、`RERUNNING`。不要在页面写成功、通知写部分成功、数据库写 failed 造成三方不一致。

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

## 上线安装包设计规则

当前版本不再交付服务器一键安装器。安装包必须按“手工安装指南 + 上线核对清单 + 必要脚本”的方式维护，避免把复杂环境判断藏在不透明脚本里。

1. 权威入口是 `上线安装包\上线安装文档清单.md` 和 `上线安装包\最终上线部署步骤\README_V2_Windows_Server_上线部署.md`。
2. 安装包只保留必要脚本：生成上线包、配置 SAP 登录信息；不再保留一键安装、健康检测、打开页面、卸载、自动启动 API 等脚本入口。
3. 所有路径必须在文档开头显式列出：源码仓库目录、运行根目录、后端 exe、SAP 登录配置、钉钉真实配置、对外访问地址。当前服务器示例是 `D:\RPA\RpaProject` 和 `D:\RPA`，不能作为其他服务器的隐藏前提。
4. Git 更新后必须写清楚三类同步：后端改动要 `dotnet publish` 并复制到运行目录 `bin`；前端改动要复制 `index.html + assets`；VBS 改动要复制到运行目录 `transactions`。
5. `config.local.json` 只能在目标服务器本机生成或由管理员现场填写。Git 和安装包里只能放 `config.local.example.json` 这类占位模板。
6. 安装包不得复制开发机数据库、日志、导出文件、真实本机配置、SAP 登录配置、真实钉钉密钥或个人路径。模块化后只复制 `index.html` 不复制 `assets/js` 会导致页面白屏或按钮无响应。
7. SQLite 数据库初始化必须区分“新装”和“升级”。升级默认保留数据库；如果要重建，必须先备份并明确说明会丢失的配置、运行历史和日志。
8. SAP GUI 自动化依赖交互式 Windows 桌面会话，不能按普通 Windows Service 在 Session 0 中直接执行 VBS。部署时必须使用固定 Windows 执行账号登录桌面，或使用“用户登录后启动”的计划任务承载 SAP GUI 执行器。
9. 本地 API 可以做后台常驻进程或服务；SAP GUI/VBS 执行必须绑定到可交互桌面，并保持串行队列，避免多个任务同时操作同一个 SAP GUI 会话。
10. 验收不能停在 API `/api/health`。必须从网页提交一次受控任务，确认 SQLite 有 run/log，VBS 从运行目录读取最新脚本，SAP ready session 可复用，钉钉启用时日志出现 `sap dingtalk openapi sent`。
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
- `assets/js/*.js` 语法检查通过，`index.html` 正确引用这些模块，页面真实加载无缺失。
- SQLite 初始化/迁移可跑通。
- API `health`、`config`、`schema` 能返回数据。
- QA 检查空库、有配置、无 plants、禁用规则、secret 不泄露。
