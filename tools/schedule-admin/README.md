# 定时任务集中管理工具

用于管理员集中查看、筛选和安全删除 SAP RPA 定时任务。工具只调用现有本地 API，不直接读取或修改 SQLite 数据库。

## 功能

- 读取全部定时任务：`GET /api/schedules`，请求不附加设置人过滤条件。
- 按综合关键字、创建人/更新人、人员编号、任务 ID、事务码筛选。
- 支持多选、全选当前筛选结果、查看任务安全详情。
- 删除前显示任务清单并要求二次确认。
- 通过 `DELETE /api/schedules/{id}` 删除任务，由后端同步清理尚未开始的排队任务。
- 删除完成后明确显示 API 返回的 `removedQueuedRuns` 数量。
- 不读取 SQLite，不保存配置，不请求或展示密码、令牌、密钥等敏感信息。

## 启动

正式本机入口为单文件 EXE：

```text
SAP定时任务集中管理.exe
```

也可以双击兼容启动脚本；同目录存在 EXE 时优先启动 EXE，否则回退到 Python 源码：

```text
启动定时任务管理工具.cmd
```

EXE 已包含 Python/Tk 运行时，日常使用不要求另外安装 Python，也不会调用大模型或消耗 AI Token。启动脚本的源码回退模式优先使用本服务器已安装的 `D:\software\Python314\python.exe`，其次使用经 `--version` 验证可用的 `python` 或 `py -3`。

也可以在 PowerShell 中运行：

```powershell
py -3 .\schedule_admin.py
```

生产本机 API 默认地址为：

```text
http://127.0.0.1:8080
```

可在界面顶部临时修改本机回环地址，或通过命令行覆盖：

```powershell
py -3 .\schedule_admin.py --api-base http://127.0.0.1:8080
```

也可设置环境变量：

```powershell
$env:SAP_RPA_SCHEDULE_API_BASE = "http://127.0.0.1:8080"
py -3 .\schedule_admin.py
```

优先级：`--api-base` 参数或程序传入值、环境变量、默认地址。管理工具只接受 `127.0.0.1`、`localhost` 或 `::1` 回环地址，并禁止在 URL 中包含用户名或密码；不能借此工具连接远程无鉴权管理接口。

## 删除规则

1. 先按离职人员姓名或人员编号筛选。
2. 核对创建人、更新人、任务名称、事务码和执行计划。
3. 多选需要删除的任务，点击“删除所选任务”。
4. 再次核对确认窗口中的任务清单后确认删除。
5. 查看结果中的“已同步移除排队任务”数量。

后端会删除定时任务配置及其尚未开始的排队任务。正在运行的任务不会被强制中断，已经运行或完成的历史记录会保留。

## 测试

测试使用 Python 标准库和临时本地 HTTP 服务，不会连接生产 API，也不会删除真实任务：

```powershell
py -3 -m unittest -v test_schedule_api.py
```

本工具仅依赖 Python 标准库：`tkinter`、`urllib`、`json`、`threading` 和 `unittest`。

## 重新构建 EXE

源码修改并通过测试后，双击 `构建本地EXE.cmd`。脚本使用专用构建环境 `D:\RPA\.tools\schedule-admin-builder`，输出到：

```text
D:\RPA\build\schedule-admin\dist\SAP定时任务集中管理.exe
```

构建产物复制到 `D:\RPA\管理工具\定时任务集中管理` 后才是正式本机版本。EXE 默认只连接本机 `http://127.0.0.1:8080`；不要把它作为无需鉴权的远程管理入口分发。
