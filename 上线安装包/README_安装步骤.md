# SAP RPA V2 上线安装包说明

本目录不再提供“一键安装器”。现在的交付方式是：

1. 手工安装指南和上线核对清单。
2. 必要脚本：生成上线包、配置 SAP 登录信息。
3. 发布产物：`SapWebLauncher`、前端页面、VBS 事务脚本、本机配置模板。

## 2026-08-06 ALV 组织归档上线口径

保存类 ALV 的 SAP GUI 导出先落本机暂存，再由后端归档到 `fileStorage.alvExportDataDirectory`。不得把 UNC 网络路径直接交给 SAP GUI。后端使用只读 SAP 查询：业务范围型 `ZFI080`、`ZFI080B`、`ZFI019NL`、`ZFI019NA`、`ZFI148` 按导出 Excel 的 `GSBER` 查询 `ZTFI48A`；其余保存类按 `WERKS` 查询 `ZTFI48B`。命中后路径为 `<根目录>\ZBU\ZSBU\yyyy_WKnn\事务码_卡片名称_WKnn.xlsx`，同组织的工厂合并，一厂多组织各生成一份。只有查询成功但无映射时才使用 `<根目录>\集采工厂\yyyy_WKnn` 回退目录；映射失败或 Excel 缺必需列时必须失败、保留暂存并触发钉钉失败通知。发布后必须执行真实导出验证网络盘路径、文件内容和重跑覆盖，不能只看 API health。

权威安装步骤请先读：

```text
上线安装文档清单.md
生产部署拷贝清单.md
最终上线部署步骤\README_V2_Windows_Server_上线部署.md
```

## 保留文件

| 文件 | 用途 |
| --- | --- |
| `上线安装文档清单.md` | 上线前、上线中、上线后的人工核对清单。 |
| `生产部署拷贝清单.md` | 生产机整包拷贝、状态保留、证书 `D:\RPA\certs` 处理规则。 |
| `最终上线部署步骤\README_V2_Windows_Server_上线部署.md` | Windows Server 手工部署 runbook。 |
| `config.local.example.json` | 本机配置模板，覆盖钉钉 OpenAPI、SAP NCo 和 ZFI057/ZFI019NL memory fetch，只能作为字段说明。 |
| `00_生成上线安装包.cmd` | 在开发/打包机生成发布包。 |
| `scripts\make_package.ps1` | 发布 `SapWebLauncher` 并组装上线包。 |
| `04_配置SAP登录信息.bat` | 在目标 Windows 执行账号下生成 SAP 登录配置。 |
| `scripts\configure_sap_login.ps1` | `04_配置SAP登录信息.bat` 调用的脚本。 |

已删除旧的一键安装、健康检查、打开页面和卸载入口。不要再按旧文档寻找这些自动安装/检测/打开页面脚本；部署、启动和验收均以手工清单为准。

## 文档同步规则（fs-skill）

本项目把 `SapRpa_V2_功能说明书.html` 当作后续 agent 和人工运维的外置记忆。凡是改动前端页面、后端 API、VBS、SQLite、钉钉配置、SAP 登录复用、网关、启动脚本、安装包或上线步骤，必须同步更新功能说明书、本文档、上线安装文档清单和必要的 `反思.md` 坑点记录。

不要只改代码或脚本后口头说明；后续上线人员应能只读安装文档就知道路径、端口、配置文件、启动入口、重启范围和验收方式。

## 上线前必须确认

| 项目 | 示例 | 说明 |
| --- | --- | --- |
| 源码仓库目录 | `D:\RPA\RpaProject` | 只用于 `git pull`、`dotnet publish` 和复制发布产物。 |
| 运行根目录 | `D:\RPA` | 线上实际读取页面、API、SQLite、日志、VBS 和配置的目录。 |
| 后端执行器 | `D:\RPA\bin\SapWebLauncher.exe` | 运行中的 API 必须来自这里。 |
| SAP 登录配置 | `%LOCALAPPDATA%\SapWebLauncher\config.json` | 必须在固定 Windows 执行账号下生成。 |
| 本机真实配置 | `D:\RPA\config.local.json` | 必须手工填写钉钉、SAP NCo、ZFI057 memory fetch 等真实值，不提交 Git。 |
| 对外访问地址 | `https://fi_automation.srv.lstech.com/rpa/` | 正式用户链接；DNS、443、证书、路径前缀、防火墙和网关需要单独确认。 |
| 兼容访问地址 | `http://<服务器IP>:6174/rpa/` | 保留给内网端口访问和排障；这是 HTTP，不是 HTTPS。当前联调服务器是 `10.0.41.158`，正式系统 IP 是 `10.0.2.120`。 |
| HTTPS 证书目录 | `D:\RPA\certs\lstech.com` | 只能放服务器本机，不提交 Git；权限限制为执行账号、Administrators、SYSTEM。 |

## 本机 JSON 配置

安装包只能带模板 `config.local.example.json`。正式推送、SAP NCo 取数和 ZFI057 第一步 memory fetch 都必须在运行根目录放真实文件：

```powershell
Copy-Item "D:\RPA\config.local.example.json" "D:\RPA\config.local.json"
```

真实文件至少包含：

```json
{
  "multiLogonPolicy": "takeover",
  "fileStorage": {
    "alvExportDataDirectory": "D:\\RPA\\临时文件\\文件数据"
  },
  "dingTalkOpenApi": {
    "baseUrl": "https://你的钉钉OpenAPI网关根地址/",
    "appKey": "你的真实AppKey",
    "appSecret": "你的真实AppSecret",
    "agentId": "你的真实AgentId"
  },
  "sapNco": {
    "connectionName": "test888",
    "ipAddress": "10.0.40.212",
    "systemNumber": "10",
    "systemId": "TD1",
    "router": ""
  },
  "zfi057Workflow": {
    "gs03SetName": "Z31",
    "zfi019nlMemory": {
      "report": "ZFI019NL",
      "memoryId": "%ZFI019NA%",
      "memoryName": "GT_ALV",
      "spoolDevice": "LP01",
      "waitSeconds": 60,
      "splitTable": "ZFI_SPLIT",
      "splitBukrs": "2030"
    }
  }
}
```

`baseUrl` 只填接口根地址，不要把 `/token` 或 `asyncsend_v2` 写进去。`sapNco` 使用目标 SAP 系统的应用服务器、实例编号和系统标识；SAP 密码仍只保存在 `%LOCALAPPDATA%\SapWebLauncher\config.json`，不要写进 `config.local.json`。真实 `config.local.json`、SAP 密码、SQLite、日志和输出文件都不能提交到 GitHub。

`fileStorage.alvExportDataDirectory` 控制含“保存”事务的 ALV 导出 Excel 输出根目录；不配置时默认 `D:\RPA\临时文件\文件数据`。工厂型事务每个工厂直接按运行时服务器系统日期落到独立目录，例如 `D:\RPA\临时文件\文件数据\2026_WK31_6700\ZFI072N_维护采购价_工厂6700_20260728134553.xlsx`；周目录加工厂号已存在则复用。ZFI072N、ZCO019 等同一工厂需要执行两段结果的场景，会先导出 `_part1/_part2` 中间文件，再在该工厂目录内合并为一个最终工厂 Excel 并删除 part 文件。业务范围型保存事务先导出原始 ALV，再按 Excel 中的 `WERKS`、`Plant Code`、`工厂`、`工厂号`、`工厂代码`、`大BU-工厂`、`业务范围-小厂` 等工厂字段拆分到各自工厂目录；拆分依据只允许使用导出 Excel 的工厂列实际值。只有表头或无工厂数据行时，程序会把该业务范围视为无导出数据，删除 raw 文件、业务范围子目录和空的 `_raw_business_area` 根目录，不登记无用文件，也不把“没数据”当作技术失败；有数据但缺列或工厂值为空时任务失败并保留 raw 文件，应按 ALV 布局/导出结果排查，不得用 `ZTSD001`、SQLite `plants`、`config.local.json`、业务范围入参或本地映射推断。拆分后的 Excel 只保留 ALV 原始列，不额外加来源、子任务或源文件列。Excel 导出 helper 会在文件存在、非空且大小稳定后，继续按完整路径关闭本次工作簿，确认关闭后才输出 `OUTPUT_FILE` 并进入后续工厂或日期段。生产机如要放到网络共享盘或其他数据盘，只改真实 `D:\RPA\config.local.json` 里的这个值即可；临时覆盖也可设置环境变量 `SAP_RPA_ALV_EXPORT_DIR`。
`multiLogonPolicy` 控制 SAP 多重登录弹窗处理。默认值是 `takeover`：服务器登录同一 SAP 账号时，如果 SAP 弹出“该账号已在其他终端登录”的多重登录确认，程序会选择继续本次登录并终止该账号其他登录，让服务器任务继续执行。若生产策略不允许踢掉其他终端，把它改成 `fail`，或设置环境变量 `SAP_RPA_MULTI_LOGON_POLICY=fail`，程序会遇到多重登录弹窗直接失败并写日志。

多重登录验收不能只看日志里是否出现 `selected MULTI_LOGON_OPT1`。那只代表程序选中了“继续本次登录并终止其他登录”的单选项，还必须继续按确认按钮或发送 Enter，并等待弹窗关闭或 `session.Info.User` 变成目标用户。若日志仍停在 `SAPMSYST screen=500`、`NO: scripting engine or connection not ready`，并且没有 `加载外部事务码脚本`、`INFO: transaction=<事务码>`，说明还没有真正进入事务码。发布版本里处理该弹窗的 `cscript //T` 超时必须长于内部确认等待时间。

## SAP NCo 与 ZFI057 第一步取数

`ZFI057` 工作台入口后台执行三步：第一步通过 SAP NCo 调用 `ZFI_SAP_API_GATEWAY` 的 `REPORT_SUBMIT/MEMORY_EXPORT` 读取 `ZFI019NL` memory 物料集合，第二步调用 `ZFI_SAP_API_GATEWAY` 的 `GET_GS03`，默认传 `IV_SET_NAME=Z31`，再从返回表里筛 `TITLE=业务范围` 的行并取 `FROM` 作为全部工厂；同一 `TITLE` 返回多行时必须全部取 `FROM`，不允许只取第一条，然后把全部工厂一次性传给 `ZFI057.vbs`。VBS 先填日期和 `S_MTART-LOW=*`，再把首个工厂写入 `S_WERKS-LOW` 通过 SAP 必填校验；多工厂时继续把全部工厂粘贴到 `S_WERKS` 多选，单工厂时跳过多选。第三步运行 `ZCO020.vbs`。第一步不运行 `ZFI019NL.vbs`，也不把 `GET_GS03` 解析出的工厂作为步骤一入参；步骤二必须使用步骤一业务范围通过 `GET_GS03` 一次性取回的工厂集合，不再读取 `ZTSD001`、SQLite `plants` 或 VBS 本地硬编码映射。这个“一次性多工厂”口径只针对 `ZFI057` 工作流步骤二，不改变其他事务码的按工厂执行逻辑。`ZFI057` 生产入口必须传 `businessAreas`，只传 `plants` 会被视为无有效业务范围。

生产机必须满足：

| 项目 | 要求 |
| --- | --- |
| NCo 依赖源 | `D:\RPA\依赖\SapNco\sapnco.dll`、`sapnco_utils.dll`、`ijwhost.dll`、`cpc4n.dll` |
| 运行目录 DLL | `D:\RPA\bin\` 必须包含上述四个 DLL，以及 `System.Configuration.ConfigurationManager.dll`、`System.Security.Permissions.dll`；后端升级必须完整复制 publish 输出到 `D:\RPA\bin\`，不能只替换 `SapWebLauncher.exe`。即使当前采购价链路不再合并总 Excel，仍需完整复制 `ClosedXML*.dll`、`DocumentFormat.OpenXml*.dll`、`ExcelNumberFormat.dll`、`RBush.dll`、`SixLabors.Fonts.dll` 等 publish 依赖，避免其他诊断/后续报表处理功能缺 DLL |
| 本机配置 | `D:\RPA\config.local.json` 必须包含 `sapNco`、`zfi057Workflow.gs03SetName` 和 `zfi057Workflow.zfi019nlMemory`；`gs03SetName` 默认 `Z31` |
| SAP 登录配置 | `%LOCALAPPDATA%\SapWebLauncher\config.json` 仍由 `04_配置SAP登录信息.bat` 在固定 Windows 执行账号下生成 |
| SAP 网关对象 | `ZFI_SAP_API_GATEWAY` 必须支持 `GET_GS03`；调用时传 `IV_SET_NAME=Z31`，返回表按 `TITLE=业务范围` 过滤，同一 `TITLE` 多行时取全部 `FROM` 为工厂；上线前必须跑下面的 `--test-zfi057-get-gs03` |

单独验收业务范围到工厂映射：
```powershell
& "D:\RPA\bin\SapWebLauncher.exe" --test-zfi057-get-gs03 --businessArea 2800 --setName Z31
```

成功标准：输出包含 `status=success`、`setName=Z31`，`plantCount` 大于 0，`plants` 包含 `GET_GS03` 返回表中 `TITLE=2800` 对应的全部 `FROM` 工厂。如果 `plantCount=0`，先检查返回表是否真的有 `TITLE=2800`；不能退回读取 `ZTSD001`，也不能用本地工厂表或旧 VBS 单工厂映射兜底。`S_WERKS-LOW` 只允许使用 GET_GS03 返回列表的第一个工厂作为必填校验种子值。

单独验收 ZFI019NL memory 物料集合：

```powershell
$out = "D:\RPA\logs\zfi019nl-memory-test.out.log"
$err = "D:\RPA\logs\zfi019nl-memory-test.err.log"
Start-Process -FilePath "D:\RPA\bin\SapWebLauncher.exe" `
  -ArgumentList "--test-zfi019nl-memory --businessArea 2800 --period 2026.04.27 --weekEnd 2026.05.03" `
  -WorkingDirectory "D:\RPA" -Wait -PassThru `
  -RedirectStandardOutput $out -RedirectStandardError $err
Get-Content $out
```

成功标准：输出包含 `status=success`、`method=MEMORY_EXPORT`、`S_BUDAT=I:BT:20260427:20260503`、`S_GSBER=I:EQ:2800:`、`materialCount` 大于 0，并生成 `D:\RPA\outputs\zfi057\DIAG-ZFI019NL-*_scope1_2800_materials.csv`。

## Git 更新后的发布要求

`git pull` 只更新源码，不会更新线上运行目录。凡是改了后端、API、队列、通知、数据库迁移、SAP 登录复用、VBS 参数替换或事务脚本，必须重新发布并复制到运行目录。

生产机必须复制的文件清单：

| 类别 | 复制到 `D:\RPA` 的位置 |
| --- | --- |
| 前端 | `index.html`、`assets\js\*.js` |
| 网关 | `gateway\rpa-gateway.js`、`gateway\start-rpa-gateway.ps1` |
| 启动脚本 | `启动脚本\00_register_sap_gui_components.cmd`、`00_register_sap_gui_components.ps1`、`start_sap_rpa_services.cmd`、`check_sap_rpa_services.cmd`、`01_start_sap_rpa_services.ps1`、`02_check_sap_rpa_services.ps1`、`gitnexus.cmd`、`gitnexus.ps1` |
| NCo 依赖源 | `依赖\SapNco\sapnco.dll`、`sapnco_utils.dll`、`ijwhost.dll`、`cpc4n.dll` |
| 后端 | `dotnet publish` 后的全部输出复制到 `D:\RPA\bin\`，不能只替换 `SapWebLauncher.exe` 或单个 dll；否则 NCo、Excel 文件处理或后续报表处理依赖可能缺失 |
| 事务脚本 | `网页启动登录\transactions\*.vbs` 和 `transaction-config.json` 复制到 `D:\RPA\transactions\` |
| 配置模板 | `上线安装包\config.local.example.json` 复制到 `D:\RPA\config.local.example.json` |

生产机本地生成或保留的内容不要从开发机覆盖：`D:\RPA\config.local.json`、`D:\RPA\data\sap-rpa-config.db`、`D:\RPA\logs\`、`D:\RPA\outputs\`、`D:\RPA\certs\lstech.com\`、`%LOCALAPPDATA%\SapWebLauncher\config.json`。详细拷贝边界和证书处理规则见 `生产部署拷贝清单.md`。

可以把当前 `D:\RPA` 整包拷贝到生产机，但只把它当作程序包。全新生产机拷贝后必须重新填写真实 `config.local.json`、重新生成 SAP 登录配置、重新放置生产证书；升级已有生产机时必须先备份并保留生产机自己的 `config.local.json`、SQLite 数据库和证书目录，不要被测试机文件覆盖。`D:\RPA\certs` 可以作为受控生产证书备份/迁移材料复制，但不能跟普通程序包、GitHub 或公开 zip 包一起流转。

```powershell
Set-Location "D:\RPA\RpaProject"
git pull

dotnet publish "D:\RPA\RpaProject\网页启动登录\SapWebLauncher\SapWebLauncher.csproj" -c Release -o "D:\RPA\RpaProject\publish\SapWebLauncher"

Copy-Item "D:\RPA\RpaProject\publish\SapWebLauncher\*" "D:\RPA\bin" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\index.html" "D:\RPA\index.html" -Force
Copy-Item "D:\RPA\RpaProject\assets" "D:\RPA\assets" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\gateway" "D:\RPA\gateway" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\启动脚本" "D:\RPA\启动脚本" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\*.vbs" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\transaction-config.json" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\上线安装包\config.local.example.json" "D:\RPA\config.local.example.json" -Force
```

首次部署或后端版本包含 SQLite 结构/默认配置变更时，复制文件后先执行迁移。升级已有生产机时不要删除数据库；`--init-db` 会在保留历史数据的前提下补齐表和默认配置：

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" --init-db
```

只重启本项目后端：

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -eq "SapWebLauncher.exe" -and $_.CommandLine -like "*D:\RPA\bin\SapWebLauncher.exe*--serve*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Start-Process -FilePath "D:\RPA\bin\SapWebLauncher.exe" -ArgumentList "--serve" -WorkingDirectory "D:\RPA"
```

只重启本项目网关：
```powershell
powershell -ExecutionPolicy Bypass -File "D:\RPA\gateway\start-rpa-gateway.ps1"
```

这个脚本只匹配 `D:\RPA\gateway\rpa-gateway.js`，会启动 `http://0.0.0.0:6174/rpa/` 和 `https://0.0.0.0:443/rpa/`。不要停止其他项目的 `node.exe`，不要占用别人已有的 80、6173 等端口。HTTP 兼容入口的展示 IP 必须取目标服务器本机 IPv4；可用 `ipconfig` 或 `Get-NetIPAddress -AddressFamily IPv4` 查看。当前联调服务器是 `10.0.41.158`，正式系统是 `10.0.2.120`。

注意：`D:\RPA\启动脚本` 是线上运行入口；`D:\RPA\RpaProject\启动脚本` 是 Git 源码副本。两处脚本默认 `RuntimeRoot = D:\RPA`，从源码目录双击也会操作线上运行目录，不是独立测试环境。

服务器重启后直接运行：
```text
D:\RPA\启动脚本\start_sap_rpa_services.cmd
```

启动后检查：
```text
D:\RPA\启动脚本\check_sap_rpa_services.cmd
```

这两个 `.cmd` 默认会自动识别本机 IPv4 作为 HTTP 兼容入口。如果生产机有多块网卡或需要强制指定正式系统 IP，使用：

```text
D:\RPA\启动脚本\start_sap_rpa_services.cmd -HttpCompatibilityHost 10.0.2.120
D:\RPA\启动脚本\check_sap_rpa_services.cmd -HttpCompatibilityHost 10.0.2.120
```

也可以在运行前设置环境变量 `RPA_HTTP_COMPATIBILITY_HOST=10.0.2.120`。

GitNexus 影响分析入口：
```text
D:\RPA\启动脚本\gitnexus.cmd status
D:\RPA\启动脚本\gitnexus.cmd detect-changes
```

如果旧 PowerShell、Codex 或任务会话识别不到 `gitnexus`，通常是进程 PATH 没刷新，不要重复安装。上面的脚本会自动切到 `D:\RPA\RpaProject` 并优先调用 `C:\Users\Marcus\AppData\Roaming\npm\gitnexus.cmd`，必要时兜底调用项目内 `.gitnexus\run.cjs`。

必须看到三类本项目服务：

| 服务 | 进程/端口 | 说明 |
| --- | --- | --- |
| 本地 API | `D:\RPA\bin\SapWebLauncher.exe --serve`，`127.0.0.1:8080` | 负责队列、SQLite、SAP GUI/VBS 执行和钉钉发送。 |
| HTTP 兼容网关 | `node.exe D:\RPA\gateway\rpa-gateway.js`，`0.0.0.0:6174` | 提供 `http://<服务器IP>:6174/rpa/`；联调示例 `http://10.0.41.158:6174/rpa/`，正式系统示例 `http://10.0.2.120:6174/rpa/`。 |
| HTTPS 正式网关 | `node.exe D:\RPA\gateway\rpa-gateway.js`，`0.0.0.0:443` | 提供 `https://fi_automation.srv.lstech.com/rpa/`。 |

## SAP 登录配置

在目标服务器上，用固定 Windows 执行账号运行：

```text
04_配置SAP登录信息.bat
```

生成位置：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

密码用当前 Windows 用户 DPAPI 加密。这个文件不能复制到其他 Windows 用户或其他电脑；换机器、换账号或重新部署时必须重新运行配置脚本。

默认登录策略会在执行前尽量清理服务器本机残留的 SAP 登录窗口；真正“踢掉其他电脑登录”发生在 SAP 返回多重登录确认窗口后，由 `multiLogonPolicy=takeover` 选择“继续本次登录并终止其他登录”。这不会直接杀本机所有 `saplogon.exe` 进程，也不会处理非目标 SAP 账号的手工会话。

如果手工 SAP GUI 登录正常，但网页执行仍反复登录、没有复用已登录会话，管理员 PowerShell 里注册 SAP GUI 脚本组件：

推荐直接运行：

```text
D:\RPA\启动脚本\00_register_sap_gui_components.cmd
```

只检查注册状态：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\RPA\启动脚本\00_register_sap_gui_components.ps1" -CheckOnly
```

预期看到 `SapROTWr.SapROTWrapper: True` 和 `Sapgui.ScriptingCtrl.1: True`。

脚本会读取 `saprotwr.dll` 和 `sapfewse.ocx` 的 PE machine 类型自动选择 `regsvr32.exe`：32 位组件使用 `C:\Windows\SysWOW64\regsvr32.exe`，64 位组件通常使用 `C:\Windows\System32\regsvr32.exe`；如果脚本从 32 位 PowerShell 进程里运行 64 位组件注册，则使用 `C:\Windows\Sysnative\regsvr32.exe` 避免文件系统重定向。`-CheckOnly` 输出里的 `Registration bitness plan` 会显示实际选择。

手工兜底命令：

```powershell
Set-Location "C:\Program Files (x86)\SAP\FrontEnd\SAPgui"
C:\Windows\SysWOW64\regsvr32.exe saprotwr.dll
C:\Windows\SysWOW64\regsvr32.exe sapfewse.ocx
```

手工注册时必须按组件位数选择：32 位 SAP GUI 组件用 `C:\Windows\SysWOW64\regsvr32.exe`，64 位 SAP GUI 组件通常用 `C:\Windows\System32\regsvr32.exe`；如果当前 shell 是 32 位进程，改用 `C:\Windows\Sysnative\regsvr32.exe`。

## 验收边界

健康检查只能说明进程可达，不能证明上线完成。最终验收必须覆盖：

1. 页面 `https://fi_automation.srv.lstech.com/rpa/` 能打开。
2. `https://fi_automation.srv.lstech.com/rpa/api/health` 返回正常。
3. 兼容入口 `http://<服务器IP>:6174/rpa/` 能打开；正式系统按 `http://10.0.2.120:6174/rpa/` 验收，当前联调服务器按 `http://10.0.41.158:6174/rpa/` 验收。
4. `D:\RPA\启动脚本\check_sap_rpa_services.cmd` 输出四个 URL 检查均为 `200 OK`。脚本对 HTTPS 使用 `curl.exe --ssl-no-revoke`，只跳过内网 CRL/OCSP 吊销查询，不跳过证书链和域名校验。
5. 浏览器从正式域名打开时，API 请求走 `https://fi_automation.srv.lstech.com/rpa/api/*`，不是旧的 `127.0.0.1`、旧 IP 或旧 `/charge`。
6. 证书域名匹配 `fi_automation.srv.lstech.com`，有效期未过期，浏览器无证书告警。
7. `D:\RPA\certs\lstech.com\*.key`、`*.pfx`、`passwd.txt` 仅执行账号、Administrators、SYSTEM 可读。
8. `http://127.0.0.1:8080/api/health` 返回的 `runtimeRoot` 是运行根目录。
9. 网页提交一次受控任务，SQLite 有 `runs` 和日志记录。
10. VBS 从运行目录 `transactions` 读取的是最新脚本。
11. SAP GUI 已登录复用时，日志出现 `Detected ready SAP GUI session; skip sapshcut login`。
12. 钉钉启用时，日志出现 `sap dingtalk openapi sent: userid=...`。
13. 含“保存”的 ALV 导出任务完成后，后端根据导出 Excel 的实际 `WERKS` 或 `GSBER` 读取 SAP 映射，将最终 Excel 写入 `fileStorage.alvExportDataDirectory` 下的 `ZBU\ZSBU\yyyy_WKnn`；没有有效映射时写入 `集采工厂\yyyy_WKnn`。同一组织、周和事务码的工厂数据合并为同一个 Excel，一厂多组织各写一份；父 run 只登记这些最终文件。SAP GUI 始终只写本机 `D:\RPA\临时文件\ALV本地暂存`，后端确认归档成功才清理暂存；映射或网络写入失败时保留暂存并发钉钉失败通知。验收必须检查组织/周目录、Excel 内容和重跑不重复来源行。

当前前台“保存类 ALV 导出”只承诺 7 个事务码：`ZFI072A`、`ZFI072N`、`ZFI080`、`ZFI080B`、`ZCO019`、`ZFI019NA`、`ZFI019NL`。`ZFI148`、`ZFIR034`、`ZFI057`、`ZCO020` 不按普通保存类 ALV Excel 归档；旧 `ZFI019NI` 没有生产 VBS 和工作台入口，不作为上线保存卡片。

## 最终包真实验收

生成或更新上线包后，不要只检查 zip/文件是否存在。提交或发给生产机前，至少从最终交付物路径跑一遍：

1. 从 `D:\RPA\RpaProject\上线安装包` 或最终解压目录开始检查，不只看 `D:\RPA\RpaProject` 源码。
2. 记录包路径、生成时间、`PACKAGE_VERSION.txt` 或当前 git commit。
3. 用真实目标路径 `D:\RPA` 执行一次覆盖升级流程，确认生产专属 `config.local.json`、SQLite、日志、输出和证书目录没有被覆盖。
4. 启动后确认进程留存、`runtimeRoot=D:\RPA`、四个 URL 检查正常。
5. 真实提交一次受控事务码，确认数据库有 run/log，含“保存”事务按 `fileStorage.alvExportDataDirectory` 落盘。
6. 失败时收集 `D:\RPA\logs` 里对应 stderr/log 尾部，不要只记录 health failed。
