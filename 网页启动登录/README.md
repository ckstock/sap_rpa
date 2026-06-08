# SapWebLauncher - 网页启动 SAP 登录和事务码脚本

本项目用于从网页唤醒本机程序，自动登录 SAP GUI，打开指定事务码，并按事务码脚本模板执行 SAP GUI Scripting。

当前推荐协议是 `sap-rpa://`，同时保留旧版 `sap-zck://` 兼容入口。

## 当前版本重点

- 先打通 `ZFI019NL` 最新版本事务码登录，并支持从网页传入多工厂、多业务范围参数。
- 所有事务码统一通过 `tcode` 参数进入 SAP。
- 后续每个事务码只需要新增或配置对应 VBS 脚本模板。
- 协议唤醒模式只能启动本机程序，不能从浏览器直接拿执行结果；实时状态回传需要 Bridge 常驻服务。

## 项目结构

```text
网页启动登录\
  SapWebLauncher.sln
  SapWebLauncher\
    SapWebLauncher.csproj
    Program.cs
    transaction_template.vbs
    zck_template.vbs              # 旧版参考模板，不再嵌入
    app.manifest
  register.ps1
  register_sap_rpa.reg
  test.html
```

## 编译

```powershell
dotnet build "D:\工作\网页启动登录\SapWebLauncher\SapWebLauncher.csproj" -c Release
```

Release 输出路径：

```text
D:\工作\网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe
```

## 注册协议

推荐用 PowerShell 注册，会自动找到 Release 或 Debug exe：

```powershell
powershell -ExecutionPolicy Bypass -File "D:\工作\网页启动登录\register.ps1"
```

也可以双击导入：

```text
D:\工作\网页启动登录\register_sap_rpa.reg
```

`.reg` 文件写死到 Release exe 路径，导入前要先完成 Release 编译。路径包含中文目录时，优先使用仓库中的 Unicode 版 `.reg` 文件或直接执行 `register.ps1`。

## URL 示例

打开 ZFI019NL：

```text
sap-rpa://run?action=run&tcode=ZFI019NL&script=openOnly&system=Fiori&client=400&user=UI5035&pw=fiori666&sysnr=04&lang=ZH&plant=1022&plants=1022,1024,1032,6041&businessArea=2900&businessAreas=2900,9200,2800,3960&factoryGroup=PINGHU_30&runStrategy=byPlant
```

旧 ZCK 验证入口仍兼容：

```text
sap-zck://action=run&system=Fiori&client=400&user=UI5035&pw=fiori666&sysnr=04
```

## 参数说明

| 参数 | 说明 | 默认值 |
|---|---|---|
| `action` | 固定 `run` | `run` |
| `tcode` | SAP 事务码 | `ZFI019NL` |
| `script` | 脚本模板模式，当前支持 `openOnly`、`zck` | `openOnly` |
| `system` | SAP 系统名 | `Fiori` |
| `client` | SAP Client | `400` |
| `user` | SAP 用户名 | `UI5035` |
| `pw` | SAP 密码 | `fiori666` |
| `lang` | SAP 语言 | `ZH` |
| `sysnr` | SAP 实例编号 | `04` |
| `plant` | 兼容旧脚本的首个工厂 | 空 |
| `plants` | 多工厂 CSV，例如 `1022,1024,1032,6041` | 空 |
| `businessArea` | 兼容旧脚本的首个业务范围 | 空 |
| `businessAreas` | 多业务范围 CSV，例如 `2900,9200,2800,3960` | 空 |
| `factoryGroup` | 厂区组，例如 `PINGHU_30` | 空 |
| `runStrategy` | 执行策略，`byPlant` 或 `merged` | 空 |
| `period` | 核算周期/周别，后续脚本可读取 | 空 |
| `weekEnd` | 周结日期，后续脚本可读取 | 空 |

## 下一步加事务码

1. 先用 SAP GUI Scripting Recorder 录制该事务码完整动作。
2. 在 `transaction_template.vbs` 增加一个 `scriptMode` 分支，或新建专用模板。
3. 在网页事务码配置中把 `script` 改成对应模板名。
4. 用 `test.html` 先单独验证登录、跳事务码、字段填充、保存/导出。

## 日志

本地日志路径：

```text
%LOCALAPPDATA%\SapWebLauncher\launcher.log
```
