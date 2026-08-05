# SAP 自动化执行门户

V2 本地 API + 网关门户，用于从网页发起 SAP 事务码自动化执行。正式入口通过服务器网关访问，后端由运行目录 `D:\RPA\bin\SapWebLauncher.exe --serve` 提供 API、队列、SQLite、SAP GUI/VBS 执行和钉钉通知。

当前重点：

- 工作区源码目录是 `D:\RPA\RpaProject`，服务器运行目录是 `D:\RPA`。
- `ZFI019NL` 周损益执行链路展示。
 - 含“保存”的 ALV 导出已按周、工厂两层归档：Excel 输出根目录由 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 或环境变量 `SAP_RPA_ALV_EXPORT_DIR` 控制；默认 `D:\RPA\临时文件\文件数据`。工厂型事务按 `yyyy_WKnn\工厂` 分目录，目录已存在则复用；跨月等同一工厂多窗口只在该工厂目录内合并成一个最终 Excel，不生成跨工厂总表。业务范围型事务先导出原始 ALV，再读取 Excel 中的 `WERKS`/`Plant Code`/`工厂`/`工厂号`/`工厂代码`/`大BU-工厂`/`业务范围-小厂` 等工厂字段，按字段值拆分到各工厂目录；拆分依据只允许使用导出 Excel 的工厂列实际值。只有表头或无工厂数据行时视为本业务范围无导出数据，程序会删除 raw 文件、业务范围子目录和空的 `_raw_business_area` 根目录，不登记无用文件且不把该情况当技术失败；有数据但缺工厂列或工厂值为空时任务失败并保留 raw 文件，按 ALV 布局/导出结果排查，不得用 `ZTSD001`、SQLite `plants`、`config.local.json`、业务范围入参或本地映射推断。
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
