# SAP 自动化执行门户

V2 本地 API + 网关门户，用于从网页发起 SAP 事务码自动化执行。正式入口通过服务器网关访问，后端由运行目录 `D:\RPA\bin\SapWebLauncher.exe --serve` 提供 API、队列、SQLite、SAP GUI/VBS 执行和钉钉通知。

当前重点：

- 工作区源码目录是 `D:\RPA\RpaProject`，服务器运行目录是 `D:\RPA`。
- `ZFI019NL` 周损益执行链路展示。
- **2026-08-06 组织归档规则优先于下方历史周/工厂说明：**保存类 ALV 由 SAP GUI 先写本机暂存，后端按导出 Excel 的实际 `WERKS` 或 `GSBER` 值只读查询 SAP 映射表。`ZFI080`、`ZFI080B`、`ZFI019NL`、`ZFI019NA`、`ZFI148` 使用 `ZTFI48A-GSBER -> ZBU/ZSBU`；其他保存类使用 `ZTFI48B-WERKS -> ZBU/ZSBU`。命中时最终文件为 `<alvExportDataDirectory>\<ZBU>\<ZSBU>\yyyy_WKnn\<事务码>_<卡片名称>_WKnn.xlsx`，相同组织、周和事务码合并，一厂多组织各写一份；无有效映射才回退 `<alvExportDataDirectory>\集采工厂\yyyy_WKnn`。业务范围型 Excel 只要有实际 `WERKS` 列也使用 `_工厂<WERKS>` 后缀，只有没有工厂列时才使用 `_业务范围<GSBER>`。重跑按来源键替换旧行，映射查询失败或 Excel 缺必要列时任务失败并保留暂存文件。
- 含“保存”的 ALV 导出由公共组织归档能力处理：Excel 输出根目录由 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 或环境变量 `SAP_RPA_ALV_EXPORT_DIR` 控制；SAP GUI 始终只写 `D:\RPA\临时文件\ALV本地暂存`。后端从 Excel 实际 `WERKS` 或 `GSBER` 值查 SAP 映射，按 `ZBU\ZSBU\yyyy_WKnn` 归档并合并相同组织、周和事务码的数据；无映射走 `集采工厂\yyyy_WKnn`。业务范围型事务按 Excel 的业务范围列路由，不能用 `ZTSD001`、SQLite `plants`、`config.local.json` 或页面入参推断。只有表头或无来源数据行时清理 raw 文件且不判技术失败；有数据但缺必需来源列或映射/网络归档失败时任务失败并保留本机暂存。
- 正式用户入口是 `https://fi_automation.srv.lstech.com/rpa/`；HTTP 兼容排障入口按目标服务器 IPv4 生成，格式为 `http://<服务器IP>:6174/rpa/`。当前联调服务器是 `10.0.41.158`，正式系统 IP 是 `10.0.2.120`。
- `sap-rpa://` 只作为兼容/历史唤醒入口排查，不是当前生产用户主入口。
- SAP 系统、客户端、账号和密码只保存在本机执行器配置中，网页只传事务码、工厂和业务范围等业务参数。

运维与排查入口：

- 安装、升级、重启和验收按 `上线安装包/README_安装步骤.md` 执行；后端改动必须 `dotnet publish` 后完整复制 publish 输出到 `D:\RPA\bin`，不能只替换 `SapWebLauncher.exe`。
- 直接调用 SAP 的表、函数、NCo/RFC、VBS 清单见 `SAP_直接调用清单.md`。

本机启动器源码在本地目录：

```text
D:\RPA\RpaProject\网页启动登录
```
