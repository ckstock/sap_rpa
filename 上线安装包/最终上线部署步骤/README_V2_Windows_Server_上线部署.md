# SAP RPA V2 Windows Server 最终上线部署步骤

本文档用于公司 Windows Server 上线 SAP RPA V2。当前 `上线安装包` 目录里的原有脚本仍然需要保留：

- `00_生成上线安装包.cmd`：开发/打包机生成发布包。
- `01_安装到本机.bat`：把 `SapWebLauncher` 安装到当前 Windows 用户目录，并注册 `sap-rpa://` 协议。
- `02_检测环境.bat`：检查执行器、协议、事务脚本、SAP GUI、SAP 登录配置。
- `03_卸载协议和程序.bat`：清理当前用户安装。
- `04_配置SAP登录信息.bat`：在目标 Windows 用户下生成本机 SAP 登录配置，密码用 DPAPI 保护。
- `scripts/`：上述批处理调用的 PowerShell 脚本。

V2 与旧版的区别是：公司服务器不只是注册浏览器协议，还要运行本地 API、SQLite 配置库、执行历史、VBS 脚本目录和输出目录。SAP 密码仍然只保存在服务器 Windows 执行账号本地，不进入浏览器页面和普通数据库。

## 1. 服务器前置条件

1. 准备一台公司 Windows Server 或专用 Windows 机器。
2. 使用固定 Windows 执行账号登录服务器桌面。
3. 安装 SAP GUI，并确认 SAP GUI Scripting 已启用。
4. 不要让多人共用并手工操作同一个执行桌面会话。
5. 执行器必须运行在已登录的交互式桌面会话中，不要作为纯 Session 0 Windows Service 运行。
6. 若需要从其他机器访问本地 API，提前确认防火墙和端口策略；默认优先本机访问。

## 2. 代码和运行目录

推荐保留两个目录：

```text
源码仓库：D:\工作\sap_rpa
运行目录：D:\sap_ai
```

运行目录建议包含：

```text
D:\sap_ai\index.html
D:\sap_ai\data\sap-rpa-config.db
D:\sap_ai\transactions\*.vbs
D:\sap_ai\outputs\
D:\sap_ai\logs\
```

可通过环境变量覆盖运行根目录：

```powershell
setx SAP_RPA_HOME "D:\sap_ai"
```

设置后重新打开终端或重新登录，确保执行器读取到新的环境变量。

### 2.1 Git 拉取和运行目录边界

公司服务器上线时，源码和 VBS 可以直接从 GitHub 拉取当前 V2 分支：

```powershell
git clone https://github.com/ckstock/sap_rpa.git "D:\工作\sap_rpa"
cd /d "D:\工作\sap_rpa"
git checkout codex/v2-local-api-sqlite
git pull
```

已有仓库时只需要：

```powershell
cd /d "D:\工作\sap_rpa"
git pull
```

Git 只负责同步源码、页面、部署脚本和 VBS，例如：

```text
D:\工作\sap_rpa\index.html
D:\工作\sap_rpa\网页启动登录\SapWebLauncher\Program.cs
D:\工作\sap_rpa\网页启动登录\transactions\ZFI072A.vbs
```

以下运行产物不要从 Git 复制，也不要提交到 Git：

```text
D:\sap_ai\data\sap-rpa-config.db
D:\sap_ai\logs\
D:\sap_ai\outputs\
%LOCALAPPDATA%\SapWebLauncher\config.json
```

SQLite 的表结构和迁移逻辑在 `SapWebLauncher` 代码中，第一次执行 `--init-db` 或 `--serve` 时自动创建/升级。`sap-rpa-config.db` 只保存目标服务器本机配置、运行历史和运行日志，迁移到公司服务器时应在服务器上重新初始化，然后通过基础配置页面维护业务数据。

SAP 登录配置必须在目标服务器固定 Windows 执行账号下重新生成，不能从开发电脑复制。密码由 DPAPI 绑定当前 Windows 用户保护。

### 2.2 从 Git 更新到运行目录

每次从 Git 拉取新版本后，按以下顺序同步到运行目录：

```powershell
cd /d "D:\工作\sap_rpa"
git pull

dotnet build "D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj"

Copy-Item "D:\工作\sap_rpa\index.html" "D:\sap_ai\index.html" -Force
Copy-Item "D:\工作\sap_rpa\网页启动登录\SapWebLauncher\bin\Debug\net8.0-windows\*" "D:\sap_ai\bin" -Recurse -Force
Copy-Item "D:\工作\sap_rpa\网页启动登录\transactions\*.vbs" "D:\sap_ai\transactions\" -Force
```

如果只改了 VBS，也仍建议从 Git 拉取后复制 `transactions\*.vbs` 到 `D:\sap_ai\transactions\`，确保运行脚本和源码一致。

同步后重启本地 API：

```powershell
Get-Process SapWebLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process -FilePath "D:\sap_ai\bin\SapWebLauncher.exe" -ArgumentList "--serve" -WorkingDirectory "D:\sap_ai\bin"
```

## 3. 生成上线包

在开发/打包机执行：

```powershell
powershell -ExecutionPolicy Bypass -File "D:\工作\sap_rpa\上线安装包\scripts\make_package.ps1"
```

或双击：

```text
D:\工作\sap_rpa\上线安装包\00_生成上线安装包.cmd
```

默认输出：

```text
D:\工作\SapRpa上线安装包
```

生成后把整个 `SapRpa上线安装包` 文件夹复制到目标服务器。

## 4. 安装执行器和协议

在目标服务器上，用固定 Windows 执行账号登录后执行：

```text
01_安装到本机.bat
```

安装后执行器位于：

```text
%LOCALAPPDATA%\SapRpaLauncher
```

协议注册位于：

```text
HKEY_CURRENT_USER\Software\Classes\sap-rpa
```

该注册使用 HKCU，不需要管理员权限。

## 5. 配置 SAP 登录信息

在目标服务器同一个 Windows 执行账号下执行：

```text
04_配置SAP登录信息.bat
```

按提示输入 SAP system、client、user、password、language、sysnr。

生成文件：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

密码字段使用 `passwordProtected`，由 Windows DPAPI 按当前 Windows 用户加密。不要把这个文件复制到其他 Windows 用户或其他电脑；迁移服务器时必须重新运行配置脚本。

## 6. 初始化 V2 运行目录和 SQLite

确认运行目录存在：

```powershell
New-Item -ItemType Directory -Force -Path "D:\sap_ai\data","D:\sap_ai\transactions","D:\sap_ai\outputs","D:\sap_ai\logs"
```

复制页面和 VBS：

```powershell
Copy-Item "D:\工作\sap_rpa\index.html" "D:\sap_ai\index.html" -Force
Copy-Item "D:\工作\sap_rpa\网页启动登录\transactions\*.vbs" "D:\sap_ai\transactions\" -Force
Copy-Item "D:\工作\sap_rpa\网页启动登录\transactions\transaction-config.json" "D:\sap_ai\transactions\" -Force
```

初始化数据库：

```powershell
& "%LOCALAPPDATA%\SapRpaLauncher\SapWebLauncher.exe" --init-db
```

预期数据库：

```text
D:\sap_ai\data\sap-rpa-config.db
```

## 7. 启动本地 API

在目标服务器交互式桌面会话中启动：

```powershell
& "%LOCALAPPDATA%\SapRpaLauncher\SapWebLauncher.exe" --serve
```

如后续改为开机自动启动，建议使用“任务计划程序”，触发条件为固定 Windows 执行账号登录后启动。不要改成纯后台 Windows Service 直接跑 SAP GUI 自动化。

## 8. 验证命令

执行器自测：

```powershell
& "%LOCALAPPDATA%\SapRpaLauncher\SapWebLauncher.exe" test
```

环境检测：

```text
02_检测环境.bat
```

API 检查：

```powershell
Invoke-RestMethod http://127.0.0.1:17890/api/health
Invoke-RestMethod http://127.0.0.1:17890/api/config
Invoke-RestMethod http://127.0.0.1:17890/api/schema
```

浏览器检查：

```text
D:\sap_ai\index.html
```

页面应能显示工作台、执行任务、定时任务、基础配置，并从 API 读取配置。基础配置页面不应显示 SAP 密码、通知机器人 webhook 原文或 secret 原文。

## 9. ZFI072A 验收

ZFI072A 的 plants 必须来自页面/API/执行器传入参数，VBS 不再硬编码固定工厂。

验收点：

1. 页面选择或 API 传入 plants。
2. 创建运行记录后，run 参数中包含 `plants`。
3. 执行器替换 VBS 中 `{PLANTS}`。
4. VBS 执行后输出标准 key：

```text
STATUS_TYPE=
STATUS_TEXT=
OUTPUT_FILE=
ERROR=
```

如果未传 plants，脚本应失败并输出 `ERROR=`，不能自行使用默认固定工厂。

## 10. 上线前人工确认

1. SAP GUI Scripting 已在客户端和服务器策略中启用。
2. 固定 Windows 执行账号可登录服务器桌面。
3. SAP 技术账号权限覆盖目标事务码。
4. SAP 密码只通过 `04_配置SAP登录信息.bat` 在服务器本机生成。
5. SQLite、日志、输出目录不提交到 Git。
6. 通知机器人 webhook/secret 不明文返回前端。
7. 如 API 要给局域网访问，需要 IT 确认端口、防火墙、认证和反向代理策略。

## 11. 钉钉通知和 SAP 现成推送程序

执行成功、失败或开始执行后的通知由本地 API 负责，不由 VBS 负责。VBS 只执行 SAP GUI 自动化并返回标准结果：

```text
STATUS_TYPE=
STATUS_TEXT=
OUTPUT_FILE=
ERROR=
```

推荐通知链路：

1. 页面提交任务时带上操作人信息和钉钉用户标识，例如 `operatorId`、`operatorName`、`operatorDept`、`dingTalkUserId`。
2. API 创建 `runs` 记录并进入串行队列。
3. 队列开始执行时写入 `run_logs`，可推送“任务开始执行”。
4. `ZFI072A.vbs` 执行结束后，API 更新 `runs.status`、`sap_status_type`、`sap_status_text`、`message`。
5. API 根据运行结果调用通知适配器。
6. 如果公司已有 SAP 程序可按钉钉 ID 推送消息，优先由 API 调用该 SAP 程序的 HTTP/RFC 接口。

不建议用 VBS 再打开一个 SAP 事务码去做通知推送。原因是 SAP GUI 桌面是串行资源，通知如果也占用 SAP GUI，会拖慢后续任务，并且高并发时更容易产生多登录窗口。

推荐让 SAP 提供以下任一接口：

```text
HTTP API：POST /sap/bc/.../z_rpa_dingtalk_notify
RFC 函数：Z_RPA_DINGTALK_NOTIFY
```

建议入参：

```text
dingTalkUserId
runId
tcode
status
message
startedAt
finishedAt
durationMs
```

建议后续新增 `notification_outbox` 表，执行任务完成后先把通知写入 outbox，再由后台异步发送和重试。这样即使钉钉或 SAP 通知接口临时失败，也不会阻塞 SAP GUI 串行队列。

## 12. 当前仍需保留的安装文件

本目录现有安装文件仍然需要保留。它们负责“生成发布包、安装执行器、注册协议、配置 SAP 登录、检测环境”。V2 新增的是服务器运行目录、SQLite/API 启动和上线验收步骤，不替代这些脚本。

后续目录重构时，可再把 `SapWebLauncher`、前端、VBS、部署脚本拆成更清晰的 `backend/`、`frontend/`、`sap/`、`deploy/` 结构。
