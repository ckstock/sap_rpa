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
| 对外 URL | `http://10.0.41.158:6174/rpa/` | 给用户访问的链接，网关和防火墙另行确认。 |
| SAP 登录配置 | `%LOCALAPPDATA%\SapWebLauncher\config.json` | 在固定 Windows 执行账号下生成。 |
| 钉钉真实配置 | `D:\RPA\config.local.json` | 只保存在服务器本机，不提交 Git。 |
| 钉钉模板 | `D:\RPA\config.local.example.json` | 只能作为字段说明。 |

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
  transactions\*.vbs
  transactions\transaction-config.json
  data\sap-rpa-config.db
  logs\
  outputs\
  config.local.example.json
  config.local.json
```

不要提交或覆盖这些服务器本机状态：

```text
D:\RPA\data\sap-rpa-config.db
D:\RPA\logs\
D:\RPA\outputs\
D:\RPA\config.local.json
%LOCALAPPDATA%\SapWebLauncher\config.json
```

可以把当前 `D:\RPA` 整包拷贝到生产机，但要把它当作程序包，而不是直接把测试机状态搬成生产状态：

1. 可以直接带走 `index.html`、`assets`、`gateway`、`启动脚本`、`bin`、`transactions`、`config.local.example.json`。
2. 全新生产机拷贝后必须重新填写真实 `D:\RPA\config.local.json`、重新生成 `%LOCALAPPDATA%\SapWebLauncher\config.json`、重新放置生产证书。
3. 升级已有生产机时，先备份并保留生产机自己的 `config.local.json`、`data\sap-rpa-config.db` 和 `certs\lstech.com`，不要被测试机文件覆盖。

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

手工兜底命令：

```powershell
Set-Location "C:\Program Files (x86)\SAP\FrontEnd\SAPgui"
C:\Windows\SysWOW64\regsvr32.exe saprotwr.dll
C:\Windows\SysWOW64\regsvr32.exe sapfewse.ocx
```

## 6. 配置钉钉 OpenAPI

从模板复制真实配置：

```powershell
Copy-Item "D:\RPA\config.local.example.json" "D:\RPA\config.local.json"
```

填写：

```json
{
  "dingTalkOpenApi": {
    "baseUrl": "https://你的钉钉OpenAPI网关根地址/",
    "appKey": "你的真实AppKey",
    "appSecret": "你的真实AppSecret",
    "agentId": "你的真实AgentId"
  }
}
```

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

不要停止其他项目的 `node.exe`，不要改别人的 80、6173 等端口。

## 9. 验收

基础检查：

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" test
Invoke-RestMethod "http://127.0.0.1:8080/api/health"
```

必须继续做真实业务验收：

1. 打开 `http://10.0.41.158:6174/rpa/`。
2. 运行 `D:\RPA\启动脚本\check_sap_rpa_services.cmd`，确认 SAP GUI COM 两个 ProgID 均为 `True`，四个 URL 均为 `200 OK`。脚本对 HTTPS 使用 `curl.exe --ssl-no-revoke`，只跳过内网 CRL/OCSP 吊销查询，不跳过证书链和域名校验。
3. 提交一次受控事务码任务。
4. 确认 `runs` 有记录，`run_logs` 或 `run_result_logs` 有日志。
5. 确认 VBS 从 `D:\RPA\transactions` 执行，并写回标准结果。
6. 已登录 SAP GUI 时，日志出现 `Detected ready SAP GUI session; skip sapshcut login`。
7. 钉钉启用时，日志出现 `sap dingtalk openapi sent: userid=...`。
8. 页面/API 不返回 SAP 密码、钉钉 `appSecret`、token。

## 10. 常见漏项

1. 只复制 `index.html`，漏复制 `assets\js`。
2. 只改源码 VBS，漏复制到运行目录 `transactions`。
3. 只 `git pull`，没有 publish 到 `D:\RPA\bin`。
4. 后端仍运行旧 exe，或协议入口指向旧的 `%LOCALAPPDATA%\SapRpaLauncher`。
5. 只有 `config.local.example.json`，没有真实 `config.local.json`。
6. `baseUrl` 填成 `/token` 或完整发送接口。
7. 手工 SAP GUI 正常，但脚本组件未注册，程序检测不到 ready session。
8. 健康检查通过，但真实任务没有写数据库、没有跑 VBS 或没有发钉钉。
9. 整包拷贝 `D:\RPA` 时把测试机 `config.local.json`、SQLite、证书或 SAP DPAPI 登录配置覆盖到生产机。
10. 内网服务器访问不到证书吊销服务器时，PowerShell `Invoke-WebRequest` 可能报 TLS 通道错误；验收以 `check_sap_rpa_services.cmd` 的 GET 检查和浏览器证书结果为准。

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

生成包只包含手工安装文档、必要脚本、发布后的 `SapWebLauncher`、前端资源、VBS 和配置模板；不包含真实 `config.local.json`、SQLite、日志、输出文件或 SAP 登录配置。
