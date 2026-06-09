# SAP RPA Project AI Handoff

Last updated: 2026-06-09

This folder is for project handoff between computers, maintainers, and AI coding agents. It must not contain SAP passwords, Netlify tokens, GitHub tokens, OAuth tickets, or personal credentials.

## Access Control

This handoff document is safe to publish in the public repository because it does not contain SAP passwords, Netlify tokens, GitHub tokens, OAuth tickets, or personal credentials.

GitHub does not support password-protecting one folder inside a repository. If future materials include sensitive implementation notes or credentials, access must be controlled at repository level:

- Make the repository private before committing sensitive materials.
- Grant access only to maintainers who need the SAP RPA source.
- Do not commit local SAP login config, Netlify tokens, OAuth ticket files, logs, or generated secrets.
- If a future AI or developer needs credentials, configure them on that computer through the installer/config scripts, not through Git.

## Project Goal

This project provides a lightweight SAP automation portal:

1. A static web portal lets the user choose a transaction code and factory scope.
2. The page calls the local browser protocol `sap-rpa://`.
3. A Windows local launcher logs in to SAP GUI and runs the matching VBS automation script.
4. Netlify hosts the static page. Each execution computer installs the local launcher package.

The current priority is function completion, especially opening/running transaction-code scripts, not building a large scheduling or analytics system.

## Main Runtime Flow

```text
Netlify/index.html
  -> sap-rpa://run?tcode=...&script=...&factoryGroup=...&plants=...
  -> HKCU browser protocol registration
  -> %LOCALAPPDATA%\SapRpaLauncher\SapWebLauncher.exe
  -> %LOCALAPPDATA%\SapWebLauncher\config.json
  -> sapshcut starts SAP GUI
  -> SAP GUI Scripting runs transactions/<TCODE>.vbs
```

Important boundary:

- The web page must not send SAP account/password.
- The web page currently sends only transaction code, script file, factory group, and selected plants.
- Year/week/date and other transaction-specific query conditions should be handled inside each VBS script or its top parameter section.

## Key Directories

```text
D:\工作\sap_rpa\index.html
```

Static web portal. Published to Netlify. Also copied to `D:\工作\index.html` for local file preview.

```text
D:\工作\sap_rpa\网页启动登录\SapWebLauncher
```

C#/.NET 8 Windows launcher. It registers and handles `sap-rpa://`, reads local SAP login config, calls `sapshcut`, waits for SAP GUI, and runs VBS scripts.

```text
D:\工作\sap_rpa\网页启动登录\transactions
```

Transaction catalog and VBS scripts. Add or replace transaction automation here.

```text
D:\工作\sap_rpa\上线安装包
```

Installer source folder. Use it to generate the target-computer package under `D:\工作\SapRpa上线安装包`.

```text
D:\工作\设计文档
```

Architecture and design documents outside the repo. Update when implementation direction changes.

## Current Transaction Design

Transaction metadata exists in two places for now:

- Frontend list in `index.html` (`tCodes`, `factoryGroups`, `plantCatalog`).
- Launcher-side catalog in `网页启动登录\transactions\transaction-config.json`.

When adding a new transaction code, update both until the frontend is changed to load the JSON catalog directly.

Current transaction catalog includes:

```text
ZFI072A, ZFI085, ZFI014D, ZFI072N, ZFI057, ZCO020, ZPP063, ZPP063X,
ZFI019NC, ZFI080, ZFI019NI, ZCO019, ZFI019NA, ZFI019NL, ZFI080B, ZFI148
```

Implemented VBS placeholders:

- `ZFI019NL.vbs`
- `ZFI072A.vbs`

These scripts currently open the transaction and print calculated parameters. The real recorded SAP GUI actions should replace the `SAP 操作区` section while keeping the parameter calculation and session-waiting code.

## VBS Parameter Pattern

Recorded SAP Script usually contains hard-coded values. Standardize each VBS like this:

```vbscript
targetDate = DateAdd("d", -7, Date)
yearValue = Year(targetDate)
weekValue = DatePart("ww", targetDate, vbMonday, vbFirstFourDays)

' Replace recorded hard-coded values with variables:
' session.findById("wnd[0]/usr/txtGJAHR").Text = CStr(yearValue)
' session.findById("wnd[0]/usr/txtWEEK").Text = CStr(weekValue)
' session.findById("wnd[0]/usr/ctxtWERKS").Text = plantsCsv
```

Factory scope comes from the page as `plantsCsv` and `factoryGroup`. Other special fields can stay inside the transaction-specific VBS until a later version needs a unified parameter engine.

## Local SAP Login Configuration

On each execution computer, SAP login config is local:

```text
%LOCALAPPDATA%\SapWebLauncher\config.json
```

The password is stored as `passwordProtected` through Windows DPAPI for the current Windows user. It cannot be copied directly to another computer or another Windows account.

Configure it by running:

```text
D:\工作\SapRpa上线安装包\04_配置SAP登录信息.bat
```

Do not commit this config file or any real SAP password.

## Installer And Registry

The official browser protocol is:

```text
sap-rpa://
```

The old temporary `sap-zck://` test protocol should not be installed. The uninstall script can clean old leftovers.

Target computer install order:

1. Copy the whole generated `D:\工作\SapRpa上线安装包` folder to the computer.
2. Run `01_安装到本机.bat`.
3. Run `04_配置SAP登录信息.bat`.
4. Run `02_检测环境.bat`.
5. Open the Netlify page and execute a transaction.

Registry location:

```text
HKEY_CURRENT_USER\Software\Classes\sap-rpa
```

No administrator permission should be required because the protocol is registered under HKCU.

## Build And Verification

Recommended checks before release:

```powershell
$node='C:\Users\chen.kai6\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
& $node -e "const fs=require('fs'); const html=fs.readFileSync('D:/工作/sap_rpa/index.html','utf8'); const scripts=[...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m=>m[1]); for(const s of scripts)new Function(s); console.log('ok', scripts.length);"

$dotnet = Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8_421\dotnet.exe'
& $dotnet build 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj' -c Release

$exe='D:\工作\sap_rpa\网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe'
& $exe test

powershell -ExecutionPolicy Bypass -File 'D:\工作\sap_rpa\上线安装包\scripts\make_package.ps1'
```

## Release Rules

GitHub remote:

```text
https://github.com/ckstock/sap_rpa
```

Netlify production site:

```text
https://hilarious-mandazi-8c2dc7.netlify.app
Site ID: c6bc6d81-cf7f-4776-95f2-30356a6f342e
```

Release sequence:

1. Run local validation.
2. Commit only intended source/docs changes.
3. Push `main` to GitHub after user approval.
4. Deploy to the existing Netlify site, not a new site.
5. Verify the live URL with a cache-busting query string.

User preference: after long editing sessions, ask before committing; roughly every four hours is acceptable. If the user explicitly says to publish, commit/push/deploy can proceed.

## Current Known Gaps

- `ZFI019NL.vbs` and `ZFI072A.vbs` still need real recorded SAP GUI steps inserted into the `SAP 操作区`.
- Most transaction codes are cataloged but use `openOnly` until their VBS scripts are recorded and added.
- Frontend transaction metadata and `transaction-config.json` should eventually be unified to avoid double maintenance.
- Netlify is static hosting. It should not store frequently changed factory data or secrets. If server-side storage is required later, add a real backend or a managed data service.
