# SAP RPA 本机执行器安装步骤

这个文件夹用于上线发给执行电脑。目标是让新电脑不需要打开源码、不需要手工改注册表，按顺序点击即可完成 `sap-rpa://` 协议安装和 SAP 登录配置。

## 文件说明

| 文件 | 用途 |
|---|---|
| `01_安装到本机.bat` | 复制 `SapWebLauncher` 到当前用户目录，并注册正式协议 `sap-rpa://` |
| `04_配置SAP登录信息.bat` | 在本机写入 SAP 登录配置，密码用当前 Windows 用户 DPAPI 加密，保存到 `%LOCALAPPDATA%\SapWebLauncher\config.json` |
| `02_检测环境.bat` | 检测协议注册、执行器自测、SAP GUI、SAP 登录配置是否存在 |
| `03_卸载协议和程序.bat` | 删除协议注册和本机安装目录 |
| `SapWebLauncher\` | 发布后的本机执行器程序，生成安装包后会自动放入 |
| `register_sap_rpa_current_user.reg` | 安装时自动生成的注册表备份文件 |

## 安装顺序

1. 把整个 `SapRpa上线安装包` 文件夹复制到目标电脑。
2. 双击 `01_安装到本机.bat`。
3. 双击 `04_配置SAP登录信息.bat`，按提示输入 SAP system、client、user、password、language、sysnr。密码不会写成明文。system/client/sysnr 必须按这台电脑实际 SAP Logon 配置填写，例如当前开发测试可填 `dev300`、`300`、`10`，正式电脑以实际为准。
4. 双击 `02_检测环境.bat`，确认协议、自测、SAP GUI、SAP 登录配置检测通过。
5. 打开 Netlify 网页，点击“只唤醒 SapWebLauncher”或“开始执行”测试。
6. 如果检测提示没有 SAP GUI，先安装 SAP GUI，并确认 SAP GUI Scripting 已启用。

## 普通用户电脑不需要 .NET SDK

上线包里的 `SapWebLauncher.exe` 是已经发布好的程序。普通执行电脑只需要安装 SAP GUI、配置 SAP 登录信息并注册 `sap-rpa://` 协议，不需要安装 .NET SDK，也不需要打开源码或编译程序。

只有开发/打包电脑需要 .NET 8 SDK，场景包括：

- 修改 `SapWebLauncher\Program.cs` 后重新生成 `SapWebLauncher.exe`
- 重新发布本机执行器程序
- 重新生成完整上线安装包

如果只是修改事务码 VBS，例如 `transactions\ZFI072A.vbs` 里的工厂清单或 SAP GUI Script 步骤，一般不需要 .NET SDK；重新运行 `01_安装到本机.bat` 或复制 VBS 到 `%LOCALAPPDATA%\SapRpaLauncher\transactions` 即可。

开发/打包机检测 SDK：

```powershell
dotnet --list-sdks
```

没有 SDK 时，安装 Microsoft .NET 8 SDK 后再编译执行器；普通用户电脑不要因为这个检测失败而中断安装。

## 安装后位置

执行器安装位置：

```text
%LOCALAPPDATA%\SapRpaLauncher
```

SAP 登录配置：

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

配置文件内的密码字段是 `passwordProtected`，由 Windows DPAPI 按当前 Windows 用户加密。它不能直接复制到其他 Windows 用户或其他电脑使用；新电脑上线时必须在那台电脑上重新运行 `04_配置SAP登录信息.bat`。

安全边界：DPAPI 保护的是配置文件静态存储，防止别人直接打开 `config.json` 看到明文密码。执行任务时，`SapWebLauncher` 仍需要在本机解密密码并交给 SAP GUI/SAP Shortcut 完成登录；日志不会记录明文密码，但同一 Windows 用户下的恶意程序仍可能截获运行时进程参数或内存。生产环境如果能上 SAP SSO/SNC，应优先用 SSO 降低密码传递风险。

协议注册位置：

```text
HKEY_CURRENT_USER\Software\Classes\sap-rpa
```

使用 HKCU 注册，不需要管理员权限。

旧的 `sap-zck://` 是临时测试协议，正式安装包不再注册。`03_卸载协议和程序.bat` 会顺手清理旧电脑上可能残留的 `sap-zck://`。

## 重新生成安装包

只在开发源码目录运行，不在最终上线包目录里运行：

```powershell
powershell -ExecutionPolicy Bypass -File "D:\工作\sap_rpa\上线安装包\scripts\make_package.ps1"
```

如果电脑执行策略限制未签名 `.ps1`，直接双击：

```text
D:\工作\sap_rpa\上线安装包\00_生成上线安装包.cmd
```

默认会生成：

```text
D:\工作\SapRpa上线安装包
```
