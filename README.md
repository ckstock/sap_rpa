# SAP 自动化执行门户

静态前端门户，用于从网页发起 SAP 事务码自动化执行。

当前重点：

- 工作区源码目录是 `D:\RPA\RpaProject`，服务器运行目录是 `D:\RPA`。
- `ZFI019NL` 周损益执行链路展示。
- 含“保存”的 ALV 导出已按工厂归档：Excel 输出根目录由 `D:\RPA\config.local.json` 的 `fileStorage.alvExportDataDirectory` 或环境变量 `SAP_RPA_ALV_EXPORT_DIR` 控制；默认 `D:\RPA\临时文件\文件数据`。工厂型事务按 `yyyy_WKnn_工厂` 分目录，目录已存在则复用；跨月等同一工厂多窗口只在该工厂目录内合并成一个最终 Excel，不生成跨工厂总表。业务范围型事务先导出原始 ALV，再读取 Excel 中的 `WERKS`/`Plant Code`/`工厂`/`工厂号`/`工厂代码` 等工厂字段，按字段值拆分到各工厂目录；拆分依据只允许使用导出 Excel 的工厂列实际值，缺列或工厂值为空应按 ALV 布局/导出结果排查，不得用 `ZTSD001`、SQLite `plants`、`config.local.json`、业务范围入参或本地映射推断。
- 通过 `sap-rpa://` 唤醒本机 `SapWebLauncher`。
- 正式协议只保留 `sap-rpa://`。
- SAP 系统、客户端、账号和密码只保存在本机执行器配置中，网页只传事务码、工厂和业务范围等业务参数。

运维与排查入口：

- 安装、升级、重启和验收按 `上线安装包/README_安装步骤.md` 执行；后端改动必须 `dotnet publish` 后完整复制 publish 输出到 `D:\RPA\bin`，不能只替换 `SapWebLauncher.exe`。
- 直接调用 SAP 的表、函数、NCo/RFC、VBS 清单见 `SAP_直接调用清单.md`。

本机启动器源码在本地目录：

```text
D:\RPA\RpaProject\网页启动登录
```
