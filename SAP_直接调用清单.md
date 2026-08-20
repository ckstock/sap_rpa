# SAP 直接调用清单

本文用于排查当前系统直接调用的 SAP 表、函数、RFC、NCo、事务码和 VBS。依据为静态扫描以下文件：

- `网页启动登录\SapWebLauncher\Program.cs`
- `网页启动登录\SapWebLauncher\Zfi019NlMemoryFetcher.cs`
- `网页启动登录\transactions\*.vbs`
- `网页启动登录\transactions\transaction-config.json`

未读取或记录 `config.local.json` 的真实内容；本文只列配置键名和排查方向，不包含真实账号、密码、密钥、服务器地址。

## 1. 调用总览

| 类别 | 对象 | 调用位置 | 用途 | 主要输入 | 输出/依赖 | 排查建议 |
| --- | --- | --- | --- | --- | --- | --- |
| NCo/RFC | `SAP.Middleware.Connector` | `Zfi019NlMemoryFetcher.cs` | 建立 SAP NCo destination 并调用 RFC/function | `sapNco`/`nco`/`sapDestination` 配置键，或 SAP Logon 条目兜底；系统号、客户端、用户、语言、router | `RfcDestination`、`IRfcFunction`、`IRfcTable` | 先查 NCo 配置是否完整，再查 SAP Logon 映射；日志应只看 `SafeSummary()` 的脱敏摘要 |
| RFC function | `ZFI_SAP_API_GATEWAY` | `Zfi019NlMemoryFetcher.cs`；`Program.cs` 的 ZFI057 step1 | `REPORT_SUBMIT` 提交报表并读取 memory/export 结果 | `IV_ACTION=REPORT_SUBMIT`、`IV_JSON_IN` | `EV_SUBRC`、`EV_MSG`、`EV_JSON_OUT` | 若失败，按 `EV_SUBRC/EV_MSG`、JSON 入参结构、ABAP 网关版本排查 |
| RFC function | `RFC_READ_TABLE` | `Zfi019NlMemoryFetcher.cs`、`Zfi057BusinessAreaPlantFetcher.cs` | 读取 SAP 表 `TBTCO`、`ZFI_SPLIT`、`ZFIT_RPA_BUKRS` | `QUERY_TABLE`、`OPTIONS`、`FIELDS`、`DELIMITER=|`、`ROWCOUNT=0` | `DATA-WA`，按 `FIELDS` offset/length 拆行 | 重点检查 RFC 权限、字段长度、`OPTIONS` 单行 72 字符限制、日期格式 |
| SAP 表 | `TBTCO` | `SapJobStatusFetcher.FetchLatest` | ZFI057 工作流 step3 后轮询后台 job 状态 | `JOBNAME='ZFI057'`，可带 `SDLUNAME`，按提交时间窗口筛选 | `JOBNAME, JOBCOUNT, SDLUNAME, STATUS, SDLSTRTDT, SDLSTRTTM, STRTDATE, STRTTIME, ENDDATE, ENDTIME` | 对照 SAP GUI 用户和 NCo 用户；状态 `F` 成功，`A/C` 失败，`R/Y/S/P` 未终止 |
| SAP 表 | `ZFI_SPLIT` | `Zfi019NlMemoryFetcher.cs`；`ZFI057.vbs` 仅打印提示 | 特殊业务范围补充物料 | 默认 `BUKRS=2030`，可配 `splitBukrs`；`WERKS`、`BEGDA<=high`、`ENDDA>=low` | `BUKRS, WERKS, MATNR, BEGDA, ENDDA, MTART` | 只有后端 NCo/RFC 读取真实表；GUI VBS 不查表，只提示需后端补数 |
| SAP GUI Scripting | `GetObject("SAPGUI")` + `findById` | 所有扫描到的 `transactions\*.vbs` | 复用已登录 SAP GUI session，进入事务码并填选择屏幕 | `{OK_CODE}`、`{PLANTS}`、`{BUSINESS_AREAS}`、`{YEAR}`、`{WEEK}`、`{PERIOD}`、`{WEEK_END}` 等模板变量 | SAP 状态栏、ALV、导出文件、`STATUS_TEXT`/`SAP_STATUS_*`/`MATERIALS_CSV` 等 stdout 标记 | 先确认 SAP GUI 登录、脚本权限、命令框 `wnd[0]/tbar[0]/okcd` 可用，再看 VBS stdout/stderr |

## 2. C# NCo/RFC 明细

### 2.1 NCo 连接配置

`Program.cs` 通过 `BuildSapNcoConnectionConfig` 组装 `SapNcoConnectionConfig`，`Zfi019NlMemoryFetcher.cs` 通过 `SapRpaNcoDestinationProvider.GetDestination` 注册并获取 `RfcDestination`。

| 配置来源 | 支持键名 | 用途 | 排查建议 |
| --- | --- | --- | --- |
| 本地配置对象 | `sapNco`、`nco`、`sapDestination` | NCo 连接主配置对象 | 只检查键是否存在和值是否为空，不在文档或日志中粘贴真实值 |
| 连接名称 | `connectionName`、`destinationName`、`name` | `RfcConfigParameters.Name` | 与 SAP Logon 条目或系统标识不一致时会影响兜底匹配 |
| 系统标识 | `systemId`、`sysId`、`sid` | `RfcConfigParameters.SystemID` | 缺失时会尝试从 SAP Logon 条目补齐 |
| 主机 | `ipAddress`、`server`、`appServerHost`、`ashost` | `RfcConfigParameters.AppServerHost` | 连接失败先查这里和网络连通性 |
| 系统号 | `systemNumber`、`instanceNumber`、`sysNr` | `RfcConfigParameters.SystemNumber` | 应为规范系统号；代码会 normalize |
| 客户端/用户/密码/语言 | `client`、`user`、`password`、`passwd`、`passwordProtected`、`passwdProtected`、`language`、`lang` | 登录凭据与语言 | 不要明文传播；若 protected 密码不可解，表现为配置缺失或登录失败 |
| 路由 | `router`、`sapRouter` | `RfcConfigParameters.SAPRouter` | 仅确认是否配置，不记录真实 router 串 |

连接完整性要求：`IpAddress`、`SystemNumber`、`Client`、`User`、`Password` 必须非空。

### 2.2 `RFC_READ_TABLE` - `ZFIT_RPA_BUKRS`

| 项 | 内容 |
| --- | --- |
| 调用位置 | `Zfi057BusinessAreaPlantFetcher.Fetch`；`Program.cs` 的 `ResolveZfi057BusinessAreaPlants` |
| 用途 | ZFI057 工作流按业务范围获取工厂清单 |
| RFC/function | `RFC_READ_TABLE` |
| 查询表 | `ZFIT_RPA_BUKRS` |
| 关键入参 | `OPTIONS: GSBER = '<业务范围>'`；`FIELDS: GSBER, WERKS` |
| 输出 | `DATA-WA`；同一 `GSBER` 的全部非空 `WERKS` 按原顺序去重 |
| 依赖 | NCo 连接完整；执行账户有 `ZFIT_RPA_BUKRS` 及 `GSBER/WERKS` 的只读权限 |
| 排查建议 | 若为空，确认该业务范围的 `WERKS` 维护；若 RFC 抛错，检查表/字段授权，不得回退本地映射 |

### 2.3 `ZFI_SAP_API_GATEWAY` - `REPORT_SUBMIT`

| 项 | 内容 |
| --- | --- |
| 调用位置 | `Zfi019NlMemoryFetcher.Fetch`，由 `Program.cs` 的 ZFI057 step1 调用 |
| 用途 | 提交报表 `ZFI019NL`，读取 memory/export 的 ALV 物料结果 |
| RFC/function | `ZFI_SAP_API_GATEWAY` |
| 固定 action | `IV_ACTION = REPORT_SUBMIT` |
| JSON 入参 | `method=MEMORY_EXPORT`、`report=ZFI019NL`、`variant`、`spool_device` 默认 `LP01`、`wait_seconds` 默认 `60`、`options` |
| options | `MEMORY_ID` 默认 `%ZFI019NA%`，`MEMORY_NAME` 默认 `GT_ALV`；选择条件包含 `S_BUDAT`、`S_GSBER`，支持 `P_` 参数和选择表条件 |
| 输出 | `EV_SUBRC`、`EV_MSG`、`EV_JSON_OUT`；从 `ET_LINES/LINES` 解析 ALV 文本表 |
| 后处理 | 解析字段 `GSBER/S_GSBER`、`S_BUDAT/BUDAT`、`SMATNR`、`MATNR`；输出最终物料列和来源列 |
| 排查建议 | 失败时确认 ABAP 网关是否支持 `MEMORY_EXPORT`、memory id/name 是否与 ABAP export 一致、`S_BUDAT/S_GSBER` 是否能出 ALV、`ET_LINES` 是否存在 |

### 2.4 `RFC_READ_TABLE` - `TBTCO`

| 项 | 内容 |
| --- | --- |
| 调用位置 | `SapJobStatusFetcher.FetchLatest`；ZFI057 step3 scope closure |
| 用途 | 第一次 `ZCO020` 后轮询 `ZFI057` 后台 job 是否终止，终止后再跑第二次 `ZCO020` |
| 表 | `TBTCO` |
| 字段 | `JOBNAME, JOBCOUNT, SDLUNAME, STATUS, SDLSTRTDT, SDLSTRTTM, STRTDATE, STRTTIME, ENDDATE, ENDTIME` |
| 条件 | `JOBNAME='ZFI057'`，可选 `SDLUNAME`，按本地时间转换后的开始/结束窗口筛选 |
| 输出 | 最新匹配 job 的状态、开始/结束时间、状态类别 |
| 排查建议 | 无匹配时看 job 用户是否与 SAP GUI 用户一致；跨时区/时间窗口问题看日志中的 lower/upper；`OPTIONS` 每行不得超过 72 字符 |

### 2.5 `RFC_READ_TABLE` - `ZFI_SPLIT`

| 项 | 内容 |
| --- | --- |
| 调用位置 | `AppendDongtaiSplitMaterials` |
| 用途 | 特殊业务范围补充物料清单 |
| 表 | 默认 `ZFI_SPLIT`，可由 `zfi019nlMemory.splitTable` 改名 |
| 字段 | `BUKRS, WERKS, MATNR, BEGDA, ENDDA, MTART` |
| 条件 | `BUKRS` 默认 `2030`，可由 `splitBukrs` 改；可带 `WERKS`；`BEGDA <= 日期上限` 且 `ENDDA >= 日期下限` |
| 触发 | 以 SAP 表 `ZTFI48A-GSBER` 匹配本次业务范围；任一匹配记录的 `ZTFI48A-ZSBU` 含“东台”即启用东台物料口径 |
| 东台判定来源 | 只读 SAP 表 `ZTFI48A-GSBER/ZSBU`；不读取 `dongtaiBusinessAreas`、`specialBusinessAreas` 或页面配置 |
| 排查建议 | 若物料缺失，先确认 `S_BUDAT/BUDAT` 是明确 include 日期范围；再查 `BUKRS/WERKS`、`ZTFI48A-GSBER/ZSBU` 维护和表授权 |

## 3. VBS 事务码清单

以下为当前 `transactions` 目录中实际存在的 VBS 文件。所有事务脚本共性：

- 通过 `GetObject("SAPGUI")` 获取 SAP GUI scripting engine。
- 通过 `wnd[0]/tbar[0]/okcd` 输入 `"/n" & tcode` 进入事务码。
- 依赖 SapWebLauncher 注入模板变量，例如 `{OK_CODE}`、`{PLANTS}`、`{BUSINESS_AREAS}`、`{YEAR}`、`{WEEK}`、`{PERIOD}`、`{WEEK_END}`、`{ALV_EXPORT_DIR}`、`{ALV_EXPORT_FILENAME}`、`{MATERIALS}`。含“保存”的 ALV 导出根目录来自 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 或环境变量 `SAP_RPA_ALV_EXPORT_DIR`，默认 `D:\RPA\临时文件\文件数据`。业务范围型保存事务导出后按 Excel 实际工厂字段拆分，支持 `WERKS`、`Plant Code`、`工厂`、`工厂号`、`工厂代码`、`大BU-工厂`、`业务范围-小厂` 等表头，输出同样落到 `<alvExportDataDirectory>\yyyy_WKnn_工厂`。拆分依据只允许使用导出 Excel 的工厂列实际值；只有表头或无工厂数据行时会删除 raw 文件、业务范围子目录和空的 `_raw_business_area` 根目录，并视为无导出数据；有业务行但缺工厂列或工厂值为空时任务失败并保留 raw 供排查，不得用 `ZTSD001`、SQLite `plants`、`config.local.json`、业务范围入参或本地映射推断。
- 通过 stdout 标记返回运行信息，例如 `STATUS_TEXT=`、`SAP_STATUS_TYPE=`、`SAP_STATUS_TEXT=`、`MATERIAL_COUNT=`、`MATERIALS_CSV=`。

| VBS | 事务码 | 配置名称 | 用途 | 关键入参/屏幕字段 | 输出/依赖 | 排查建议 |
| --- | --- | --- | --- | --- | --- | --- |
| `ZFI072A.vbs` | `ZFI072A` | 采购价月表 | 按年度/周次/工厂查询采购价月表并导出 ALV | `P_GJAHR`、`P_WEEK`、`S_WERKS` 多选；可用 `ALV_EXPORT_DIR/FILENAME` | 工厂级 ALV Excel，目录为 `<alvExportDataDirectory>\yyyy_WKnn_工厂`；`SAP_STATUS_*`；依赖 SAP GUI session 和 ALV 导出按钮 | 多工厂通过多选粘贴；若导出失败，查 ALV 是否有数据、按钮 `btn[43]`、文件路径权限 |
| `ZFI072N.vbs` | `ZFI072N` | 维护采购价 | 按工厂和过账日期窗口执行维护/保存；跨月会拆多个窗口 | `S_WERKS-LOW`、`S_BUDAT-LOW/HIGH`；`period/weekEnd` 推导日期 | 工厂级 ALV Excel，目录为 `<alvExportDataDirectory>\yyyy_WKnn_工厂`；跨月 `_part1/_part2` 会被后端合并为一个工厂文件；最终 `STATUS_TEXT=Automated transaction finished` | 跨月问题看日志 `date input group #` 和 `ALV plant parts merged`；导出和保存失败分别查 ALV 文件、SAP 状态栏 |
| `ZFI057.vbs` | `ZFI057` | 产值拆分 | ZFI057 workflow 的 step2，按 `ZFIT_RPA_BUKRS-GSBER/WERKS` 映射逐工厂传入上游物料执行拆分；跨月按发布成本月（1/3/5/7/9/11）规则拆成 1-3 个日期窗口 | 单个 `S_WERKS-LOW`、`S_MATNR` 多选、`S_KADKY-LOW/HIGH`、`S_KADAT-LOW/HIGH` | 依赖 step1 物料清单和 `ZFIT_RPA_BUKRS` 工厂结果；输出 step2 SAP 状态 | VBS 不直接读 `ZFI_SPLIT`；若提示缺物料，回查 NCo step1；若部分窗口无数据，看 `STATUS_TEXT` 中 success/noData |
| `ZCO020.vbs` | `ZCO020` | 拆分验证 | ZFI057 workflow 的 step3，按业务范围和过账日期验证拆分结果 | `S_BUDAT-LOW/HIGH`、`S_GSBER-LOW`；执行后筛选 `ZBZ1` 并保存/导出选中行 | SAP 状态栏；依赖 ZFI057 后台 job 完成状态 | 若第一次后未重复执行，查 `TBTCO` 轮询；若筛选失败，查 `ZBZ1` 列和按钮权限 |
| `ZFI080.vbs` | `ZFI080` | 实际材料保存 | 按工厂和上一完整周过账日期执行实际材料明细/保存 | `S_BUDAT-LOW/HIGH`、`S_WERKS-LOW` | 选择 ALV 列：`GSBER,BUDAT,ZMON,WEEK,AUFNR,SMATNR,MATNR1,WERKS,MBLNR,LGORT,BWART,MATNR,MENGE,VERPR,ZBFJE,VERPR2,ZBFJE2,ZWLLB` | 列缺失时查 ALV 布局或权限；日期默认上一完整周 |
| `ZFI080B.vbs` | `ZFI080B` | 工费率保存 | 按工厂和过账日期范围保存工费率 | `S_WERKS-LOW`、`S_BUDAT-LOW/HIGH` | SAP 状态栏/`STATUS_TEXT` | 多工厂只取第一个，父任务应拆批；日期错误查 `period/weekEnd` |
| `ZCO019.vbs` | `ZCO019` | 标准材料成本 | 按工厂和上一完整周过账日期执行标准材料成本相关保存 | `S_BUDAT-LOW/HIGH`、`S_WERKS-LOW`、`P_RADIO2` | SAP 状态栏/`STATUS_TEXT` | 如果结果口径异常，确认 radio 选项和日期窗口 |
| `ZFI019NA.vbs` | `ZFI019NA` | 周损益生成 | 按业务范围和上一完整周过账日期生成/保存周损益 | `S_BUDAT-LOW/HIGH`、`S_GSBER-LOW` | SAP 状态栏/`STATUS_TEXT` | 多业务范围由父任务拆批；先查业务范围、日期、SAP 状态栏 |
| `ZFIR034.vbs` | `ZFIR034` | 维护特殊价格（ZFI085） | 按日期范围和周次执行报表/维护流程 | `P_GJAHR`、`P_WEEK`、`S_BUDAT-LOW/HIGH` | SAP 状态栏/`STATUS_TEXT` | 不使用工厂或业务范围；若年周不对，查系统日期和 `period/weekEnd` |
| `ZFI019NL.vbs` | `ZFI019NL` | 周损益保存导出 | 按业务范围和过账日期提取周损益 ALV 并执行原保存动作 | `S_BUDAT-LOW/HIGH`、`S_GSBER-LOW`；可用 `ALV_EXPORT_DIR/FILENAME` | 先导出业务范围原始 ALV，后端再按 Excel 中 `WERKS`/`Plant Code`/`工厂`/`工厂号`/`工厂代码`/`大BU-工厂`/`业务范围-小厂` 等工厂字段拆分为 `<alvExportDataDirectory>\yyyy_WKnn_工厂` 下的工厂级 Excel；只有表头或无工厂数据行时删除 raw 文件、业务范围子目录和空的 `_raw_business_area` 根目录，不登记无用文件；有业务行但缺工厂列或工厂值为空时失败并保留 raw；查询阶段 SAP 状态栏明确提示“没有符合条件数据”时，VBS 输出 `STATUS_TYPE=W` 和成功标记，不输出 `ERROR:`；`MATERIAL_COUNT`、`MATERIALS_CSV`、SAP 状态栏；特殊业务范围提示需后端读 `ZFI_SPLIT` | 若物料为空，查 ALV 列 `MATNR/MATNR1/RMATNR/IMATNR`；若有数据却拆分失败，先确认导出 Excel 是否有工厂列及列值，不允许退回 `ZTSD001`、SQLite、本地配置或业务范围入参推断；特殊范围补料要回查 NCo `ZFI_SPLIT` |
| `ZFI148.vbs` | `ZFI148` | 推送大数据平台 | 按工厂、年度、周次推送大数据平台 | `S_WERKS-LOW`、`P_GJAHR`、`P_WEEK` | SAP 状态栏/`STATUS_TEXT` | 脚本不直接填 `S_BUDAT`，日期只用于推导年周；失败时查周次和工厂 |
| `sap_alv_export_helper.vbs` | 非事务码 | 共享 ALV 导出 helper | 被事务 VBS 载入，封装 ALV 导出入口、路径、文件等待、Excel/WMI 辅助处理 | SAP session、导出目录、文件名、timeout | 导出文件 ready 标记；确认本次完整路径对应的 Excel 工作簿已关闭后才输出 `OUTPUT_FILE`；可能触发 Excel COM/WMI | 若 ALV 导出失败，先查 helper 是否存在、导出窗口、文件占用、Excel 进程 |

## 4. 配置文件中的事务码映射

`transaction-config.json` 当前配置了以下事务。`automation=script` 表示走 VBS 自动化；`automation=openOnly` 表示仅打开事务或当前目录没有对应脚本时不应按已实现 VBS 处理。

| 事务码 | 脚本 | automation | 参数 | 当前脚本文件状态 | 排查建议 |
| --- | --- | --- | --- | --- | --- |
| `ZFI072A` | `ZFI072A.vbs` | `script` | `year, week, plants` | 存在 | 查年度、周次、工厂多选、ALV 导出 |
| `ZFI085` | `ZFI085.vbs` | `openOnly`，且 `enabled=false` | `year, week, plants` | 本次扫描目录未见对应 VBS | 按禁用/openOnly 处理，不作为当前直接 VBS 调用 |
| `ZFI014D` | `ZFI014D.vbs` | `openOnly` | `year, week, plants` | 本次扫描目录未见对应 VBS | 若页面仍展示，排查配置与运行目录同步 |
| `ZFI072N` | `ZFI072N.vbs` | `script` | `plants, period, weekEnd` | 存在 | 查过账日期窗口拆分 |
| `ZFI057` | `ZFI057.vbs` | `script` | `plants, businessAreas, period, weekEnd` | 存在 | 实际入口是自动工作流：`ZFI019NL` memory -> `ZFI057` -> `ZCO020` |
| `ZCO020` | `ZCO020.vbs` | `script` | `businessAreas, period, weekEnd` | 存在 | 通常由 ZFI057 workflow 调起 |
| `ZPP063` | `ZPP063.vbs` | `openOnly` | `year, week, plants` | 本次扫描目录未见对应 VBS | 不作为当前已实现 VBS 自动化 |
| `ZPP063X` | `ZPP063X.vbs` | `openOnly` | `year, week, plants` | 本次扫描目录未见对应 VBS | 不作为当前已实现 VBS 自动化 |
| `ZFI019NC` | `ZFI019NC.vbs` | `openOnly` | `year, week, plants` | 本次扫描目录未见对应 VBS | 不作为当前已实现 VBS 自动化 |
| `ZFI080` | `ZFI080.vbs` | `script` | `plants` | 存在 | 查 ALV 字段选择和保存结果 |
| `ZCO019` | `ZCO019.vbs` | `script` | `plants` | 存在 | 查 `P_RADIO2` 选项和日期 |
| `ZFI019NA` | `ZFI019NA.vbs` | `script` | `businessAreas` | 存在 | 查业务范围与上一完整周日期 |
| `ZFIR034` | `ZFIR034.vbs` | `script` | `period, weekEnd` | 存在 | 查 `P_WEEK` 与日期范围 |
| `ZFI019NL` | `ZFI019NL.vbs` | `script` | `businessAreas` | 存在 | 查业务范围、ALV 物料列、后端 `ZFI_SPLIT` 补数 |
| `ZFI080B` | `ZFI080B.vbs` | `script` | `plants, period, weekEnd` | 存在 | 查单工厂拆批和日期范围 |
| `ZFI148` | `ZFI148.vbs` | `script` | `plants, period, weekEnd` | 存在 | 查工厂、年度、周次 |

## 5. ZFI057 自动工作流排查链

ZFI057 是当前最复杂的跨层调用链：

1. 用户/API 点击 `ZFI057`。
2. `Program.cs` 不直接只跑 `ZFI057.vbs`，而是进入 `ExecuteZfi057Workflow`。
3. Step 1：NCo 调用 `ZFI_SAP_API_GATEWAY`，`IV_ACTION=REPORT_SUBMIT`，提交报表 `ZFI019NL`，通过 memory/export 读取物料。
4. Step 1 补充：特殊业务范围时，NCo 通过 `RFC_READ_TABLE` 读取 `ZFI_SPLIT` 补充 `MATNR`。
5. Step 2：NCo 调用 `RFC_READ_TABLE`，查询 `ZFIT_RPA_BUKRS` 的 `GSBER=业务范围` 并取全部 `WERKS` 工厂。
6. Step 2：launcher 把物料清单外置给 `ZFI057.vbs`，每个工厂各调用一次，VBS 在 SAP GUI 中填单个 `S_WERKS-LOW`、`S_MATNR`、`S_KADKY`、`S_KADAT` 并执行。
7. Step 3：运行 `ZCO020.vbs` 做拆分验证。
8. Step 3 中间检查：NCo 通过 `RFC_READ_TABLE` 读 `TBTCO`，确认 `JOBNAME=ZFI057` 的后台 job 到终止状态。
9. Step 3：终止后再运行一次 `ZCO020.vbs` 做 scope closure。

关键排查顺序：

| 症状 | 优先检查 |
| --- | --- |
| ZFI057 一开始失败 | NCo 配置完整性；`ZFI_SAP_API_GATEWAY REPORT_SUBMIT` 是否可用；`S_BUDAT/S_GSBER` 是否有数据 |
| 有 ZFI019NL 数据但 ZFI057 无工厂 | `ZFIT_RPA_BUKRS` 中该 `GSBER` 的 `WERKS` 维护，以及该表/字段的 RFC 读取权限 |
| ZFI057 提示没有物料 | Step1 的 `finalRows`、`MATERIALS` 外置文件、特殊范围 `ZFI_SPLIT` 读取 |
| 第一次 ZCO020 后不继续 | `TBTCO` 是否能读；job 用户是否匹配；状态是否仍在 `R/Y/S/P` |
| 特殊业务范围物料缺少 | `ZFI_SPLIT` 的 `BUKRS/WERKS/BEGDA/ENDDA` 条件和表权限 |

## 6. 安全与日志注意事项

- 不要把 `config.local.json`、真实 SAP 用户、密码、router、服务器地址、钉钉密钥或 webhook 粘贴到 issue、文档或聊天中。
- C# 日志里 `SafeSummary()` 会脱敏用户并只提示 router 是否 configured；排查时优先引用这类脱敏摘要。
- VBS stdout 可能包含工厂、业务范围、日期、物料号、导出路径；业务数据可按最小必要范围截取。
- 若要给 ABAP 或 Basis 排查，建议只提供：事务码/function/table 名、脱敏后的入参字段名、`EV_SUBRC/EV_MSG`、SAP 状态栏文本、失败时间窗口。
