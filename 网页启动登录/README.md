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
- 协议唤醒模式只能启动本机程序，不能从浏览器直接拿执行结果；当前通过本地日志闭环，后续再扩展状态回传。

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
%LOCALAPPDATA%\SapWebLauncher\launcher.log
```
