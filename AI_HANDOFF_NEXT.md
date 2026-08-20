# SAP RPA V2 下一任 AI 交接文档

更新时间：2026-08-14

## 2026-08-14 定时任务月度星期

- 定时任务仍用 `schedule_tasks.weekday` 保存执行星期。新建定时任务页面默认执行时间为 `20:00`，历史任务已保存时间不迁移。`weekly` 必须有 weekday，前端默认周一并在列表显示“每周一/每周二”等；`monthly` 现在也可保存 weekday，新建月任务默认周一，列表显示“每月首个周一/周二”等。
- 后端调度规则：`monthly + weekday` 按当月首个指定星期几和 `run_time` 触发；如果当天已过，下次执行显示为下个月首个该星期几。历史月任务没有 `weekday` 时不补默认值，继续按原来的创建日对应每月日期触发，避免编辑前的旧任务被静默迁移。
- 前端编辑旧月任务且 `weekday` 为空时，下拉会保留“按原每月日期”选项；用户选择具体周几并保存后才切换到新月度星期规则。相关覆盖：`tests/frontend-zfi057-fixed-scope.test.js`、后端内置自测 `monthly schedule explicit weekday keeps legacy day-of-month fallback` 与 `schedule weekday sqlite fallback`。

## 2026-08-11 ZFI057 可编辑业务范围

- ZFI057 以业务范围为唯一页面和任务范围，不再展示或保存“对应工厂 / 新增工厂代码”筛选。`transaction_plant_rules.business_areas_json` 提供工作台执行的默认值和新建定时任务的初始值；定时任务弹窗可增加或删除业务范围标签。
- 定时任务保存时将业务范围写入 `businessAreas` / `params.businessAreas`。后端在 `/api/runs`、`sap-rpa://run`、定时触发和失败批次重跑中保留该任务范围，不再用基础配置覆盖；旧 `zfi057PlantFilter` 工厂筛选字段会被清除且不参与执行。
- 保存空业务范围会被前端阻止。运行时每个已选 `GSBER` 仍只读查询 SAP `ZFIT_RPA_BUKRS-GSBER/WERKS` 的全部映射工厂，并逐工厂执行步骤二；无映射或读取失败时该业务范围在步骤一前返回中文可操作错误。
- 2026-08-11 已发布到 `D:\RPA`：`SapWebLauncher.dll` SHA-256 为 `608A135FC121C0DEF3FBC0712A283D6FF1077E08744824B055DF33FC3675A818`，以及 `portal-state.js`、`portal-api.js`、`portal-actions.js`、`portal-render.js`。升级前备份为 `D:\RPA\deployment-backups\20260811-zfi057-business-area-scope`；`SAP_RPA_StartupMonitor` 已恢复为启用且运行中。项目检查脚本确认本机 API、HTTP 兼容入口、HTTPS 页面和 HTTPS API 均为 200。

## 2026-08-11 ZFI057 工厂映射与后台作业收敛

- ZFI057 的步骤二工厂唯一来源为 SAP 表 `ZFIT_RPA_BUKRS`：以 `GSBER=本次业务范围` 读取全部非空 `WERKS`，按返回顺序去重后由后端逐工厂调用 `ZFI057.vbs`。VBS 强制只接收一个工厂，任何页面、历史任务、SQLite 工厂、SAP 集、GS03 或本地硬编码均不能回退为工厂来源。
- 表查询失败或业务范围没有可执行 `WERKS` 时，必须在步骤一 `ZFI019NL` memory 取物料前直接将该业务范围记为失败，提示维护 `ZFIT_RPA_BUKRS-GSBER/WERKS`；不得因为步骤一无物料而误记为无数据。
- 每个业务范围的步骤三先执行一次 `ZCO020`。脚本在 `ZBZ1=zpp063` 过滤后仅当 ALV 行数大于 0 时才全选并点击保存；0 行时输出 `ZCO020_FILTERED_NO_DATA=1`，不保存、不创建 job、不轮询且不执行第二次 `ZCO020`，范围记为 `no_data`。有数据时，第一次保存后通过 NCo `RFC_READ_TABLE(TBTCO)` 每 10 秒查询本范围步骤二开始前 1 分钟至当前时间的 `JOBNAME=ZFI057`、本次 SAP GUI 用户作业；每个成功工厂至少应有一个 job。全部预期 job 成功完成才执行第二次 `ZCO020`。第二次保存可能产生的 job 按已确认业务规则不需要再次轮询；第二次 VBS 成功即结束范围。第一次 job 任何失败/取消或 10 分钟未收敛时，范围失败且不重复 `ZCO020`。
- `ZFI057.vbs` 不再声称自行读取 `ZFI_SPLIT`；物料集合由后端 NCo 的 `ZFI019NL/ZFI_SPLIT` 上游查询准备后传入 VBS 的 `S_MATNR`。
- 2026-08-11 已在隔离发布目录完成后端自检（84 PASS，2 条既有钉钉文案断言失败），并通过 `tests\zco020-filtered-no-data-contract.test.js`；未启动 SAP GUI 或打开任何 Excel。两条钉钉断言与本次 ZCO020/TBTCO 改动无关。
- 2026-08-11 已上线本次 ZFI057/ZCO020 修复：发布包 `D:\RPA\deployment-staging\20260811-zfi057-zco020` 的 `SapWebLauncher.dll` SHA-256 为 `CF5C65429862F81267081112980BDCD0794E79A050076830314BB0A83888CB59`，已复制到 `D:\RPA\bin`；新 `ZCO020.vbs` 已同步到 `D:\RPA\bin\transactions` 与 `D:\RPA\transactions`。升级前备份在 `D:\RPA\deployment-backups\20260811-zfi057-zco020-deploy`。`SAP_RPA_StartupMonitor` 已恢复，以 `marcus` 会话拉起新 `SapWebLauncher.exe --serve`；项目检查脚本确认本机 API、HTTP 兼容入口、HTTPS 页面和 HTTPS API 均为 200。旧 run 的英文历史错误不会被改写，后续新 run 才会使用新中文错误提示。

## 2026-08-10 ZFI057 保存/导出

- ZFI057 已纳入保存类 ALV 白名单。工作台名称会自动显示为“产值拆分（保存）”，并只加粗加黑“保存”。
- ZFI057 三步流程不变：步骤一通过 NCo 取 ZFI019NL/ZFI_SPLIT 上游物料，步骤二执行 ZFI057.vbs，步骤三执行 ZCO020。步骤二每个成功的日期窗口都会调用 sap_alv_export_helper.vbs 写入本机 ALV 暂存并输出 OUTPUT_FILE；跨月时按发布成本月规则拆成 1 到 3 个窗口，后端按分片顺序合并后再归档。
- ZFI057 不属于 GSBER 路由类报表。它按导出 Excel 的实际 WERKS 逐工厂读取 ZTFI48B-WERKS，按组织、周和事务码合并归档；归档失败保留本机暂存并作为任务失败通知钉钉。不得让 SAP GUI 直接写 UNC 目录，也不得由 Codex 打开导出的 Excel。
- 本轮仅完成代码和静态/自测验证；尚未在 test888 发起真实 ZFI057 作业。发布时必须同步后端 publish 输出、assets/js/portal-render.js、transactions/ZFI057.vbs 与 transactions/sap_alv_export_helper.vbs 到 D:\RPA，并使用本项目启动脚本重启。

## 2026-08-10 ZFI057 业务范围默认配置

- `transaction_plant_rules.business_areas_json` 是 ZFI057 的默认业务范围来源，不是对用户任务的强制覆盖。用户保存的 `businessAreas` 是该次执行和该定时任务的实际范围。
- 归一化后只保留 `businessAreas` 与首项 `businessArea`，并清除 `plants/plant` 与遗留 `zfi057PlantFilter`，避免 ZFI057 混入工厂筛选或将工厂误拆成 workflow scope。没有业务范围的旧请求才回退到规则默认值。
- 业务范围不等于固定工厂。ZFI057 在每个业务范围实际执行时只读查询 SAP 表 `ZFIT_RPA_BUKRS`：`GSBER=业务范围`，取全部 `WERKS` 工厂；多个工厂必须逐个执行步骤二。页面和定时任务允许在默认业务范围基础上增删范围。
- ZFI057 步骤一的东台判定不再读取 JSON 或硬编码范围。后端对每个 `S_GSBER` 只读查询 `ZTFI48A`，任一匹配记录的 `ZSBU` 含“东台”才按东台口径：ALV 行以 `SMATNR=800*` 筛选后取 `MATNR`，并由 NCo `RFC_READ_TABLE` 读取 `ZFI_SPLIT` 的 `BUKRS=2030` 且 `BEGDA/ENDDA` 与本次日期范围重叠的 `MATNR`，两路去重后写入 `ZFI057` 的 `S_MATNR`。`ZFI_SPLIT` 补充物料不做 `800*` 筛选；当前工作流传入的 `SplitWerks` 为空，因此表读取不附加 `WERKS` 条件。查表失败必须使该范围失败，查无 `GSBER` 或 `ZSBU` 不含“东台”则按非东台口径处理。

## 2026-08-07 配置删除与 ZCO019 定时任务模式

- 基础配置页面的红色按钮统一显示“删除”。前端先确认，调用正确的资源删除 API，成功后立即从页面内存移除并重新拉取配置；不能再只将行标记为停用或等待刷新后消失。
- 后端删除工厂、业务范围、事务码规则、事务码和通知机器人时，会从 SQLite 删除记录并写入 `config_delete_markers`，默认播种在服务重启后会跳过该标记，避免已删除的默认项重新出现。显式新增/编辑会清除对应标记，允许维护人员重新添加。
- 历史已完成 `runs` 保留。事务码被定时任务或排队/运行 run 引用、工厂/业务范围仍被规则或任务引用时，后端返回 HTTP `409` 和引用说明，前端不得假装删除成功。
- 定时任务的 `ZCO019` 下拉拆为“标准材料成本（明细保存）”和“标准材料成本（汇总保存）”。前端持久化 `params.runStrategy=detail|summary`；调度器保留该参数，`ZCO019.vbs` 只执行对应的 `P_RADIO1` 或 `P_RADIO2` 分支。历史没有该参数的任务仍按原“明细 + 汇总”全流程执行，编辑时显示兼容选项，不会静默改写。
- 导出文件不得由 Codex/ChatGPT 打开。脚本只按 Windows 默认 `.xlsx` 关联由 Excel 处理，并按本次导出完整路径关闭相应工作簿/进程；禁止按进程名关闭所有 Excel。
- 已在隔离运行目录执行自测：`ZCO019` 的 detail、summary 与非法模式拒绝通过；事务码删除跨 reseed 保持删除。全套自测另外有两条既有 ZFI057 钉钉文案断言失败，均不在本轮变更范围。

## 2026-08-07 ZFI057 卡片实际链路与中文映射失败提示

- 工作台“产值拆分”卡片的唯一生产入口是 `ZFI057`。后端按业务范围依次执行：步骤一通过 SAP NCo 的 `REPORT_SUBMIT/MEMORY_EXPORT` 从 `ZFI019NL` memory 取物料，步骤二执行 `ZFI057.vbs` 并直接在 SAP GUI 输入 `/nZFI057`，步骤三执行 `ZCO020.vbs` 后按 `TBTCO` 状态完成范围闭环。RPA 源码、VBS 和运行日志均没有直接调用 `ZFI085_MAINTAIN`；若需核实 SAP 事务 `ZFI057` 在系统内部绑定的程序，必须由 SAP 管理员在 `SE93` 只读确认。
- ZFI057 的工厂权威来源是 SAP 表 `ZFIT_RPA_BUKRS`，不是 SAP 集或本地配置。后端只读查询 `GSBER=业务范围` 并取全部 `WERKS`；业务范围有上游物料但没有任何可执行工厂时，不得回退本地旧映射、SQLite 或 VBS 硬编码。
- 基础配置的“事务码与工厂/业务范围规则”中，`ZFI057.factoryRule` 只说明可维护的默认业务范围：`本规则仅维护默认业务范围；执行工厂运行时由 SAP 表 ZFIT_RPA_BUKRS 按 GSBER 查询 WERKS，多个工厂逐个执行，不能在此配置或用本地工厂覆盖。` 不能把三步工作流、物料取数或 SAP 内部程序名误写进这条范围配置备注。
- 该失败会回写并通知中文可操作提示：`业务范围 <范围> 未在 SAP 表 ZFIT_RPA_BUKRS 中维护可执行工厂，已在步骤一前停止执行 ZFI057 与 ZCO020 后续步骤。请维护 ZFIT_RPA_BUKRS-GSBER/WERKS 映射，或从任务范围中移除该业务范围。` 这属于 SAP 映射配置失败，不是 SAP GUI、导出或“无数据”失败。日志可以保留技术细节，但钉钉和页面必须展示中文处理建议。
- `FormatZfi057ScopeResultForMessage` 的业务范围失败详情上限为 160 个字符，以保留完整的维护建议；对应自测为 `ZFI057 missing plant mapping is explained in Chinese`。

## 2026-08-07 无数据不是失败

- SAP 明确返回“无数据”“没有符合条件数据”“No data”“No records”或“No matching”时，后端将 run 写为 `no_data`，SAP 状态以 `W` 回写；不生成空 Excel、不进入失败重跑，也不保留临时 VBS 作为故障现场。
- 多工厂批次中，`no_data` 计入已完成但不计入失败：有成功工厂时父 run 为 `success` 并单列无数据数量；全部无数据时父 run 为 `no_data`；只有真实脚本、导出、归档、SAP 登录等错误才会形成 `failed` 或 `partial_failed`。
- 钉钉按“成功通知”开关发送无数据完成消息，标题为“自动化已完成（无数据）”，无数据工厂不进入“失败工厂”列表。前端执行页与历史状态显示“无数据”，不显示为失败。
- `ALV export entry not found or not usable before timeout` 不是无数据标记，仍是导出自动化异常，必须按失败处理并保留诊断信息；不能为了减少告警而把这类错误误判为无数据。
- `ZCO019` 等共用 `sap_alv_export_helper.vbs` 的保存类事务，在 ALV grid 出现后会先等待 SAP GUI 空闲，再重试正常网格工具栏 `&MB_EXPORT -> &XXL` 和 `&XXL`；这些入口没有打开有效的导出格式/文件路径窗口时，才调用由 `Script1.vbs` 整理出的右键 `grid.contextMenu -> &XXL` 兜底，最后才尝试不保证所有报表都存在的顶部 `btn[43]`。任意遗留 `wnd[1]` 不得当成导出窗口，助手会先取消无效弹窗。录制脚本里的 `btn[8]`、第 8 行和 `SMAKTX` 是报表特定状态，不能放入公共助手。只有导出文件对话框实际出现才继续写文件。超时日志会列出当前 `wnd[0]/tbar[1]` 可用的 `btn[n]`、tooltip 和 text，用于确认服务器 SAP GUI 的真实导出按钮，不得把该错误降级为无数据。
- 2026-08-07 已用 `ZCO019` 工厂 `1022`、测试日期 `2026.06.01 ~ 2026.06.07` 真实验收：run `RUN-20260807142133-ZCO019-f48b8a0b79d14bfeb18b0084436bd9` 的两段 ALV 均走 `Script1` 兜底，临时 Excel 已就绪并合并归档到 `\\10.0.16.31\财务管报自动化\2026_WK32\1022\ZCO019_标准材料成本_工厂1022_20260807142139.xlsx`。运行服务日志可证明归档成功；当前 Codex 进程对该 UNC 根目录无直接浏览权限。

## 2026-08-07 ZCO019 明细与保存文件分开

- 上述真实验收为切分前的历史结果。此后 `ZCO019.vbs` 第一段导出固定使用 ASCII 暂存后缀 `_detail.xlsx`，第二段固定使用 `_saved.xlsx`；不再使用 `_part1/_part2`，因此后端不会把两种业务结果误判为同一份窗口分段并合并。
- 组织归档只对 `ZCO019` 的这两个后缀映射用户可见名称：`标准材料成本_明细` 和 `标准材料成本_保存`。最终命中组织时为 `<alvExportDataDirectory>\ZBU\ZSBU\yyyy_WKnn\ZCO019_标准材料成本_明细_WKnn.xlsx` 与 `..._保存_WKnn.xlsx`；未命中时也保持同样的名称区分再追加工厂号。
- 两种文件各自按工厂来源去重和重跑，明细不会覆盖保存，保存也不会删除明细。自检已用两个不同内容的 `1022` 工作簿验证独立组织路由，两个最终工作簿分别保留各自行内容。
- 已发布到运行目录：`D:\RPA\transactions\ZCO019.vbs` 和 `D:\RPA\bin\SapWebLauncher.dll`；发布前备份在 `D:\RPA\deployment-backups\20260807-zco019-distinct-detail-saved`。生产 API、HTTPS 入口和 HTTP 兼容入口在发布后均返回 `200`。

## 2026-08-06 ALV 组织归档规则（覆盖本文此前周/工厂目录说明）

- 保存类 ALV 的最终归档是公共后端能力，当前覆盖 `ZFI072A`、`ZFI072N`、`ZFI080`、`ZFI080B`、`ZCO019`、`ZFI019NA`、`ZFI019NL`、`ZFI148`；不能为 `103C` 或任一具体工厂硬编码。
- 2026-08-19 归档规则已更新：`ZFI019NL` 和 `ZFI019NA` 都是业务范围 child run，VBS 均填写 `S_GSBER-LOW`；归档必须用请求 `businessArea` 查询 `ZTFI48A-GSBER -> ZBU/ZSBU`，不得强制要求 Excel 含 `GSBER` 列。Excel 若有 `WERKS`，仅用于来源键、重跑替换和行追溯。两者均不得回退按工厂入参或 `ZTFI48B` 路由；其余卡片保持各自既有映射入口。
- 命中组织时的最终文件为 `<alvExportDataDirectory>\<ZBU>\<ZSBU>\yyyy_WKnn\<事务码>_<卡片名称>_WKnn.xlsx`。`ZBU` 和 `ZSBU` 是两层组织，周目录固定在其下；同一 `ZBU+ZSBU+周+事务码` 的所有工厂数据合并到同一工作簿；一厂映射多个组织时向每个组织各写一份。重跑同一工厂或业务范围要替换该来源旧行，不能重复追加。
- 查询成功但未命中组织时回退到 `<alvExportDataDirectory>\集采工厂\yyyy_WKnn\<事务码>_<卡片名称>_WKnn_工厂<WERKS>.xlsx`。业务范围型 Excel 只要实际包含 `WERKS`，也必须按该工厂名回退；仅没有任何工厂列时才可用 `_业务范围<GSBER>.xlsx`。`集采工厂` 与 `BU1`、`BU2` 等一级组织同级，但它没有 `ZSBU` 层；查询失败、Excel 缺必需列或必需值时任务失败，保留本机暂存供排障，不得伪造成功。
- 网络盘根目录只由运行目录 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 控制；证书、真实配置、网络盘 Excel 都不得提交 Git。发布后必须用真实 SAP 导出验证最终网络目录和 Excel 行数。
- SAP GUI 导出只能写 `fileStorage.alvExportStagingDirectory`（当前 `D:\RPA\临时文件\ALV本地暂存`），由 `SapWebLauncher` 以实际执行账户复制到 `alvExportDataDirectory`、校验文件大小并原子落盘；本机暂存写入失败与网络归档失败必须分别记录，不能把前者误判为网络共享盘无权限。服务重启时，上一执行器遗留的 `running` 子任务必须立即标记失败、通知并保留本机暂存，不能等待 6 小时阻塞队列。

- 执行器重启回收仅针对带 `locked_by=<当前执行账户>|queue`（兼容历史同账户租约）的桥服务队列任务；`sap-rpa://` fallback 没有队列租约，不能被服务启动或 6 小时守护误杀。所有完成、超时和重启失败都会清空租约字段。暂存目录只能是 `D:\RPA\临时文件\ALV本地暂存` 或其子目录；UNC 暂存和暂存/归档目录相同都会拒绝或回退，确保 SAP GUI 从不直接写网络盘。

## 2026-08-05 SAP 登录与测试日期控件修正

- SAP GUI 自动登录同时处理两类受控窗口：已有登录会话的多重登录接管窗口，以及空用户的标准登录页。后者只在当前 Windows 用户的 DPAPI 本机登录配置可用、窗口为 `S000` / `SAPMSYST` 时填写 client、user、password、language；密码仅经本次 `cscript.exe` 子进程的私有环境变量传递，写入密码框后立即清空，不写入临时 VBS、API、日志或 Git。
- SAP NCo 连接别名可能与 SAP GUI 实际 SID 不同。例如测试配置为 `test888`、SAP GUI 实际系统为 `TD1` 时，目标会话匹配必须同时识别 `connectionName`、`name` 和 `systemId`。2026-08-05 已以 `ZFI072A`、工厂 `5021`、测试周 `2026-W31` 真实验证成功：run `RUN-20260805100148-ZFI072A-8c9ab514805942fabbab2a97d55b9`，网络归档与钉钉成功通知均已落地。
- 测试日期控件必须以发布 VBS 实际消费的 `{PERIOD}`、`{WEEK_END}`、`{YEAR}`、`{WEEK}` 占位符和 SAP 写屏字段为准，不能只看可能滞后的 `@params` 头注释：
  - `ZFI072A`：仅 ISO 周，网页只显示测试周，提交 `year/week` 及其派生的 `period/weekEnd`，不显示或提交开始/截止日期字段。
  - `ZFI057`：VBS 同时接收 `year/week` 和 `period/weekEnd`。页面同屏显示 ISO 周、开始日期、截止日期：改周会回填该周日期范围，改开始日期会回填对应 ISO 周；提交始终带齐 `year/week/period/weekEnd`。日期范围模式不提交 `testIsoWeek`，避免后端按整周覆盖手工日期范围。
  - `ZCO019`、`ZCO020`、`ZFI019NA`、`ZFI019NL`、`ZFI072N`、`ZFI080`、`ZFI080B`、`ZFI148`、`ZFIR034`：仅日期范围。
  - 保存类卡片全部有测试日期输入：`ZFI072A` 输入 ISO 周；其余保存类 `ZFI072N`、`ZFI080`、`ZFI080B`、`ZCO019`、`ZFI019NA`、`ZFI019NL` 输入开始/截止日期。周结完工成本明细表的 `ZFI019NL`、`ZFI019NA`、`ZFI148` 三张卡均显示日期范围。
- 前端源文件是 `D:\RPA\RpaProject\assets\js\portal-utils.js` 与 `portal-render.js`；部署时必须同步复制到运行目录 `D:\RPA\assets\js`，仅修改 Git 源码不会影响正式网页。

## 2026-08-05 工厂规则配置刷新与联动

- 工厂主数据目录与事务码/业务范围规则的显式工厂代码是两类数据：规则允许先配置一个有效 SAP 工厂代码，再补充可选的门户工厂主数据。因此 `/api/config` 刷新时不得以“该代码不在 `plants` 或已停用”为由，静默删除 `plantGroups.plants`、`zfi072Plants`、`zco019Plants` 或事务规则 `fixedPlants`。
- 规则弹窗的“新增工厂代码”只会在点击新增或回车成功后清空输入，并必须即时显示新工厂标签和“已新增工厂 <code>”反馈；重复代码保留输入并提示，不得伪装为成功。
- 对按工厂执行的事务规则，页面必须实时展示由工厂主数据推导的“关联业务范围”。工厂代码本身允许保留；若主数据缺少该代码或该代码没有业务范围，则显示缺失提示，不能因此删除配置。保存后刷新页面仍必须保留该显式工厂代码。
- 本轮改动位于 `assets/js/portal-api.js`、`portal-render.js`、`portal-actions.js`；发布时三者均须复制到 `D:\RPA\assets\js`。网关静态 JS 缓存最长 60 秒，紧急验证使用浏览器硬刷新。

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

- 当前服务器源码仓库：`D:\RPA\RpaProject`
- 当前服务器运行根目录：`D:\RPA`
- 前端入口：`D:\RPA\index.html` + `D:\RPA\assets\js\*.js`
- 后端源码入口：`D:\RPA\RpaProject\网页启动登录\SapWebLauncher\Program.cs`
- 后端运行入口：`D:\RPA\bin\SapWebLauncher.exe --serve`
- VBS 运行脚本：`D:\RPA\transactions`
- SQLite 运行库：`D:\RPA\data\sap-rpa-config.db`
- 功能说明书：`D:\RPA\RpaProject\SapRpa_V2_功能说明书.html`
- 安装包权威清单：`D:\RPA\RpaProject\上线安装包\上线安装文档清单.md`
- 生产部署拷贝清单：`D:\RPA\RpaProject\上线安装包\生产部署拷贝清单.md`
- Windows Server 手工部署 runbook：`D:\RPA\RpaProject\上线安装包\最终上线部署步骤\README_V2_Windows_Server_上线部署.md`

## 2026-07-29 上线/生产部署最新状态

- 正式用户入口是 `https://fi_automation.srv.lstech.com/rpa/`；HTTP 兼容排障入口按目标服务器 IP 生成，格式为 `http://<服务器IP>:6174/rpa/`。当前联调服务器示例是 `http://10.0.41.158:6174/rpa/`，正式系统 IP 是 `10.0.2.120`，正式系统兼容入口应为 `http://10.0.2.120:6174/rpa/`，不要把测试 IP 照抄到生产机。
- 端口口径：`SapWebLauncher.exe --serve` 只作为本机 API 监听 `127.0.0.1:8080`；`gateway\rpa-gateway.js` 默认监听 `0.0.0.0:6174`，路径前缀固定 `/rpa/`，并把 `/rpa/api/*` 代理到本机 API；正式 HTTPS 用户入口仍是 `https://fi_automation.srv.lstech.com/rpa/`。不要占用或修改别人项目使用的 80、6173 等端口。
- `D:\RPA\启动脚本\start_sap_rpa_services.cmd` 和 `check_sap_rpa_services.cmd` 已支持把参数透传给 PowerShell。默认自动识别本机 IPv4；正式机有多网卡或要强制指定时，用 `-HttpCompatibilityHost 10.0.2.120`，或设置环境变量 `RPA_HTTP_COMPATIBILITY_HOST=10.0.2.120`。
- 生产部署拷贝边界以 `上线安装包\生产部署拷贝清单.md` 为准：可以把 `D:\RPA` 当作程序包整体拷贝到生产机，但只能带走 `index.html`、`assets`、`gateway`、`启动脚本`、`bin`、`transactions`、`依赖\SapNco`、`config.local.example.json` 和运维文档。
- GitHub 仓库只保留 `D:\RPA\RpaProject\上线安装包` 这一套最终上线资料；运行根目录下旧 `安装包`、旧 `上线安装包`、`临时文件`、`vp` 都不是源码交付物，不应提交。`D:\RPA\临时文件\文件数据` 可能被运行时按默认导出路径自动重建，生产以 `config.local.json` 的 `fileStorage.alvExportDataDirectory` 为准。
- 生产机专属状态不能被测试机覆盖：`D:\RPA\config.local.json`、`D:\RPA\data\sap-rpa-config.db`、`D:\RPA\logs\`、`D:\RPA\outputs\`、`D:\RPA\certs\lstech.com\`、`%LOCALAPPDATA%\SapWebLauncher\config.json`。全新生产机要重新填写/放置/生成这些内容；已有生产机升级要先备份并默认保留。
- `D:\RPA\certs` 不是技术上只能安装不能复制；它可以作为受控生产证书备份/迁移材料复制。但它是 secret，不能进 GitHub、普通安装包、公开 zip 或聊天明文附件；全新生产机放生产证书，升级已有生产机保留现有证书，复制/替换后必须重设 ACL 并验收 HTTPS。
- 含“保存”的 ALV Excel 导出覆盖 8 个事务码：`ZFI072A`、`ZFI072N`、`ZFI080`、`ZFI080B`、`ZCO019`、`ZFI019NA`、`ZFI019NL`、`ZFI148`。`ZFI019NI` 是无生产 VBS 的旧残留，已从默认前端 fallback 和 `transaction-config.json` 移除；后端历史 run 名称解析可以保留，不代表它是生产入口。
- Excel 输出根目录必须由运行目录真实配置 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 控制，默认 `D:\RPA\临时文件\文件数据`；临时覆盖可用环境变量 `SAP_RPA_ALV_EXPORT_DIR`。生产机换网络共享盘时只改配置并重启后端，不改 VBS 或 C#。
- 保存类 ALV 的最终文件必须按组织和周归档：命中组织时为 `<alvExportDataDirectory>\ZBU\ZSBU\yyyy_WKnn\事务码_卡片名称_WKnn.xlsx`，同一组织、周和事务码的工厂数据合并；未命中组织时为 `<alvExportDataDirectory>\集采工厂\yyyy_WKnn\事务码_卡片名称_WKnn_工厂工厂号.xlsx`。本机暂存仍在 `D:\RPA\临时文件\ALV本地暂存`，绝不按最终网络目录直接导出；旧目录不自动搬迁。
- 上线/升级后必须从运行目录验证：publish 输出完整复制到 `D:\RPA\bin`，`assets`、`gateway`、`启动脚本`、`transactions` 同步到 `D:\RPA`，执行 `--init-db` 保留并迁移 SQLite，只重启本项目 `SapWebLauncher.exe --serve`。
- 生成或交付安装包后，必须从最终包或解压目录跑一次真实路径验收：启动服务、打开正式 HTTPS、提交受控任务、确认 SQLite run/log、钉钉日志和保存类 Excel 落到配置目录。

## Git 状态

- GitHub 仓库：`https://github.com/ckstock/sap_rpa`
- 当前分支：`sap-rpa-v2-local-service`
- 注意：`D:\RPA` 是当前运行根目录，`D:\RPA\RpaProject` 是 GitHub 源码仓库。`git pull` 后必须 publish/copy 到运行目录才会线上生效。
- 历史提交点（不是当前最新提交；以 `git log -1` 和远端分支为准）：
  - `1a4728e fix: enforce token notify account mode`：当前 GitHub 远端旧分支已到此提交，包含 `AI_HANDOFF_NEXT.md`、token 通知模式和前端身份入口调整。
  - `3bead51 fix: auto-login external token jumps`：外部门户 token 直跳后自动进入执行页。
  - `cc437a2 feat: prepare server installer for port 8080`：历史提交，曾加入服务器一键安装包和 `SapWebLauncher` 本地 API/SQLite/部署脚本；当前安装包方向已改为手工清单。
  - `acfa314 chore: checkpoint v2 before frontend modularization`：前端模块化前回退点。
  - `41befc0 refactor: split portal javascript modules`：把 `index.html` 内联 JS 拆成 `assets/js/*.js`。
- 当前工作前必须检查 `git status` 和 diff，确认没有数据库、日志、真实 `config.local.json`、钉钉密钥或 SAP 密码。

### 2026-07-08 运行副本漂移排查结论

- 历史排查时曾确认旧目录 `D:\工作\sap_rpa` 的 `HEAD` 和 GitHub 远端旧分支一致：`1a4728edd925d9a984f9d245739d7f7ada84cf59`。
- 已确认最新提交 `1a4728e` 包含本交接文档 `AI_HANDOFF_NEXT.md`。
- 之前的问题根因是本机运行副本没有同步到最新源码；当前服务器应优先检查 `D:\RPA\bin\SapWebLauncher.exe` 是否来自最新 publish，以及是否已重启。
- 如果还保留 `sap-rpa://` 协议注册表入口，需确认它没有指向旧的 `%LOCALAPPDATA%\SapRpaLauncher\SapWebLauncher.exe`。
- 如果用户反馈“GitHub 里修过的 SAP 已登录复用/避免重复登录问题又出现”，不要先判断为代码未提交；优先检查实际运行的 `SapWebLauncher.exe` 版本、路径、协议注册入口和是否仍是旧构建。
- 现有源码在 `Program.cs` 中会先执行 `ProbeSapSession`；探测到 ready SAP GUI session 时日志应出现 `Detected ready SAP GUI session; skip sapshcut login`。如果实际日志没有这句，重点排查运行的是不是旧 exe、服务是否未重启、`sap-rpa://` 是否指向 `%LOCALAPPDATA%\SapRpaLauncher` 旧副本。

## 最近完成的文档与安装包更新

1. 已按用户要求删除一键安装器方向，安装包改为“手工安装指南 + 必要脚本”。
2. 当前保留的安装包入口：
   - `上线安装文档清单.md`
   - `最终上线部署步骤\README_V2_Windows_Server_上线部署.md`
   - `config.local.example.json`
   - `00_生成上线安装包.cmd`
   - `scripts\make_package.ps1`
   - `04_配置SAP登录信息.bat`
   - `scripts\configure_sap_login.ps1`
3. 已删除旧的一键安装、检测、卸载、服务器一键安装包，以及最终上线部署步骤中的自动准备/安装/初始化/启动/检测/打开页面脚本。
4. 安装指南已补充路径清单、真实 `config.local.json`、SAP GUI 脚本组件注册、Git 更新后必须 publish/copy 到 `D:\RPA\bin/assets/transactions`、只重启本项目 `SapWebLauncher.exe --serve` 的要求。
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
- 工厂主数据列表会过滤 `enabled=false`；但业务范围和事务码规则中由用户显式保存的工厂代码必须在配置刷新后保留。代码未登记或缺少业务范围时显示缺失提示，不能静默清理。
- SAP GUI 执行保持串行，不并发操作同一个桌面会话。
- ZFI072A 是当前优先打通事务码。
- 多工厂执行应记录父 run 和每个工厂子 run；开始通知一次，结束汇总通知一次，失败工厂支持重跑。
- ZFIR034 已接入为日期范围事务：页面/API/协议入口/定时触发器都不传 `plants` 或 `businessAreas`，默认按运行时系统日期取上一完整自然周并写入 `period/weekEnd/year/week`；VBS 写 `S_BUDAT-LOW/HIGH`，并按最终开始日期推导年度和周次后写入 `P_GJAHR/P_WEEK`（优先 `wnd[0]/usr/txtP_GJAHR`、`wnd[0]/usr/txtP_WEEK`，兜底 `ctxt` 字段）。系统日期 `2026-07-02` 时应得到 `2026.06.22` 到 `2026.06.28`，`P_GJAHR=2026`、`P_WEEK=26`。钉钉通知只显示日期范围，不显示工厂或业务范围。
- 钉钉通知由后端发送，不由 VBS 进入 SAP 再调用函数。
- 外部门户 `https://lydctest.lstech.com/dataAnalysis.financial.sapschedule` 会调用本页面，可通过页面 URL 传入 `?token=<jwt>`、`?authorization=Bearer ...` 或 `?access_token=<jwt>`；前端兼容解析 JWT payload 的 `Account/Ddid/ddid/DingTalkUserId/UserId` 作为钉钉 ID，兼容解析 `UserName/Name/RealName/DisplayName` 作为姓名，不验签、不保存 token 原文，并在加载后清理 URL。页面不再显示账号/密码、“钉钉扫码登录”界面或“清除身份”按钮；执行页保留“勾选固定通知 11464769”复选框，勾选时提交 `11464769`，不勾选时必须使用本次 URL token 解析出的钉钉 ID。识别到合法钉钉 ID 后页面进入执行页、顶部身份位置同时显示姓名和钉钉 ID 并自动取消固定通知勾选，提交 `/api/runs` 时把钉钉 ID 写入 `operator.dingTalkUserId` 和 `operator.ddid`；不勾选且未解析到钉钉 ID 时禁止触发执行。生产可信身份仍必须由后端/SSO 验签确认。
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
D:\RPA\RpaProject\agent.md
D:\RPA\RpaProject\SapRpa_V2_功能说明书.html
D:\RPA\RpaProject\上线安装包\最终上线部署步骤\SapRpa_V2_技术设计说明.html
D:\RPA\RpaProject\DATABASE_FIELD_DESIGN.md
D:\RPA\RpaProject\AI_HANDOFF_NEXT.md

当前前端已经完成第一阶段模块化：源码入口是 D:\RPA\RpaProject\index.html，脚本在 D:\RPA\RpaProject\assets\js\*.js；线上运行入口是 D:\RPA\index.html + D:\RPA\assets\js\*.js。
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
git -C "D:\RPA\RpaProject" status --short --branch

$node='C:\Users\chen.kai6\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
Get-ChildItem 'D:\RPA\RpaProject\assets\js\*.js' | ForEach-Object { & $node --check $_.FullName }
Select-String -Path 'D:\RPA\RpaProject\index.html' -Pattern 'assets/js/portal-state.js','assets/js/portal-utils.js','assets/js/portal-api.js','assets/js/portal-render.js','assets/js/portal-actions.js','assets/js/main.js'

D:\RPA\bin\SapWebLauncher.exe test

$packageScript='D:\RPA\RpaProject\上线安装包\scripts\make_package.ps1'
$text=Get-Content -LiteralPath $packageScript -Raw -Encoding UTF8
[ScriptBlock]::Create($text) | Out-Null
```

## 换新对话框建议

建议换新对话框继续。当前窗口上下文很长，下一步是新模块级设计，换框更省 token，也更容易让下一任 AI 只聚焦“可编排 SAP 报表链路”。

如果只是小功能维护，可以不强制换窗口，但必须先读本交接文档、功能说明书和 `agent.md`，并优先用 GitNexus/diff 定位影响面。大功能或继续拆 `portal-render.js` 时建议新对话框。
