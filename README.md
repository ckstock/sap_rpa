# SAP 自动化执行门户

V2 本地 API + 网关门户，用于从网页发起 SAP 事务码自动化执行。正式入口通过服务器网关访问，后端由运行目录 `D:\RPA\bin\SapWebLauncher.exe --serve` 提供 API、队列、SQLite、SAP GUI/VBS 执行和钉钉通知。

钉钉应用 `agent_id` 按当前 SAP NCo 连接的 `SystemId` 从本机 `D:\RPA\config.local.json` 的 `dingTalkOpenApi.agentIdBySystem` 选择，例如 `EP1` 使用生产 AgentId、`TD1` 使用测试 AgentId；映射已配置但系统标识未命中时拒绝发送并写明原因，只有未配置映射时才使用 `defaultAgentId`，再兼容旧版 `agentId`。该值是应用标识，不是接收人 userid；接收人仍在实际发送时通过 `ZFI_GET_DDID` 转换，转换无值时回退网页人员编号。真实密钥和本机配置不提交 Git。

当前重点：
- **前端启动依赖本地化：**正式门户的 Lucide 图标脚本随项目部署在 `assets/vendor`，不得依赖外网 CDN。图标资源缺失或加载异常只能影响图标展示，不能阻断 API 健康检查、基础配置加载或定时任务保存。保存定时任务时若前端状态暂时离线，会先自动重新检查执行服务器 API，确认仍不可用才拒绝保存。远程用户看到“API 未连接”时，应检查正式域名下的 `/rpa/api/health` 和 `/rpa/api/config`，不需要在用户电脑启动本地 API。
- **定时任务入参快照公共规则：**新建任务时，基础配置仅提供默认工厂/业务范围；保存后，任务的工厂、业务范围和手工填写的日期等入参作为快照写入任务。后续基础配置、工厂组成员或固定业务范围的改动，都不会修改既有任务；只有用户在该任务的编辑页主动改入参并保存，才会更新快照。触发、入队、SAP 执行和失败重跑均使用同一快照。未填写日期而选择“自动取上周”的任务，日期仍按每次计划运行时的上周计算。
- **定时任务执行版本快照：**任务保存时同时保存当时事务码对应的 VBS 文件名、脚本哈希和超时秒数。后续基础配置替换脚本、脚本缓存或修改事务超时，不会改变已经保存任务的执行版本；用户在任务编辑页主动更换事务码并保存时，才按新事务码重新取脚本和超时。调度触发、队列运行、批次子任务和失败重跑均使用该任务快照。
- **统计报表导出全量分页：**左侧统计报表的“导出报表”会按当前日期范围分页拉取全部 run，不只导出当前页缓存；CSV 里会附带原始工厂入参、业务范围入参和业务范围组，方便排查遗漏。
- **正式上线统计数据清理：**正式切换前先确认队列 `runningRunId` 为空且 `queuedCount=0`，停止本项目 API 后备份 `D:\RPA\data\sap-rpa-config.db`（含 WAL/SHM），再只清理 `runs`、`run_batch_items`、`run_params`、`run_result_logs`、`run_files`、`run_logs`、`schedule_task_runs`。必须保留 `schedule_tasks`、`transactions`、工厂/组织映射、`app_settings` 和其他通用配置；清理后重启服务并确认统计报表为空、配置仍可读取。
- **正式目录替换规则：**源码或配置变更完成后，必须先编译并运行契约测试；确认 `GET /api/queue/status` 的 `runningRunId` 为空且 `queuedCount=0` 后，备份生产 `D:\RPA\bin`，再把最新编译产物复制到生产目录。复制后必须核对源码编译目录与 `D:\RPA\bin` 的关键程序集哈希一致，重启 SapWebLauncher 和网关，并检查本机 `/api/health`、`/api/queue/status` 及正式入口可访问。队列有运行或排队任务时不得替换正式程序集，避免中断 SAP GUI；只能等待任务完成后再发布。

- 工作区源码目录是 `D:\RPA\RpaProject`，服务器运行目录是 `D:\RPA`。
- `ZFI019NL` 周损益执行链路展示。
- `ZFI057` 业务范围显示规则：钉钉执行入参只读取工作流 `[scope]` 汇总日志，并按 `businessArea` 唯一归并 `ZFIT_RPA_BUKRS-WERKS`；开始通知尚未完成映射时显示“执行时按 ZFIT_RPA_BUKRS 查询”，不能显示为映射日志缺失。ZFI057 物料 Excel 的 `800*` 物料来源为 `ZFI019NL MEMORY_EXPORT`，东台追加物料来源为 `ZFI_SPLIT` 自定义表，合并后仍按行保留 `SourceType/Source`，取数层和归类/写表层都按最终 `MATNR` 双重过滤东台 memory 物料，不能把 631* 标成 `ZFI019NL_MEMORY`；非 800* 物料只能来自 `ZFI_SPLIT`。
- **2026-08-06 组织归档规则优先于下方历史周/工厂说明：**保存类 ALV 由 SAP GUI 先写本机暂存，后端按导出 Excel 的实际 `WERKS` 或 `GSBER` 值只读查询 SAP 映射表。`ZFI080`、`ZFI080B`、`ZFI019NL`、`ZFI019NA`、`ZFI148` 使用 `ZTFI48A-GSBER -> ZBU/ZSBU`；其他保存类使用 `ZTFI48B-WERKS -> ZBU/ZSBU`。命中时最终文件为 `<alvExportDataDirectory>\<ZBU>\<ZSBU>\yyyy_WKnn\<事务码>_<卡片名称>_WKnn.xlsx`，相同组织、周和事务码合并，一厂多组织各写一份；无有效映射才回退 `<alvExportDataDirectory>\集采工厂\yyyy_WKnn`。业务范围型 Excel 只要有实际 `WERKS` 列也使用 `_工厂<WERKS>` 后缀，只有没有工厂列时才使用 `_业务范围<GSBER>`。重跑按来源键替换旧行，映射查询失败或 Excel 缺必要列时任务失败并保留暂存文件。
- 含“保存”的 ALV 导出由公共组织归档能力处理：Excel 输出根目录由 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 或环境变量 `SAP_RPA_ALV_EXPORT_DIR` 控制；SAP GUI 始终只写 `D:\RPA\临时文件\ALV本地暂存`。后端从 Excel 实际 `WERKS` 或 `GSBER` 值查 SAP 映射，按 `ZBU\ZSBU\yyyy_WKnn` 归档并合并相同组织、周和事务码的数据；无映射走 `集采工厂\yyyy_WKnn`。业务范围型事务按 Excel 的业务范围列路由，不能用 `ZTSD001`、SQLite `plants`、`config.local.json` 或页面入参推断。只有表头或无来源数据行时清理 raw 文件且不判技术失败；有数据但缺必需来源列或映射/网络归档失败时任务失败并保留本机暂存。
- 正式用户入口是 `https://fi_automation.srv.lstech.com/rpa/`；HTTP 兼容排障入口按目标服务器 IPv4 生成，格式为 `http://<服务器IP>:6174/rpa/`。当前联调服务器是 `10.0.41.158`，正式系统 IP 是 `10.0.2.120`。
- `sap-rpa://` 只作为兼容/历史唤醒入口排查，不是当前生产用户主入口。
- SAP 系统、客户端、账号和密码只保存在本机执行器配置中，网页只传事务码、工厂和业务范围等业务参数。

运维与排查入口：

- 安装、升级、重启和验收按 `上线安装包/README_安装步骤.md` 执行；后端改动必须 `dotnet publish` 后完整复制 publish 输出到 `D:\RPA\bin`，不能只替换 `SapWebLauncher.exe`。
- 直接调用 SAP 的表、函数、NCo/RFC、VBS 清单见 `SAP_直接调用清单.md`。

本机启动器源码在本地目录：

```text
D:\RPA\RpaProject\网页启动登录
```

- ZFI057 step 2 ALV archive row modify key: `BUKRS` + `WERKS` + `KADKY` + `SMATNR/PMATNR` + `STUFE` + `MATNR`. Same organization/week/workbook and identical six-field key modifies the existing row; a different key preserves existing rows and appends a new row.
- Generated ALV workbooks use the logged-in webpage user's personnel number and name as the worksheet name, joined with `_` (for example `10027973_MiRO Wang_王淼榕`). The source is the webpage login identity, never the DingTalk `OV_DDID` recipient value. Excel-invalid characters are replaced, the name is limited to 31 characters, and missing identity falls back to `ALV`. This changes only the worksheet label; file names, organization routing, source-key merge behavior, and ZFI057-specific modify rules are unchanged.
