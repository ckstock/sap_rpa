# SAP RPA V2 Windows Server 手工上线部署

本文档是 Windows Server 上线 runbook。当前安装包不再提供一键安装器；上线由管理员按清单手工执行、核对和验收。

## 1. 前置条件

1. 使用固定 Windows 执行账号登录服务器交互式桌面。
2. SAP GUI 已安装，并启用 SAP GUI Scripting。
3. 手工登录 SAP 成功，目标事务码权限可用。
4. 目标服务器已有 Git、.NET 8 SDK 或可用的打包产物。
5. 已确认运行目录、端口、对外路径、防火墙和访问入口。
6. SAP GUI 自动化不要跑在纯 Session 0 Windows Service 中。

## 2. 必填参数

| 参数 | 当前服务器示例 | 说明 |
| --- | --- | --- |
| 源码仓库目录 | `D:\RPA\RpaProject` | GitHub 拉取位置，只用于源码和构建。 |
| 运行根目录 | `D:\RPA` | 线上实际运行目录。 |
| 后端 exe | `D:\RPA\bin\SapWebLauncher.exe` | API 只能以运行目录下的 exe 为准。 |
| 本机 API | `http://127.0.0.1:8080` | SapWebLauncher 默认只监听本机。 |
| 正式对外 URL | `https://fi_automation.srv.lstech.com/rpa/` | 给用户访问的正式链接；DNS、443、证书、路径前缀和防火墙必须确认。 |
| 兼容访问 URL | `http://<服务器IP>:6174/rpa/` | 仅用于内网 HTTP 兼容访问和排障，不是 HTTPS。当前联调服务器是 `10.0.41.158`，正式系统 IP 是 `10.0.2.120`。 |
| HTTPS 证书目录 | `D:\RPA\certs\lstech.com` | 全新生产机放置生产证书；升级已有生产机保留现有证书；证书不进 Git 或普通安装包。 |
| SAP 登录配置 | `%LOCALAPPDATA%\SapWebLauncher\config.json` | 在固定 Windows 执行账号下生成。 |
| 本机真实配置 | `D:\RPA\config.local.json` | 包含钉钉、SAP NCo、ZFI057 memory fetch 等真实配置，只保存在服务器本机，不提交 Git。 |
| 本机配置模板 | `D:\RPA\config.local.example.json` | 只能作为字段说明。 |

## 3. 运行目录结构

运行目录至少包含：

```text
D:\RPA\
  index.html
  assets\js\*.js
  gateway\rpa-gateway.js
  gateway\start-rpa-gateway.ps1
  启动脚本\00_register_sap_gui_components.cmd
  启动脚本\00_register_sap_gui_components.ps1
  启动脚本\start_sap_rpa_services.cmd
  启动脚本\check_sap_rpa_services.cmd
  bin\SapWebLauncher.exe
  bin\sapnco.dll
  bin\sapnco_utils.dll
  bin\ijwhost.dll
  bin\cpc4n.dll
  bin\System.Configuration.ConfigurationManager.dll
  依赖\SapNco\
  transactions\*.vbs
  transactions\transaction-config.json
  data\sap-rpa-config.db
  logs\
  outputs\
  certs\lstech.com\
  config.local.example.json
  config.local.json
```

不要提交或覆盖这些服务器本机状态：

```text
D:\RPA\data\sap-rpa-config.db
D:\RPA\logs\
D:\RPA\outputs\
D:\RPA\certs\lstech.com\
D:\RPA\config.local.json
%LOCALAPPDATA%\SapWebLauncher\config.json
```

可以把当前 `D:\RPA` 整包拷贝到生产机，但要把它当作程序包，而不是直接把测试机状态搬成生产状态。详细规则见 `..\生产部署拷贝清单.md`：

1. 可以直接带走 `index.html`、`assets`、`gateway`、`启动脚本`、`bin`、`transactions`、`config.local.example.json`。
2. 全新生产机拷贝后必须重新填写真实 `D:\RPA\config.local.json`、重新生成 `%LOCALAPPDATA%\SapWebLauncher\config.json`、重新放置生产证书。
3. 升级已有生产机时，先备份并保留生产机自己的 `config.local.json`、`data\sap-rpa-config.db` 和 `certs\lstech.com`，不要被测试机文件覆盖。
4. `D:\RPA\certs` 可以作为受控生产证书备份/迁移材料复制，但不能跟普通程序包、GitHub 或公开 zip 包一起流转；复制后必须重设 ACL。

## 4. 拉取源码并发布

首次部署：

```powershell
git clone https://github.com/ckstock/sap_rpa.git "D:\RPA\RpaProject"
Set-Location "D:\RPA\RpaProject"
git checkout codex/v2-local-api-sqlite
git pull
```

已有仓库时：

```powershell
Set-Location "D:\RPA\RpaProject"
git pull
```

发布后端：

```powershell
dotnet publish "D:\RPA\RpaProject\网页启动登录\SapWebLauncher\SapWebLauncher.csproj" -c Release -o "D:\RPA\RpaProject\publish\SapWebLauncher"
```

同步到运行目录：

```powershell
New-Item -ItemType Directory -Force -Path "D:\RPA\bin","D:\RPA\data","D:\RPA\transactions","D:\RPA\logs","D:\RPA\outputs" | Out-Null

Copy-Item "D:\RPA\RpaProject\publish\SapWebLauncher\*" "D:\RPA\bin" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\index.html" "D:\RPA\index.html" -Force
Copy-Item "D:\RPA\RpaProject\assets" "D:\RPA\assets" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\gateway" "D:\RPA\gateway" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\启动脚本" "D:\RPA\启动脚本" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\*.vbs" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\transaction-config.json" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\上线安装包\config.local.example.json" "D:\RPA\config.local.example.json" -Force
```

关键边界：

1. 只 `git pull` 不会让线上生效。
2. 后端改动必须 `dotnet publish` 并复制到 `D:\RPA\bin`。
3. 前端改动必须复制 `index.html` 和整个 `assets`。
4. VBS 改动必须复制到 `D:\RPA\transactions`。
5. 运行中的服务读取 `D:\RPA`，不是源码目录。

## 5. 配置 SAP 登录

在目标服务器固定 Windows 执行账号下运行：

```text
D:\RPA\RpaProject\上线安装包\04_配置SAP登录信息.bat
```

按提示输入 `system/client/user/password/language/sysnr`。生成文件：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

密码由当前 Windows 用户 DPAPI 加密。不要把这个文件复制到其他用户或其他电脑。

如果手工登录 SAP 正常，但程序仍检测不到已登录 GUI、反复打开登录窗口，优先运行生产机注册脚本：

```text
D:\RPA\启动脚本\00_register_sap_gui_components.cmd
```

这个入口会弹出 UAC，需要管理员确认。注册后只检查状态：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\RPA\启动脚本\00_register_sap_gui_components.ps1" -CheckOnly
```

预期看到：

```text
SapROTWr.SapROTWrapper: True
Sapgui.ScriptingCtrl.1: True
SAP GUI scripting COM registration is ready.
```

脚本会读取 `saprotwr.dll` 和 `sapfewse.ocx` 的 PE machine 类型自动选择 `regsvr32.exe`：32 位组件使用 `C:\Windows\SysWOW64\regsvr32.exe`，64 位组件通常使用 `C:\Windows\System32\regsvr32.exe`；如果脚本从 32 位 PowerShell 进程里运行 64 位组件注册，则使用 `C:\Windows\Sysnative\regsvr32.exe` 避免文件系统重定向。`-CheckOnly` 输出里的 `Registration bitness plan` 会显示实际选择。

手工兜底命令：

```powershell
Set-Location "C:\Program Files (x86)\SAP\FrontEnd\SAPgui"
C:\Windows\SysWOW64\regsvr32.exe saprotwr.dll
C:\Windows\SysWOW64\regsvr32.exe sapfewse.ocx
```

手工注册时必须按组件位数选择：32 位 SAP GUI 组件用 `C:\Windows\SysWOW64\regsvr32.exe`，64 位 SAP GUI 组件通常用 `C:\Windows\System32\regsvr32.exe`；如果当前 shell 是 32 位进程，改用 `C:\Windows\Sysnative\regsvr32.exe`。

## 6. 配置钉钉 OpenAPI

从模板复制真实配置：

```powershell
Copy-Item "D:\RPA\config.local.example.json" "D:\RPA\config.local.json"
```

填写：

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

`fileStorage.alvExportDataDirectory` 是含“保存”事务 ALV Excel 的输出根目录。生产机迁移到网络共享盘或其他数据盘时，只改真实 `D:\RPA\config.local.json` 的这个值，或临时设置环境变量 `SAP_RPA_ALV_EXPORT_DIR`；不要改 VBS 或 C# 代码。改完后只重启本项目 `SapWebLauncher.exe --serve`，再通过 health 的 `alvExportDataRoot` 和一次保存类任务落盘结果确认。

`baseUrl` 只填接口根地址。程序会自动访问：

```text
{baseUrl}/token
{baseUrl}/dingtalk-oa/topapi/message/corpconversation/asyncsend_v2?token=...
```

只有 `config.local.example.json` 或只勾选固定通知人都不会推送钉钉。缺配置时日志应出现：

```text
sap dingtalk openapi skipped: missing baseUrl/appKey/appSecret/agentId config
```

发送成功时日志应出现：

```text
sap dingtalk openapi sent: userid=...
```

## 6.1 配置 SAP NCo / ZFI019NL memory fetch

`ZFI057` 产值拆分入口后台第一步不运行 `ZFI019NL.vbs`，而是通过 SAP NCo 调用 `ZFI_SAP_API_GATEWAY` 的 `REPORT_SUBMIT/MEMORY_EXPORT`，从 `ZFI019NL` memory 输出读取物料集合。业务范围到工厂映射通过 `GET_GS03` 获取，默认传 `IV_SET_NAME=Z31`，再从返回表筛 `TITLE=业务范围` 并取 `FROM` 作为工厂；同一 `TITLE` 返回多行时必须全部取 `FROM`。步骤二“业务范围一次执行、一次性传入全部工厂”只针对 `ZFI057` 自动三步工作流，不改变 `ZFI072A`、`ZFI080`、`ZCO019` 等其他事务码的按工厂执行方式；VBS 先填日期和 `S_MTART-LOW=*`，再把首个工厂写入 `S_WERKS-LOW` 通过 SAP 必填校验，多工厂时继续把全部工厂写入 `S_WERKS` 多选，单工厂时跳过多选。上线前必须确认：

| 项目 | 要求 |
| --- | --- |
| NCo 依赖源 | `D:\RPA\依赖\SapNco\sapnco.dll`、`sapnco_utils.dll`、`ijwhost.dll`、`cpc4n.dll` |
| 运行目录 DLL | `D:\RPA\bin\` 必须包含上述四个 DLL，以及 `System.Configuration.ConfigurationManager.dll`、`System.Security.Permissions.dll` |
| 本机配置 | `D:\RPA\config.local.json` 必须包含 `sapNco`、`zfi057Workflow.gs03SetName` 和 `zfi057Workflow.zfi019nlMemory`；`gs03SetName` 默认 `Z31` |
| SAP 登录配置 | `%LOCALAPPDATA%\SapWebLauncher\config.json` 仍由 `04_配置SAP登录信息.bat` 在固定 Windows 执行账号下生成 |
| SAP 网关对象 | `ZFI_SAP_API_GATEWAY` 必须支持 `GET_GS03`；调用参数为 `IV_SET_NAME=Z31`，返回表用 `TITLE` 匹配业务范围，同一 `TITLE` 多行时用全部 `FROM` 输出工厂 |

单独验收业务范围到工厂映射：

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" --test-zfi057-get-gs03 --businessArea 2800 --setName Z31
```

成功标准：`status=success`、`setName=Z31`、`plantCount` 大于 0、`plants` 包含 `GET_GS03` 返回表中 `TITLE=2800` 对应的全部 `FROM` 工厂。

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

## 7. 初始化数据库

首次部署或需要确认迁移时执行：

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" --init-db
```

预期数据库：

```text
D:\RPA\data\sap-rpa-config.db
```

升级时不要删除数据库，除非已经备份并明确接受会丢失配置、运行历史和日志。

## 8. 启动或重启本项目后端

只停止当前项目的 `SapWebLauncher.exe --serve`：

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -eq "SapWebLauncher.exe" -and $_.CommandLine -like "*D:\RPA\bin\SapWebLauncher.exe*--serve*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

启动：

```powershell
Start-Process -FilePath "D:\RPA\bin\SapWebLauncher.exe" -ArgumentList "--serve" -WorkingDirectory "D:\RPA"
```

服务器重启或完整启动本项目时，优先使用总入口脚本：

```text
D:\RPA\启动脚本\start_sap_rpa_services.cmd
```

脚本默认会自动识别本机 IPv4 作为 HTTP 兼容入口。如果正式机有多块网卡或需要强制指定正式系统 IP，运行：

```text
D:\RPA\启动脚本\start_sap_rpa_services.cmd -HttpCompatibilityHost 10.0.2.120
```

不要停止其他项目的 `node.exe`，不要改别人的 80、6173 等端口。

## 9. 验收

基础检查：

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" test
Invoke-RestMethod "http://127.0.0.1:8080/api/health"
```

必须继续做真实业务验收：

1. 打开 `https://fi_automation.srv.lstech.com/rpa/`；兼容排障时再打开 `http://<服务器IP>:6174/rpa/`。当前联调服务器用 `http://10.0.41.158:6174/rpa/`，正式系统用 `http://10.0.2.120:6174/rpa/`。
2. 运行 `D:\RPA\启动脚本\check_sap_rpa_services.cmd`，确认 SAP GUI COM 两个 ProgID 均为 `True`，四个 URL 均为 `200 OK`。正式系统可运行 `D:\RPA\启动脚本\check_sap_rpa_services.cmd -HttpCompatibilityHost 10.0.2.120` 强制验收生产 IP。脚本对 HTTPS 使用 `curl.exe --ssl-no-revoke`，只跳过内网 CRL/OCSP 吊销查询，不跳过证书链和域名校验。
3. 单独运行 `--test-zfi019nl-memory --businessArea 2800 --period 2026.04.27 --weekEnd 2026.05.03`，确认 ZFI057 第一步能通过 NCo/MEMORY_EXPORT 拿到 ZFI019NL 物料集合。
4. 提交一次受控事务码任务。
5. 确认 `runs` 有记录，`run_logs` 或 `run_result_logs` 有日志。
6. 确认 VBS 从 `D:\RPA\transactions` 执行，并写回标准结果。
7. 已登录 SAP GUI 时，日志出现 `Detected ready SAP GUI session; skip sapshcut login`。
8. 钉钉启用时，日志出现 `sap dingtalk openapi sent: userid=...`。
9. 页面/API 不返回 SAP 密码、钉钉 `appSecret`、token。
10. 含“保存”的 7 个事务码 `ZFI072A`、`ZFI072N`、`ZFI080`、`ZFI080B`、`ZCO019`、`ZFI019NA`、`ZFI019NL` 跑完后，Excel 必须落在 `fileStorage.alvExportDataDirectory` 配置目录下的 `yyyy_WKnn_工厂` 文件夹；业务范围型导出按 Excel 工厂列拆分，只有表头/无数据时清理 raw 目录且不当技术失败。

## 10. 常见漏项

1. 只复制 `index.html`，漏复制 `assets\js`。
2. 只改源码 VBS，漏复制到运行目录 `transactions`。
3. 只 `git pull`，没有 publish 到 `D:\RPA\bin`。
4. 后端仍运行旧 exe，或协议入口指向旧的 `%LOCALAPPDATA%\SapRpaLauncher`。
5. 只有 `config.local.example.json`，没有真实 `config.local.json`。
6. `baseUrl` 填成 `/token` 或完整发送接口。
7. 没有在真实 `config.local.json` 设置 `fileStorage.alvExportDataDirectory`，或者改完保存目录后没有重启后端，导致 Excel 仍落到旧目录。
8. 手工 SAP GUI 正常，但脚本组件未注册，程序检测不到 ready session。
9. 健康检查通过，但真实任务没有写数据库、没有跑 VBS、没有生成 Excel 或没有发钉钉。
10. 整包拷贝 `D:\RPA` 时把测试机 `config.local.json`、SQLite、证书或 SAP DPAPI 登录配置覆盖到生产机。
11. 内网服务器访问不到证书吊销服务器时，PowerShell `Invoke-WebRequest` 可能报 TLS 通道错误；验收以 `check_sap_rpa_services.cmd` 的 GET 检查和浏览器证书结果为准。
12. ZFI057 第一步只看 health，没有单独跑 `--test-zfi019nl-memory` 确认 NCo/MEMORY_EXPORT 能按业务范围和日期拿到物料集合。
13. 生成上线包后只看文件存在，没有从最终包/解压目录执行覆盖升级、启动、网页提交、数据库写回和 Excel 落盘验收。

## 11. 生成上线包

在开发/打包机执行。不要直接用 `powershell -File` 运行这个中文路径脚本，Windows PowerShell 5.1 可能按旧编码解析中文路径；使用下面的 UTF-8 ScriptBlock 方式，或直接双击 `.cmd`：

```powershell
$script = Get-Content -LiteralPath "D:\RPA\RpaProject\上线安装包\scripts\make_package.ps1" -Raw -Encoding UTF8
& ([ScriptBlock]::Create($script)) -PackageSource "D:\RPA\RpaProject\上线安装包"
```

或双击：

```text
D:\RPA\RpaProject\上线安装包\00_生成上线安装包.cmd
```

生成包只包含手工安装文档、必要脚本、发布后的 `SapWebLauncher`、前端资源、VBS 和配置模板；不包含真实 `config.local.json`、SQLite、日志、输出文件、`D:\RPA\certs` 证书目录或 SAP 登录配置。

交付前必须从最终包或解压目录做一次真实验收：记录包路径、生成时间和 `PACKAGE_VERSION.txt`，按 `D:\RPA` 目标路径执行一次覆盖升级，确认生产专属 `config.local.json`、SQLite、证书、日志和输出未被覆盖；启动后确认 `runtimeRoot=D:\RPA`、正式 HTTPS/兼容 HTTP 入口可用，并从网页提交一次受控任务，验证数据库写回、钉钉日志和含“保存”事务的 Excel 落盘。失败时收集 `D:\RPA\logs` 对应 stderr/log 尾部。
