# SAP RPA V2 上线安装包说明

本目录不再提供“一键安装器”。现在的交付方式是：

1. 手工安装指南和上线核对清单。
2. 必要脚本：生成上线包、配置 SAP 登录信息。
3. 发布产物：`SapWebLauncher`、前端页面、VBS 事务脚本、钉钉配置模板。

权威安装步骤请先读：

```text
上线安装文档清单.md
最终上线部署步骤\README_V2_Windows_Server_上线部署.md
```

## 保留文件

| 文件 | 用途 |
| --- | --- |
| `上线安装文档清单.md` | 上线前、上线中、上线后的人工核对清单。 |
| `最终上线部署步骤\README_V2_Windows_Server_上线部署.md` | Windows Server 手工部署 runbook。 |
| `config.local.example.json` | 钉钉 OpenAPI 配置模板，只能作为字段说明。 |
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
| 钉钉真实配置 | `D:\RPA\config.local.json` | 必须手工填写真实值，不提交 Git。 |
| 对外访问地址 | `https://fi_automation.srv.lstech.com/rpa/` | 正式用户链接；DNS、443、证书、路径前缀、防火墙和网关需要单独确认。 |
| 兼容访问地址 | `http://10.0.41.158:6174/rpa/` | 保留给内网端口访问和排障；这是 HTTP，不是 HTTPS。 |
| HTTPS 证书目录 | `D:\RPA\certs\lstech.com` | 只能放服务器本机，不提交 Git；权限限制为执行账号、Administrators、SYSTEM。 |

## 钉钉配置

安装包只能带模板 `config.local.example.json`。正式推送必须在运行根目录放真实文件：

```powershell
Copy-Item "D:\RPA\config.local.example.json" "D:\RPA\config.local.json"
```

真实文件格式：

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

`baseUrl` 只填接口根地址，不要把 `/token` 或 `asyncsend_v2` 写进去。真实 `config.local.json`、SAP 密码、SQLite、日志和输出文件都不能提交到 GitHub。

## Git 更新后的发布要求

`git pull` 只更新源码，不会更新线上运行目录。凡是改了后端、API、队列、通知、数据库迁移、SAP 登录复用、VBS 参数替换或事务脚本，必须重新发布并复制到运行目录。

生产机必须复制的文件清单：

| 类别 | 复制到 `D:\RPA` 的位置 |
| --- | --- |
| 前端 | `index.html`、`assets\js\*.js` |
| 网关 | `gateway\rpa-gateway.js`、`gateway\start-rpa-gateway.ps1` |
| 启动脚本 | `启动脚本\start_sap_rpa_services.cmd`、`check_sap_rpa_services.cmd`、`01_start_sap_rpa_services.ps1`、`02_check_sap_rpa_services.ps1`、`gitnexus.cmd`、`gitnexus.ps1` |
| 后端 | `dotnet publish` 后的全部输出复制到 `D:\RPA\bin\` |
| 事务脚本 | `网页启动登录\transactions\*.vbs` 和 `transaction-config.json` 复制到 `D:\RPA\transactions\` |
| 配置模板 | `上线安装包\config.local.example.json` 复制到 `D:\RPA\config.local.example.json` |

生产机本地生成或保留的内容不要从开发机覆盖：`D:\RPA\config.local.json`、`D:\RPA\data\sap-rpa-config.db`、`D:\RPA\logs\`、`D:\RPA\outputs\`、`D:\RPA\certs\lstech.com\`、`%LOCALAPPDATA%\SapWebLauncher\config.json`。

```powershell
Set-Location "D:\RPA\RpaProject"
git pull

dotnet publish "D:\RPA\RpaProject\网页启动登录\SapWebLauncher\SapWebLauncher.csproj" -c Release -o "D:\RPA\RpaProject\publish\SapWebLauncher"

Copy-Item "D:\RPA\RpaProject\publish\SapWebLauncher\*" "D:\RPA\bin" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\index.html" "D:\RPA\index.html" -Force
Copy-Item "D:\RPA\RpaProject\assets" "D:\RPA\assets" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\gateway" "D:\RPA\gateway" -Recurse -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\*.vbs" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\网页启动登录\transactions\transaction-config.json" "D:\RPA\transactions\" -Force
Copy-Item "D:\RPA\RpaProject\上线安装包\config.local.example.json" "D:\RPA\config.local.example.json" -Force
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

这个脚本只匹配 `D:\RPA\gateway\rpa-gateway.js`，会启动 `http://0.0.0.0:6174/rpa/` 和 `https://0.0.0.0:443/rpa/`。不要停止其他项目的 `node.exe`，不要占用别人已有的 80、6173 等端口。

注意：`D:\RPA\启动脚本` 是线上运行入口；`D:\RPA\RpaProject\启动脚本` 是 Git 源码副本。两处脚本默认 `RuntimeRoot = D:\RPA`，从源码目录双击也会操作线上运行目录，不是独立测试环境。

服务器重启后直接运行：
```text
D:\RPA\启动脚本\start_sap_rpa_services.cmd
```

启动后检查：
```text
D:\RPA\启动脚本\check_sap_rpa_services.cmd
```

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
| HTTP 兼容网关 | `node.exe D:\RPA\gateway\rpa-gateway.js`，`0.0.0.0:6174` | 提供 `http://10.0.41.158:6174/rpa/`。 |
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

如果手工 SAP GUI 登录正常，但网页执行仍反复登录、没有复用已登录会话，管理员 PowerShell 里注册 SAP GUI 脚本组件：

```powershell
Set-Location "C:\Program Files (x86)\SAP\FrontEnd\SAPgui"
C:\Windows\SysWOW64\regsvr32.exe saprotwr.dll
C:\Windows\SysWOW64\regsvr32.exe sapfewse.ocx
```

## 验收边界

健康检查只能说明进程可达，不能证明上线完成。最终验收必须覆盖：

1. 页面 `https://fi_automation.srv.lstech.com/rpa/` 能打开。
2. `https://fi_automation.srv.lstech.com/rpa/api/health` 返回正常。
3. 兼容入口 `http://10.0.41.158:6174/rpa/` 能打开。
4. `D:\RPA\启动脚本\check_sap_rpa_services.cmd` 输出四个 URL 检查均为 `200 OK`。
5. 浏览器从正式域名打开时，API 请求走 `https://fi_automation.srv.lstech.com/rpa/api/*`，不是旧的 `127.0.0.1`、旧 IP 或旧 `/charge`。
6. 证书域名匹配 `fi_automation.srv.lstech.com`，有效期未过期，浏览器无证书告警。
7. `D:\RPA\certs\lstech.com\*.key`、`*.pfx`、`passwd.txt` 仅执行账号、Administrators、SYSTEM 可读。
8. `http://127.0.0.1:8080/api/health` 返回的 `runtimeRoot` 是运行根目录。
9. 网页提交一次受控任务，SQLite 有 `runs` 和日志记录。
10. VBS 从运行目录 `transactions` 读取的是最新脚本。
11. SAP GUI 已登录复用时，日志出现 `Detected ready SAP GUI session; skip sapshcut login`。
12. 钉钉启用时，日志出现 `sap dingtalk openapi sent: userid=...`。
