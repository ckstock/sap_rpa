# PR: V2 local API, SQLite runtime, and multi-agent workflow

Branch: `codex/v2-local-api-sqlite`

Target branch: `main`

## Summary

This PR moves SAP RPA V2 toward a local API plus SQLite runtime model, while keeping the browser free of SAP credentials.

Included work:

1. Add V2 local API server behavior and serialized execution queue.
2. Store run history in SQLite under the runtime root.
3. Move local runtime files under `D:\sap_ai` by default or `SAP_RPA_HOME` when provided.
4. Add a dry-run switch for queue execution.
5. Show disabled queue state on the portal.
6. Add SQLite schema/table inspection API for local troubleshooting.
7. Add the multi-agent collaboration workflow used for future V2 development.

## Important Runtime Paths

```text
Runtime root: D:\sap_ai
SQLite DB:    D:\sap_ai\data\sap-rpa-config.db
Logs:         D:\sap_ai\logs\launcher.log
VBS scripts:  D:\sap_ai\transactions
```

Do not commit runtime DB/log/output files. They are machine-local and should be recreated or migrated intentionally when moving to the company server.

## Verification Already Performed

```powershell
& 'C:\Users\chen.kai6\AppData\Local\CodexDotnetSdk8_421\dotnet.exe' build 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\SapWebLauncher.csproj' -c Release
```

Result: passed.

```powershell
& 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe' --init-db
```

Result: initialized SQLite under `D:\sap_ai\data\sap-rpa-config.db`.

```powershell
& 'D:\工作\sap_rpa\网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe' test
```

Result: 7 checks passed.

```powershell
Invoke-RestMethod http://127.0.0.1:17890/api/health
Invoke-RestMethod http://127.0.0.1:17890/api/schema
Invoke-RestMethod 'http://127.0.0.1:17890/api/schema/tables/runs?limit=1'
```

Result: health/schema/table preview endpoints responded successfully.

```powershell
& 'C:\Users\chen.kai6\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' -e "const fs=require('fs'); for (const f of ['D:/sap_ai/index.html','D:/sap_ai/db-viewer.html','D:/sap_ai/config-prototype.html']) { const html=fs.readFileSync(f,'utf8'); for (const m of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new Function(m[1]); console.log('ok', f); }"
```

Result: local runtime pages parsed successfully.

## Example Successful Run

```text
runId: RUN-20260610173054-ZFI072A-06b225b50a8d447e90e6569476123
tcode: ZFI072A
status: success
duration: 5721ms
message: INFO: transaction script executed
```

## Manual Confirmation Before Merge/Deployment

1. Confirm this branch belongs to the second version line, not the old static-only baseline.
2. Confirm the company server will run the launcher in an interactive Windows desktop session, not Session 0 service-only mode.
3. Confirm SAP GUI scripting is enabled on the target machine.
4. Confirm SAP login config is created locally on the company server through the installer/config script.
5. Confirm DingTalk robot webhook and secret storage approach before enabling real notifications.
6. Confirm the next PR should make database-backed factory/transaction configuration the source of truth for the page and VBS parameters.

## Multi-Agent Plan For Next PR

Use `github-ai-handoff/MULTI_AGENT_WORKFLOW_V2.md` as the working rule.

Suggested split:

1. Worker A: backend/API/SQLite configuration tables.
2. Worker B: Basic Config UI tabs and CRUD interaction.
3. Worker C: `ZFI072A` VBS parameter consumption and standard outputs.
4. QA: regression, schema migration, and sensitive-data checks.
