# SAP RPA V2 下一任 AI 交接文档

更新时间：2026-06-26

## 当前项目定位

这是 SAP RPA V2，不是旧版纯静态 `sap-rpa://` 页面方案。目标是在公司 Windows Server 上运行：

- 本地 API：`SapWebLauncher.exe --serve`
- SQLite 配置、运行历史、日志和输出文件
- SAP GUI + VBS 自动化
- 页面从本地 API 读取配置和运行状态
- SAP GUI 执行串行排队
- 钉钉通知由后端 HTTP/OpenAPI 发送
- SAP 密码和真实钉钉密钥只保存在服务器本机

## 关键路径

- 源码仓库：`D:\工作\sap_rpa`
- 运行/预览/交接目录：`D:\sap_ai`
- 前端入口：`D:\sap_ai\index.html` + `D:\sap_ai\assets\js\*.js`
- 后端入口：`D:\工作\sap_rpa\网页启动登录\SapWebLauncher\Program.cs`
- VBS 运行脚本：`D:\sap_ai\transactions`
- SQLite 运行库：`D:\sap_ai\data\sap-rpa-config.db`
- 项目规则：`D:\sap_ai\agent.md` 和 `D:\工作\sap_rpa\agent.md`
- 功能说明书：`D:\sap_ai\SapRpa_V2_功能说明书.html`
- 技术设计说明：`D:\sap_ai\SapRpa_V2_技术设计说明.html`
- 数据库字段设计：`D:\sap_ai\DATABASE_FIELD_DESIGN.md`
- 服务器一键安装包：`D:\sap_ai\上线安装包\服务器一键安装包`
- 源码仓库一键安装包副本：`D:\工作\sap_rpa\上线安装包\服务器一键安装包`

## Git 状态

- GitHub 仓库：`https://github.com/ckstock/sap_rpa`
- 当前分支：`codex/v2-local-api-sqlite`
- 注意：`D:\sap_ai` 是运行/预览目录，不是主要 GitHub 源码仓库；正式提交优先在 `D:\工作\sap_rpa`。
- 最近提交点：
  - `acfa314 chore: checkpoint v2 before frontend modularization`：前端模块化前回退点。
  - `41befc0 refactor: split portal javascript modules`：把 `index.html` 内联 JS 拆成 `assets/js/*.js`。
- 当前工作前必须检查 `git status` 和 diff，确认没有数据库、日志、真实 `config.local.json`、钉钉密钥或 SAP 密码。

## 最近完成的文档与安装包更新

1. 已补充功能说明书：
   - 服务器一键安装器作为正式入口。
   - `SAP_RPA_HOME`、路径选择、`.bat` 入口、CLI、`ForceResetDb`、日志复制/保存、诊断日志规则。
   - 可编排 SAP 报表链路：A/B 报表取数、result 结构化保存、C 入参、父子 run/chainRun、失败中断和重跑追溯。
2. 已新增服务器一键安装包：
   - `启动一键安装器.bat`
   - `SapRpaServerSetup.ps1`
   - `config.local.example.json`
   - `README_服务器一键安装说明.md`
   - `关键功能说明.md`
3. 一键安装器已支持：
   - 运行根目录和源码/发布包根目录由用户填写。
   - 一键部署/升级。
   - 配置 SAP 登录，密码走当前 Windows 用户 DPAPI。
   - 初始化/迁移 SQLite。
   - 启动/停止本地 API。
   - 检测上线状态。
   - 打开运行页面。
   - 备份当前运行目录。
   - 备份并重建 SQLite，GUI 二次确认，CLI 要 `-ForceResetDb`。
   - 一键复制日志、保存诊断日志。
4. 已修复安装器读取 `SapWebLauncher.exe test` 输出乱码问题：PowerShell 端按系统默认编码读回 stdout/stderr。
5. 已完成前端 JS 第一阶段模块化：
   - `index.html` 只保留页面结构、样式和脚本引用。
   - `assets/js/portal-state.js`：默认状态、fallback 配置和共享状态。
   - `assets/js/portal-utils.js`：格式化、日期、数组去重、DOM 等工具。
   - `assets/js/portal-api.js`：本地 API 请求、配置归一化、队列/报表/运行/配置刷新。
   - `assets/js/portal-render.js`：页面渲染、表格、卡片、弹窗和日志面板。
   - `assets/js/portal-actions.js`：按钮事件、保存、删除、执行提交、轮询、导出和 toast。
   - `assets/js/main.js`：启动入口。
   - 注意：后续部署或同步运行目录时必须复制 `assets/js`，不能只复制 `index.html`。

## 当前已知设计结论

- 数据库是主配置源。
- 页面从本地 API 读取配置。
- 删除/停用工厂后，前端过滤 `enabled=false`，并清理业务范围/事务码规则里的停用工厂引用。
- SAP GUI 执行保持串行，不并发操作同一个桌面会话。
- ZFI072A 是当前优先打通事务码。
- 多工厂执行应记录父 run 和每个工厂子 run；开始通知一次，结束汇总通知一次，失败工厂支持重跑。
- 钉钉通知由后端发送，不由 VBS 进入 SAP 再调用函数。
- VBS 保持 ASCII/WSH 安全格式；VBS 只接收执行器传入的最终参数。
- 前端后续修改必须按 `assets/js` 模块定位：改 API 合约优先看 `portal-api.js`，改展示优先看 `portal-render.js`，改按钮/保存/执行优先看 `portal-actions.js`，改默认数据优先看 `portal-state.js`。
- `portal-render.js` 仍然偏大。下一任 AI 如果继续做页面维护，建议先把它按页面拆成 `render-workbench.js`、`render-execute.js`、`render-config.js`、`render-reports.js`、`render-schedule.js` 或等价模块，再做较大 UI 改造。

## 可编排 SAP 报表链路问题点

用户后续想让 SapWebLauncher 支持类似场景：

- 后台 submit 报表 A，拿到结构化结果。
- 后台 submit 报表 B，拿到结构化结果。
- 后端按配置把 A/B 结果清洗、合并、计算为 `result`。
- 将页面工厂参数 + `resultId`/`resultFile` + 后续报表参数传给报表 C。
- 如果 C 必须 SAP GUI 操作，再由 VBS 执行 C。

这不能写死成固定 `A+B -> C`。应抽象为“可编排 SAP 报表链路”：

- 每个步骤可以是 NCo/SUBMIT 取数、后端 result 计算、条件判断、人工确认、VBS 执行。
- 每个步骤有依赖关系、输入快照、输出快照、状态、错误码、错误消息、SAP 返回消息、开始/结束时间。
- 如果 A 失败，依赖 A 的后续步骤应 `skipped` 或链路中断，不允许误跑 C。
- 支持 `fail-fast`、`continue-on-error`、`conditional`、`manual-review` 等策略。
- `result` 必须结构化保存。小结果可入 SQLite，大结果写 `outputs/<chainRunId>/<stepId>/result.json`、CSV 或 XLSX。
- 大 result 不允许通过 URL 或命令行直接传给 VBS，只传 `resultId` 或 `resultFile`。
- C 只能在依赖步骤成功且 result 校验通过后执行。
- 重跑必须可追溯：复用旧 result 或重新生成新版 result 必须明确，不能混用不同版本 A/B 结果。
- 页面要能显示父 run 状态、步骤状态、失败原因、跳过原因、输出文件、可重跑入口。
- 钉钉通知要汇总链路整体状态、失败步骤、失败原因、成功步骤、输出文件摘要。

## 新对话框推荐提示词

```text
请继续 SAP RPA V2。先读：
D:\sap_ai\agent.md
D:\sap_ai\SapRpa_V2_功能说明书.html
D:\sap_ai\SapRpa_V2_技术设计说明.html
D:\sap_ai\DATABASE_FIELD_DESIGN.md
D:\sap_ai\AI_HANDOFF_NEXT.md

当前前端已经完成第一阶段模块化：入口是 D:\sap_ai\index.html，脚本在 D:\sap_ai\assets\js\*.js。
后续改页面时不要重新写回大段内联 JS；先按模块定位：
- API/归一化：portal-api.js
- 渲染：portal-render.js
- 事件/保存/执行/轮询：portal-actions.js
- 状态/fallback：portal-state.js
- 工具函数：portal-utils.js
portal-render.js 仍然偏大，下一步建议继续按页面拆细，降低后续改 A 坏 B 的风险。

当前要设计/实现的是“可编排 SAP 报表链路”，不是固定 A+B -> C。

业务目标：
页面提交一次业务请求后，SapWebLauncher/后端可以按配置执行多个 SAP 报表或 SAP GUI 步骤。每个步骤可能是：
1. 通过 sap-report-fetch / NCo / ZFM_NCO_SUBMIT 后台 SUBMIT 报表并取回结果。
2. 对前面步骤结果做清洗、合并、计算，生成 result。
3. 把页面参数、工厂参数、前置步骤 result、resultFile 或 resultId 作为入参，继续执行后续报表。
4. 必要时最后调用 VBS 操作 SAP GUI。

核心要求：
1. 不要把报表取数、复杂计算、链路判断写进 VBS。VBS 只负责 SAP GUI 填参和点击。
2. 后端负责 chainRun/父子 run、步骤定义、依赖关系、步骤状态、参数快照、result 落库/落文件、校验、日志和重跑。
3. 每个步骤必须独立记录输入、输出、开始时间、结束时间、状态、错误码、错误消息、SAP 返回消息、result 文件路径。
4. 如果步骤 A 失败，后续依赖 A 的步骤必须中断或标记 skipped，不允许继续误跑。
5. 链路状态必须能解释清楚：哪个步骤失败、为什么失败、哪些步骤没跑、哪些步骤成功、是否可以从失败步骤重跑。
6. 支持不同链路策略：fail-fast、continue-on-error、conditional、manual-review。
7. result 必须结构化保存。小结果可进 SQLite，大结果写 outputs/<chainRunId>/<stepId>/result.json、csv 或 xlsx。
8. 大 result 不允许通过 URL 或命令行直接传给 VBS，只传 resultId/resultFile。
9. C 或后续步骤只能在依赖步骤成功且 result 校验通过后执行。
10. 重跑必须可追溯：重跑某一步时，要说明是复用旧 result，还是重新生成新版 result；不能混用不同版本的 A/B 结果。
11. 页面要能看到链路进度：父 run 状态、每个步骤状态、日志、错误原因、输出文件、可重跑按钮。
12. 钉钉通知要汇总链路结果：整体状态、失败步骤、失败原因、成功步骤、输出文件摘要。
13. 敏感信息不得进前端、日志或 Git。

请先按多 subagent 模式输出任务拆分表：
- Worker A：后端/API/SQLite/状态机/链路执行器
- Worker B：前端链路进度与错误追溯页面
- Worker C：VBS 参数接收与 resultFile/resultId 读取规范
- QA：失败链路、跳过链路、重跑、敏感信息和日志审计

然后给出：
1. 推荐数据库表设计。
2. API 设计。
3. chainRun/stepRun 状态机。
4. result 存储格式。
5. 错误处理和中断规则。
6. 重跑规则。
7. 页面展示设计。
8. VBS 入参规范。
9. 最小可实现版本。
10. 后续扩展点。
```

## sap-report-fetch 相关约束

- 使用 skill：`C:\Users\chen.kai6\.codex\skills\sap-report-fetch\SKILL.md`
- 优先用本地工具：`C:\Users\chen.kai6\Documents\Codex\2026-06-22\new-chat-3\outputs\SapReportSubmitTester`
- 默认执行方式：`AUTO`，内部优先 `BACKGROUND_SPOOL`，再按情况尝试 `ALV_RUNTIME`、`MEMORY_EXPORT`。
- 默认 RFC 函数：`ZFM_NCO_SUBMIT`
- 默认 ABAP 类：`ZCL_NCO_REPORT`
- 返回表：`ET_LINES`，结构 `SOLI`，字段 `LINE`
- 安全边界：不要随意修改/激活/删除 SAP 对象；只操作用户明确点名的对象。

## 常用验证命令

```powershell
git -C "D:\工作\sap_rpa" status --short --branch

$node='C:\Users\chen.kai6\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
Get-ChildItem 'D:\sap_ai\assets\js\*.js' | ForEach-Object { & $node --check $_.FullName }
Select-String -Path 'D:\sap_ai\index.html' -Pattern 'assets/js/portal-state.js','assets/js/portal-utils.js','assets/js/portal-api.js','assets/js/portal-render.js','assets/js/portal-actions.js','assets/js/main.js'

D:\sap_ai\bin\SapWebLauncher.exe test

$setup='D:\sap_ai\上线安装包\服务器一键安装包\SapRpaServerSetup.ps1'
$text=Get-Content -LiteralPath $setup -Raw -Encoding UTF8
[ScriptBlock]::Create($text) | Out-Null
```

## 换新对话框建议

建议换新对话框继续。当前窗口上下文很长，下一步是新模块级设计，换框更省 token，也更容易让下一任 AI 只聚焦“可编排 SAP 报表链路”。

如果只是小功能维护，可以不强制换窗口，但必须先读本交接文档、功能说明书和 `agent.md`，并优先用 GitNexus/diff 定位影响面。大功能或继续拆 `portal-render.js` 时建议新对话框。
