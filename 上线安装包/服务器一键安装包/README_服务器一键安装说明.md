# SAP RPA V2 服务器一键安装说明

## 入口

双击运行：

```text
启动一键安装器.bat
```

安装器会打开一个 Windows 图形界面。服务器根目录不要靠猜，如果目标服务器没有 `D:` 盘，就在界面里填写或选择实际目录，例如 `C:\SAP_RPA`、`E:\SAP_RPA` 或公司规定目录。

## 必填路径

| 项目 | 说明 |
| --- | --- |
| 运行根目录 | SAP RPA V2 在服务器上的最终运行目录。保存 `index.html`、`assets\js`、`bin`、`transactions`、`data`、`logs`、`outputs`。 |
| 源码/发布包根目录 | 可以是 Git 拉取后的仓库根目录，也可以是复制到服务器的发布包根目录。安装器会从这里寻找页面、前端静态资源、VBS 和 SapWebLauncher。 |

默认路径只用于减少输入，不代表服务器必须有对应盘符。安装器优先读取 `SAP_RPA_HOME`，没有时才给出可修改默认值。

## 主要按钮

| 按钮 | 作用 |
| --- | --- |
| 一键部署/升级 | 创建运行目录，复制页面、`assets\js`、VBS、执行器，注册 `sap-rpa://` 协议，初始化或迁移 SQLite，启动本地 API，并做状态检测。默认保留数据库、日志、导出文件和本机配置。 |
| 配置 SAP 登录 | 在当前 Windows 执行账号下录入 SAP system/client/user/password/language/sysnr，密码用 Windows DPAPI 保护，不进入网页，不提交 Git。 |
| 初始化/迁移 SQLite | 调用 `SapWebLauncher.exe --init-db`，已有数据库会先备份再迁移，不会直接清空。 |
| 启动本地 API | 启动 `SapWebLauncher.exe --serve`，默认监听 `http://127.0.0.1:17890`。 |
| 检测上线状态 | 检查关键文件、协议注册、SQLite、API health/config/schema、SAP GUI 基础可用性。 |
| 打开运行页面 | 打开运行根目录下的 `index.html`。 |
| 备份当前运行目录 | 备份页面、执行器、VBS、数据库和本机配置到 `backups` 子目录。 |
| 备份并重建 SQLite | 危险动作，单独按钮。先备份现有数据库，再重建空库。不会删除 SAP DPAPI 登录配置。 |
| 停止本地 API | 停止当前用户下以 `--serve` 运行的 SapWebLauncher 进程。 |
| 一键复制日志 | 复制当前诊断日志、关键路径、API 地址和日志文件路径到剪贴板，便于直接发给 AI 或管理员排错。 |
| 保存诊断日志 | 把当前诊断日志保存为 `logs\installer-diagnostic-<时间>.txt`。 |

## 关键上线前提

1. 必须使用固定 Windows 执行账号登录服务器桌面。
2. SAP GUI 和 SAP GUI Scripting 必须在该账号下可用。
3. SAP GUI/VBS 自动化不能作为普通 Windows Service 在 Session 0 里跑。
4. SQLite 数据库、日志、导出文件、`config.local.json`、SAP 登录密文都是服务器本机状态，升级时默认保留。
5. Git 和安装包只允许带 `config.local.example.json` 占位模板，不允许带真实 `appKey`、`appSecret`、`agentId`、SAP 密码或服务器私有地址。

## 钉钉配置

真实钉钉参数只放在服务器本机运行根目录的 `config.local.json`，不要提交 Git。模板如下：

```json
{
  "dingTalkOpenApi": {
    "baseUrl": "请填写接口根地址",
    "appKey": "请填写真实AppKey",
    "appSecret": "请填写真实AppSecret",
    "agentId": "请填写真实AgentId"
  }
}
```

安装器会把模板复制为 `config.local.example.json`。如果需要真实联调，由管理员在服务器本机另建 `config.local.json` 并填写真实值。

## 命令行模式

图形界面以外，也支持管理员从命令行调用：

```powershell
$script = Get-Content -LiteralPath ".\SapRpaServerSetup.ps1" -Raw -Encoding UTF8
& ([ScriptBlock]::Create($script)) -Cli -Action install -RuntimeRoot "C:\SAP_RPA" -SourceRoot "C:\deploy\sap_rpa"
```

常用 `Action`：

| Action | 说明 |
| --- | --- |
| `install` | 执行部署/升级、启动 API、检测状态。 |
| `check` | 只检测上线状态。 |
| `start-api` | 只启动本地 API。 |
| `open-page` | 只打开运行页面。 |
| `init-db` | 只初始化或迁移 SQLite。 |
| `backup` | 只备份当前运行目录。 |
| `reset-db` | 备份并重建 SQLite。命令行必须显式追加 `-ForceResetDb`，GUI 模式会弹出二次确认。 |

## 验收方式

安装完成后至少确认：

1. `SapWebLauncher.exe test` 通过。
2. `http://127.0.0.1:17890/api/health` 返回正常。
3. `http://127.0.0.1:17890/api/config` 和 `/api/schema` 能返回数据。
4. `index.html` 能打开并读取 API 配置。
5. `assets\js\portal-state.js`、`portal-utils.js`、`portal-api.js`、`portal-render.js`、`portal-actions.js`、`main.js` 均存在，浏览器加载无缺失。
6. `transactions\ZFI072A.vbs` 存在。
7. 数据库位于运行根目录 `data\sap-rpa-config.db`。
8. 页面和 API 不返回 SAP 密码、钉钉 appSecret、token 等敏感信息。
