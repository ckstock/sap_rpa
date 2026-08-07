# SAP RPA V2 QA Report

检查时间：2026-06-17
检查范围：`D:\工作\sap_rpa`
运行目录：`D:\sap_ai`

## 本轮 QA 范围

- 定时任务保存后能从 API/SQLite 读取并展示。
- 定时任务后台线程能读取启用任务，后续到点可创建 `runs` 队列任务。
- ZFI072A 多工厂按工厂拆成 parent/child run 后串行执行。
- 9301 等长耗时工厂不再被旧的 45/60 秒本地超时提前判失败。
- VBS 执行后等待保存按钮 `Enabled=True` 再保存，保存后执行 `/NEX` 清理 SAP GUI。
- child run 不单独发送最终通知，parent run 在全部工厂结束后汇总通知。

## 已完成验证

### 构建和发布

```powershell
$env:DOTNET_ROOT='D:\sap_ai\.dotnet'
$env:PATH='D:\sap_ai\.dotnet;' + $env:PATH
& 'D:\sap_ai\.dotnet\dotnet.exe' build 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj' --no-restore
& 'D:\sap_ai\.dotnet\dotnet.exe' publish 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj' -c Debug -o 'D:\sap_ai\bin' --no-restore
```

结果：`0 warning, 0 error`。已发布到 `D:\sap_ai\bin`。

### API 重启

旧进程已停止，新进程已启动：

```text
SapWebLauncher.exe --serve
Path: D:\sap_ai\bin\SapWebLauncher.exe
```

`GET /api/health` 返回：

- `ok=true`
- `queueMode=serial`
- `database=D:\sap_ai\data\sap-rpa-config.db`
- `transactionRoot=D:\sap_ai\transactions`

### API 配置和 Schema

已验证接口：

- `GET /api/config`：返回 `version=2`，plants/groups/rules/scheduleTasks 正常。
- `GET /api/schema`：包含 `runs`、`run_batch_items`、`schedule_task_runs`。
- `GET /api/schedules`：返回 `version=2` 和 `schedules` 列表。

### 定时任务保存/展示链路

用测试 ID `SCH-CODEX-VERIFY` 验证：

- `PUT /api/schedules/SCH-CODEX-VERIFY` 成功。
- `GET /api/schedules/SCH-CODEX-VERIFY` 能读回 `tcode=ZFI072A`、`plants=5021,9301`。
- `GET /api/schedules` 能在列表中看到该任务。
- 测试完成后已从 SQLite 删除测试任务和对应 `schedule_task_runs`，避免页面出现垃圾数据。

### SapWebLauncher 自测

```powershell
& 'D:\sap_ai\bin\SapWebLauncher.exe' test
```

结果：`11 PASS, 0 FAIL`。

### 前端 JS 语法

当前前端已经拆为 `index.html + assets/js/*.js`。语法检查应对模块文件执行，而不是再抽取内联脚本：

```powershell
$node='C:\Users\chen.kai6\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
Get-ChildItem 'D:\工作\sap_rpa\assets\js\*.js' | ForEach-Object { & $node --check $_.FullName }
Get-ChildItem 'D:\sap_ai\assets\js\*.js' | ForEach-Object { & $node --check $_.FullName }
```

基线要求：`portal-state.js`、`portal-utils.js`、`portal-api.js`、`portal-render.js`、`portal-actions.js`、`main.js` 均通过 `node --check`。

### 运行目录同步

已确认源码和运行目录一致：

- `D:\工作\sap_rpa\index.html` 与 `D:\sap_ai\index.html` 无差异。
- `D:\工作\sap_rpa\assets\js\*.js` 与 `D:\sap_ai\assets\js\*.js` 应保持一致。
- `D:\工作\sap_rpa\网页启动登录\transactions\ZFI072A.vbs` 与 `D:\sap_ai\transactions\ZFI072A.vbs` 无差异。

模块化后 QA 基线：

1. `index.html` 按顺序引用 `assets/js/portal-state.js`、`portal-utils.js`、`portal-api.js`、`portal-render.js`、`portal-actions.js`、`main.js`。
2. 运行目录复制/安装器必须同步 `assets/js/**`，不能只复制 `index.html`。
3. 浏览器加载页面时 6 个 JS 文件无缺失。
4. 最小点击回归：工作台提交能跳转执行页；执行页事务码下拉可刷新范围；ZFI019NL 显示“执行业务范围”；普通事务码显示“执行工厂”。

## 已修复的关键问题

### 旧 exe 导致 9301 被提前失败

日志确认此前运行的 `D:\sap_ai\bin\SapWebLauncher.exe` 仍是旧发布版本，旧逻辑会用短超时杀掉 VBS，导致 SAP GUI 仍在跑时 run 已失败。

已处理：

- 重新 build/publish 到 `D:\sap_ai\bin`。
- 停止旧 `SapWebLauncher.exe`。
- 启动新 `SapWebLauncher.exe --serve`。

### 定时任务响应字段冲突

实测 `PUT /api/schedules/{id}` 时发现后端返回对象同时包含 `tcode` 和 `tCode`，在当前 JSON 选项下触发大小写不敏感字段冲突。

已处理：

- 后端 schedule 返回只保留 `tcode/transactionCode/code`，前端仍兼容读取。
- 重新发布后 `PUT/GET/list` 已通过。

### 多客户端测试下 `/NEX` 兜底清理跳过实际会话

此前后端兜底清理固定按本机配置的 system/client/user 过滤，测试 TD1/ED1 时可能全部 `skip non-target session`。

已处理：

- `SAP_RPA_STRICT_SAP_SESSION=1` 时仍严格按 system/client/user 清理。
- 未开启严格匹配时，允许清理当前可关闭 SAP session，支持多客户端测试。

## 仍需实机确认

- 9301 长耗时工厂完整跑批：需确认保存按钮会从 disabled 变为 enabled，并最终保存成功。
- 多工厂串行实机流程：一个工厂失败时，后续工厂是否继续跑，parent run 是否汇总为 `partial_failed`。
- `/NEX` 清理现场行为：需观察日志出现 `CLEANUP: closing session ...` 和 `CLEANUP: sent /nex`，且不会留下多余 SAP 窗口。
- 定时任务真实到点触发：需等待一个调度周期，确认写入 `schedule_task_runs` 并创建真实 run。
- 钉钉通知：需确认 parent 汇总通知只发一次，成功/失败/失败工厂内容准确。

## 风险和建议

- 测试环境允许多客户端时，建议只保留 RPA 使用的 SAP GUI 会话，避免兜底 `/NEX` 误关同一桌面的其他窗口。
- 生产环境建议设置 `SAP_RPA_STRICT_SAP_SESSION=1`，按系统/客户端/用户精确匹配 SAP 会话。
- 9301 这类长耗时任务的最终验收必须以真实 SAP GUI 完整跑完为准，本轮已解决本地短超时和保存按钮等待问题，但未替代实机业务验证。
