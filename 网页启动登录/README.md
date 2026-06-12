# SapWebLauncher - 网页启动 SAP 登录和事务码脚本

本项目用于从网页唤醒本机程序，自动登录 SAP GUI，打开指定事务码，并按事务码脚本模板执行 SAP GUI Scripting。

正式协议只保留 `sap-rpa://`。旧的 `sap-zck://` 仅用于早期临时测试，正式安装包不再注册。

## 登录配置

Netlify 页面不传 SAP 密码。`SapWebLauncher` 从本机配置读取 SAP 登录信息：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

推荐通过上线安装包里的 `04_配置SAP登录信息.bat` 生成配置。配置格式：

```json
{
  "system": "dev300",
  "client": "300",
  "user": "YOUR_SAP_USER",
  "passwordProtected": "WINDOWS_DPAPI_PROTECTED_BASE64",
  "language": "ZH",
  "sysNr": "10"
}
```

`passwordProtected` 由 Windows DPAPI 按当前 Windows 用户加密。不要手工维护明文 `password`；旧版明文配置重新运行 `04_配置SAP登录信息.bat` 后会迁移为加密字段。

`system`、`client`、`sysNr` 必须按目标电脑实际 SAP Logon 配置填写。执行器不再默认回退到 `Fiori/400/04`，避免网页或空配置把客户端带错。

安全边界：DPAPI 保护的是配置文件静态存储。执行时程序会在本机解密密码并传给 SAP GUI/SAP Shortcut，日志会遮蔽密码，但同一 Windows 用户下的恶意程序仍可能截获运行时参数或内存。生产环境如果能接 SAP SSO/SNC，应优先用 SSO。

## 当前版本重点

- 先打通 `ZFI019NL` 最新版本事务码登录，并支持从网页传入多工厂、多业务范围参数。
- 所有事务码统一通过 `tcode` 参数进入 SAP。
- SAP 账号密码只存放在执行电脑本机配置，不放在 Netlify 页面 URL。
- 后续每个事务码只需要新增或配置对应 VBS 脚本模板。
- V2 公司服务器模式通过 `SapWebLauncher.exe --serve` 常驻本地 API 和串行队列；页面创建 queued run 后，由 Windows Server 上的后台执行器领取并执行，不依赖用户电脑的 `sap-rpa://`。
- `sap-rpa://` 只保留为本机兜底和单机调试入口。迁移到公司服务器后，正式页面应优先调用 API，不应唤醒普通用户电脑。

## V2 公司服务器后台运行

推荐服务器部署形态：

```text
公司门户/静态页面
  -> http://<windows-server>:17890/api/runs
  -> V2 运行目录: D:\sap_ai
  -> SQLite: D:\sap_ai\data\sap-rpa-config.db
  -> SapWebLauncher.exe --serve 后台队列线程
  -> SAP GUI 交互桌面会话
  -> D:\sap_ai\transactions\<TCODE>.vbs
  -> runs/run_result_logs/run_files
```

默认 V2 运行目录是 `D:\sap_ai`，也可以用 `SAP_RPA_HOME` 覆盖：

```powershell
$env:SAP_RPA_HOME = "D:\sap_ai"
SapWebLauncher.exe --init-db
SapWebLauncher.exe --serve
```

目录约定：

```text
D:\sap_ai\index.html                         # V2 页面预览/门户静态页
D:\sap_ai\data\sap-rpa-config.db             # SQLite 配置和执行历史
D:\sap_ai\transactions\transaction-config.json
D:\sap_ai\transactions\ZFI072A.vbs
D:\sap_ai\logs\launcher.log
D:\sap_ai\outputs\
```

首次启动时，如果新库不存在且旧库 `%LOCALAPPDATA%\SapWebLauncher\sap-rpa-config.db` 存在，程序会复制一份到 `D:\sap_ai\data\sap-rpa-config.db`。SAP 登录配置仍固定保留在 `%LOCALAPPDATA%\SapWebLauncher\config.json`，不要复制进 `D:\sap_ai` 或提交到 Git。

`--serve` 会同时启动：

- HTTP API：健康检查、事务码配置、执行任务、执行历史和结果回写。
- 串行队列工作线程：每次只领取一个 `queued` run，标记为 `running`，执行完成后写回 `success`、`failed` 或 `canceled`。

开发验证数据库和页面时，可以临时禁用后台队列，避免测试点击后立即进入 SAP：

```powershell
$env:SAP_RPA_DISABLE_QUEUE = "1"
SapWebLauncher.exe --serve
```

此时 `/api/health` 会返回 `queueMode: "disabled"`，`POST /api/runs` 仍会写入 SQLite，但不会执行 SAP GUI。正式服务器执行时不要设置这个环境变量。

不要把 SAP GUI 自动化做成纯 Session 0 Windows Service。建议在服务器上使用固定 Windows 执行账号登录桌面后，通过任务计划程序“用户登录时启动”运行：

```powershell
SapWebLauncher.exe --serve
```

该 Windows 账号负责：

- 保存 DPAPI 加密的 SAP 登录配置。
- 保持 SAP GUI Scripting 可用。
- 运行串行 SAP 自动化队列。

生产接入企业门户时，API 应部署在公司内网地址，并由门户或反向代理提供认证。前端传来的用户身份只能作为本地模拟；生产审计应以公司 SSO/钉钉网关注入的可信身份为准。

## V2 SQLite 数据模型

SQLite 是第一阶段本地数据库，表设计按未来迁移 SQL Server/PostgreSQL 保持边界清晰：

- `transactions`：事务码主数据、脚本文件、阶段、启用状态、脚本 hash 和版本。
- `runs`：一次执行任务，包含 `run_id`、事务码、发起人、队列状态、请求 JSON、SAP 状态栏、脚本信息、开始/结束时间和耗时。
- `run_result_logs`：执行日志明细，保存 VBS 输出、错误摘要和后台队列事件。
- `run_files`：导出文件记录，保存文件名、路径、类型和大小。
- `app_settings`：脚本目录、输出目录、密码存储方式和队列模式等本地设置。

后续迁移服务器数据库时，优先保持 API 合约和字段含义稳定，只替换 repository 层；SAP GUI/VBS 执行仍应留在 Windows Server 交互桌面执行器内。

迁移到公司服务器时，建议迁移 `D:\sap_ai\index.html`、`D:\sap_ai\transactions`、`D:\sap_ai\data`、`D:\sap_ai\outputs`。不要迁移个人电脑上的 `%LOCALAPPDATA%\SapWebLauncher\config.json`；应在服务器执行账号下重新运行登录配置脚本，让 DPAPI 按服务器 Windows 用户重新加密。

## V2 Local API

默认监听：

```text
http://127.0.0.1:17890
```

公司服务器需要让其他电脑访问时，可在启动前设置监听前缀，例如：

```powershell
$env:SAP_RPA_API_PREFIX = "http://+:17890/"
SapWebLauncher.exe --serve
```

`HttpListener` 使用 `+` 表示监听所有主机名；也可以改成服务器具体主机名。生产环境不要直接裸露到公网。建议只在公司内网开放，并通过门户后端或反向代理做认证、限流和审计。

前端默认访问 `http://127.0.0.1:17890`。迁移公司门户时，可在页面注入：

```html
<script>window.SAP_RPA_API_BASE = "http://windows-server:17890";</script>
```

或在浏览器本地设置：

```js
localStorage.setItem("sapRpaApiBase", "http://windows-server:17890")
```

主要接口：

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `/api/health` | 检查 API、版本和数据库路径 |
| `GET` | `/api/transactions` | 读取事务码配置 |
| `POST` | `/api/transactions` | 新增或更新事务码配置 |
| `PUT` | `/api/transactions/{code}` | 更新指定事务码配置 |
| `DELETE` | `/api/transactions/{code}` | 停用事务码 |
| `POST` | `/api/runs` | 创建 queued 执行任务 |
| `GET` | `/api/runs` | 查询执行历史 |
| `GET` | `/api/runs/{runId}` | 查询单次执行详情、日志和文件 |
| `POST` | `/api/runs/{runId}/result` | 执行器或外部工具回写结果 |

VBS 标准输出建议保留这些键，便于写入 `runs` 和 `run_files`：

```text
STATUS_TYPE=S
STATUS_TEXT=保存成功
OUTPUT_FILE=C:\path\result.xlsx
ERROR=错误摘要
```

## 编译

```powershell
dotnet build "D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj" -c Release
```

Release 输出路径：

```text
D:\工作\sap_rpa\网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe
```

## 注册协议

推荐使用上线安装包安装。开发调试时可运行：

```powershell
powershell -ExecutionPolicy Bypass -File "D:\工作\sap_rpa\网页启动登录\register.ps1"
```

## URL 示例

打开 ZFI019NL：

```text
sap-rpa://run?action=run&tcode=ZFI019NL&script=openOnly&plant=1022&plants=1022,1024,1032,6041&businessArea=2900&businessAreas=2900,9200,2800,3960&factoryGroup=PINGHU_30
```

如需测试 ZCK 脚本模式，仍通过正式协议传参：

```text
sap-rpa://run?action=run&tcode=zck&script=zck
```

## 参数说明

| 参数 | 说明 | 默认值 |
|---|---|---|
| `action` | 固定 `run` | `run` |
| `tcode` | SAP 事务码 | `ZFI019NL` |
| `script` | 脚本模板模式，当前支持 `openOnly`、`zck` | `openOnly` |
| `system` | SAP 系统名。正式网页不传，执行器以本机配置为准 | 本机配置 |
| `client` | SAP Client。正式网页不传，执行器以本机配置为准 | 本机配置 |
| `lang` | SAP 语言。正式网页不传，执行器以本机配置为准 | 本机配置 |
| `sysnr` | SAP 实例编号。正式网页不传，执行器以本机配置为准 | 本机配置 |
| `plant` | 兼容旧脚本的首个工厂 | 空 |
| `plants` | 多工厂 CSV，例如 `1022,1024,1032,6041` | 空 |
| `businessArea` | 兼容旧脚本的首个业务范围 | 空 |
| `businessAreas` | 多业务范围 CSV，例如 `2900,9200,2800,3960` | 空 |
| `factoryGroup` | 厂区组，例如 `PINGHU_30` | 空 |
| `period` | 核算周期/周别，后续脚本可读取 | 空 |
| `weekEnd` | 周结日期，后续脚本可读取 | 空 |

## 本地日志

```text
D:\sap_ai\logs\launcher.log
```

SAP 登录配置读取路径仍是：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```
