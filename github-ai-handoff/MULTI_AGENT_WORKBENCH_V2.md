# SAP RPA V2 Multi-Agent Workbench

Updated: 2026-06-11

This is the GitHub-tracked operation board for SAP RPA V2 multi-agent work. It complements `MULTI_AGENT_WORKFLOW_V2.md` with practical commands and split patterns.

## How The User Can Ask

```text
按多 agent 模式做基础配置：数据库做主配置源，页面从 API 读，VBS 只接收执行器传入的 plants。最后给我变更摘要、验证命令、测试结果、人工确认点。
```

```text
按多 agent 模式继续 ZFI072A：worker A 做后端/API/SQLite，worker B 做页面，worker C 做 VBS，QA 做检查。
```

The user does not need to manually create every sub-agent. The main agent should create workers when the task is complex enough.

## Main Agent Operating Rules

1. Read `D:\sap_ai\README_FOR_AI.md` first.
2. Read `D:\sap_ai\MULTI_AGENT_WORKBENCH.md` or this tracked copy before splitting work.
3. For complex implementation, spawn concrete workers, not only explorers.
4. Assign each worker a separate write scope.
5. Keep the immediate blocking task local; delegate side tasks that can progress in parallel.
6. Do not let two workers edit the same file at the same time.
7. Integrate worker outputs and run final verification locally.

## Default Worker Split

| Agent | Purpose | Writes |
| --- | --- | --- |
| Main agent | Architecture, task split, merge, final answer | Only after owner is clear |
| Explorer | Read-only code impact and risk questions | No writes |
| Worker A | Backend, API, SQLite, migration design | `D:\工作\sap_rpa\网页启动登录\SapWebLauncher\**` |
| Worker B | Frontend, page style, interaction, API calls | `D:\工作\sap_rpa\index.html`, future frontend assets |
| Worker C | VBS, transaction scripts, script config | `D:\工作\sap_rpa\网页启动登录\transactions\**` |
| QA | Tests, regression, boundary cases, sensitive-data scan | No writes by default |

## Current Feature Split: Basic Config

| Agent | Task | Acceptance |
| --- | --- | --- |
| Worker A | Add SQLite schema/repository/API for `plants`, plant groups, transaction factory rules, notification robots | Empty DB init works; old run history remains readable; no secrets exposed |
| Worker B | Build three tabs: factory config, transaction config, notification robot | UI can add/edit/delete; page reads API; no hardcoded factory source |
| Worker C | Update `ZFI072A.vbs` and script config so plants come from launcher input | VBS no longer owns fixed plant scope; standard output keys remain |
| QA | Verify empty DB, seeded DB, CRUD, run creation, disabled/missing config, sensitive info | Report pass/fail with exact commands |

## Backend Data Model Direction

Recommended future tables:

```text
plants
plant_groups
plant_group_members
transactions
transaction_plant_rules
notification_robots
notification_robot_bindings
runs
run_logs
run_files
app_settings
schema_migrations
```

Design principle:

1. Database is the source of truth for configurable business scope.
2. Page reads configuration through API.
3. VBS receives runtime parameters from the launcher.
4. Secrets are configured on the Windows server side, not in frontend code.
5. Keep migration fields such as `created_at`, `updated_at`, `created_by`, `updated_by`, `is_active`.

## Branch And PR Rules

Use feature branches:

```text
codex/v2-<feature-name>
```

Examples:

```text
codex/v2-local-api-sqlite
codex/v2-basic-config
codex/v2-zfi072a-db-config
```

Before PR:

1. `git status --short`
2. Inspect diff.
3. Run build/syntax/API checks.
4. Do not commit runtime DB/log/output files.
5. Push the branch and open PR to `main`.

## Final Answer Format

Every multi-agent task final answer should include:

```text
Change summary:

Worker file list:

Verification commands:

Test results:

Manual confirmation:
```
