# SAP RPA Project AI Handoff

Last updated: 2026-07-29

This folder is for project handoff between computers, maintainers, and AI coding agents. It must not contain SAP passwords, DingTalk secrets, GitHub tokens, OAuth tickets, certificates, SQLite databases, logs, exported Excel files, or personal credentials.

## Current Authority

The current project authority is no longer the old Netlify + `sap-rpa://` handoff flow. Use these files first:

- `AI_HANDOFF_NEXT.md`: latest AI handoff and current risk notes.
- `README.md`: current project entry summary.
- `SapRpa_V2_功能说明书.html`: user-visible behavior and operation guide.
- `SAP_直接调用清单.md`: SAP table/report/function/VBS direct-call inventory.
- `上线安装包/上线安装文档清单.md`: production installation checklist.
- `上线安装包/生产部署拷贝清单.md`: production copy, local-state preservation, and certificate handling checklist.
- `上线安装包/README_安装步骤.md`: manual installation and upgrade guide.
- `上线安装包/最终上线部署步骤/README_V2_Windows_Server_上线部署.md`: Windows Server runbook.

## Runtime Model

Current V2 runtime:

```text
https://fi_automation.srv.lstech.com/rpa/
  -> D:\RPA\gateway\rpa-gateway.js
  -> http://127.0.0.1:8080/api/*
  -> D:\RPA\bin\SapWebLauncher.exe --serve
  -> D:\RPA\data\sap-rpa-config.db
  -> D:\RPA\transactions\*.vbs
  -> SAP GUI / SAP NCo / DingTalk OpenAPI
```

The HTTP compatibility URL `http://10.0.41.158:6174/rpa/` is kept for intranet troubleshooting. Do not document it as the primary production URL.

`sap-rpa://` is only a historical compatibility path. It is not the production user entry for the current server deployment.

## Directory Boundaries

- Source repository: `D:\RPA\RpaProject`
- Runtime root: `D:\RPA`
- Backend executable: `D:\RPA\bin\SapWebLauncher.exe`
- Runtime frontend assets: `D:\RPA\assets`
- Runtime VBS/catalog: `D:\RPA\transactions`
- Runtime SQLite: `D:\RPA\data\sap-rpa-config.db`
- Runtime logs: `D:\RPA\logs`
- Runtime gateway: `D:\RPA\gateway`
- Runtime startup scripts: `D:\RPA\启动脚本`
- Real local config: `D:\RPA\config.local.json`
- Config template: `D:\RPA\config.local.example.json`
- HTTPS certificate directory: `D:\RPA\certs\lstech.com`

After any source change that affects frontend, transactions, gateway, scripts, docs, or backend code, copy or publish to the runtime directory before testing the public URL. A green source build does not prove the deployed server is running the new version.

## Local Configuration

Production-specific values stay on the machine:

- SAP GUI login config: `%LOCALAPPDATA%\SapWebLauncher\config.json`, protected by Windows DPAPI for the current Windows user.
- DingTalk and SAP NCo config: `D:\RPA\config.local.json`.
- Certificates under `D:\RPA\certs\lstech.com`.
- SQLite, logs, and exported Excel output under `D:\RPA`.

Do not commit these runtime-private files.

## Production Copy Rule

`D:\RPA` can be copied to a production server as a program package, but production-local state must not be overwritten by test-server state. Program files include `index.html`, `assets`, `gateway`, `启动脚本`, `bin`, `transactions`, `依赖\SapNco`, `config.local.example.json`, and docs.

Preserve or recreate production-local state on the target machine:

- `D:\RPA\config.local.json`
- `D:\RPA\data\sap-rpa-config.db`
- `D:\RPA\logs\`
- `D:\RPA\outputs\`
- `D:\RPA\certs\lstech.com\`
- `%LOCALAPPDATA%\SapWebLauncher\config.json`

`D:\RPA\certs` is not technically install-only; it can be copied only as controlled production certificate backup/migration material. It must not be included in GitHub, public zip packages, normal program packages, or plaintext chat attachments. After certificate copy or replacement, reset ACLs and verify HTTPS.

The ALV Excel output root is controlled by:

1. Environment variable `SAP_RPA_ALV_EXPORT_DIR`, if set.
2. `D:\RPA\config.local.json` key `fileStorage.alvExportDataDirectory`.
3. Default `D:\RPA\临时文件\文件数据`.

For production migration to a network share or another data disk, change `fileStorage.alvExportDataDirectory` in the real runtime config and restart only this project backend.

## Current Transaction Entry Set

The workstation page and execution/schedule dropdowns should expose only the current workbench cards:

- 采购价: `ZFI072A` -> `ZFI072N`
- 产值拆分: `ZFI057`
- 实际领料: `ZFIR034` -> `ZFI080` -> `ZFI080B`
- 标准价: `ZCO019`
- 周结完工成本明细表: `ZFI019NL`, `ZFI019NA`, `ZFI148`

Current production ALV save/export commitment is limited to these 7 transaction codes:

```text
ZFI072A, ZFI072N, ZFI080, ZFI080B, ZCO019, ZFI019NA, ZFI019NL
```

`ZFI019NI` is an old residual catalog entry from the early design. It has no production VBS and no current workbench entry. Do not reintroduce it into the frontend fallback, default `transaction-config.json`, execution dropdown, schedule dropdown, or save/export checklist.

## ALV Export Rules

Factory-type save transactions write one Excel file per factory:

```text
<alvExportDataDirectory>\yyyy_WKnn_工厂\事务码_卡片名称_工厂工厂号_yyyyMMddHHmmss.xlsx
```

If one factory produces multiple windows, such as cross-month date windows, merge only that factory's part files into one final factory Excel and delete the part files. Do not create a cross-factory parent workbook.

Business-area save transactions first export a raw workbook, then split by the actual factory column in the workbook. Supported headers include `WERKS`, `Plant Code`, `工厂`, `工厂号`, `工厂代码`, `大BU-工厂`, and `业务范围-小厂`.

Do not infer factory from `ZTSD001`, SQLite plant config, local JSON config, business-area input, or static mapping when splitting Excel. If the raw workbook has only headers or no factory data rows, clean the raw file and empty `_raw_business_area` folders and treat it as no data. If there are business rows but no usable factory column or factory value, fail and keep the raw workbook for diagnosis.

## Verification Baseline

Minimum verification after deployment or upgrade:

```powershell
& "D:\RPA\bin\SapWebLauncher.exe" test
Invoke-RestMethod "http://127.0.0.1:8080/api/health"
curl.exe --ssl-no-revoke -s -o NUL -w "HTTPS portal %{http_code}\n" https://fi_automation.srv.lstech.com/rpa/
curl.exe --ssl-no-revoke -s -o NUL -w "HTTPS API %{http_code}\n" https://fi_automation.srv.lstech.com/rpa/api/health
curl.exe -s -o NUL -w "HTTP portal %{http_code}\n" http://10.0.41.158:6174/rpa/
```

Health checks are necessary but not sufficient. Before production release, submit a controlled transaction from the real page and verify:

- SQLite has the run and logs.
- SAP GUI session handling matches expectation.
- DingTalk send log appears when configured.
- Save/export transaction output lands under `fileStorage.alvExportDataDirectory`.
- The enabled transaction API and frontend dropdowns do not expose `ZFI019NI`.

## Git Rules

Use `git add -- <explicit files>`. Do not use `git add .` in this repository. Keep real `config.local.json`, SQLite, logs, Excel output, certificates, SAP DPAPI config, and private packages out of Git.
