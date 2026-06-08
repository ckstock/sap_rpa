# SAP RPA 本机执行器安装步骤

这个文件夹用于上线发给执行电脑。目标是让新电脑不需要打开源码、不需要手工改注册表，按顺序点击即可完成 `sap-rpa://` 协议安装。

## 文件说明

| 文件 | 用途 |
|---|---|
| `01_安装到本机.bat` | 复制 `SapWebLauncher` 到当前用户目录，并注册 `sap-rpa://`、`sap-zck://` |
| `02_检测环境.bat` | 检测协议注册、执行器自测、SAP GUI 是否存在 |
| `03_卸载协议和程序.bat` | 删除协议注册和本机安装目录 |
| `SapWebLauncher\` | 发布后的本机执行器程序，生成安装包后会自动放入 |
| `register_sap_rpa_current_user.reg` | 安装时自动生成的注册表备份文件 |

## 安装顺序

1. 把整个 `SapRpa上线安装包` 文件夹复制到目标电脑。
2. 双击 `01_安装到本机.bat`。
3. 双击 `02_检测环境.bat`，确认协议、自测、SAP GUI 检测通过。
4. 打开 Netlify 网页，点击“只唤醒 SapWebLauncher”或“开始执行”测试。
5. 如果检测提示没有 SAP GUI，先安装 SAP GUI，并确认 SAP GUI Scripting 已启用。

## 安装后位置

安装脚本会把执行器复制到：

```text
%LOCALAPPDATA%\SapRpaLauncher
```

协议注册位置：

```text
HKEY_CURRENT_USER\Software\Classes\sap-rpa
HKEY_CURRENT_USER\Software\Classes\sap-zck
```

使用 HKCU 注册，不需要管理员权限。

## 重新生成安装包

在开发电脑上运行：

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
