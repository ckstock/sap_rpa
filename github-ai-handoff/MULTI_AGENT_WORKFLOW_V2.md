# SAP RPA V2 Multi-Agent Workflow

Last updated: 2026-06-11

This document defines how Codex agents should collaborate on SAP RPA V2 work in this repository. It is intentionally kept in the source repository so the rule travels with GitHub branches and future company-server migrations.

## Core Rules

1. For complex feature work, the main agent should split implementation across multiple concrete workers, not only spawn read-only explorers.
2. Each worker must own a disjoint file set before editing starts.
3. Workers must not revert or overwrite changes made by another worker.
4. Runtime artifacts are not source deliverables: do not commit local SQLite databases, logs, exported SAP files, credentials, webhook secrets, or tokens.
5. SAP GUI automation remains serialized by default. Do not introduce parallel SAP GUI script execution against the same interactive desktop session.
6. Browser pages must not collect, store, or receive SAP passwords.
7. Company-server migration must preserve the runtime/source split: source in GitHub, machine-specific config on the target Windows user profile.

## Recommended Roles

| Role | Primary responsibility | Default write scope |
| --- | --- | --- |
| Main agent | Task decomposition, API contract decisions, merge review, final acceptance | Any file only after ownership is clear |
| Explorer | Read-only impact analysis for specific unknowns | No writes |
| Worker A | Backend, local API, SQLite schema, queue, launcher behavior | `网页启动登录/SapWebLauncher/**`, future backend docs/contracts |
| Worker B | Frontend pages, UI interaction, API consumption | `index.html`, future extracted frontend assets |
| Worker C | VBS scripts, SAP transaction parameters, script catalog | `网页启动登录/transactions/**` |
| QA agent | Verification, regression review, sensitive-data checks | No writes unless explicitly assigned a fix |

## File Ownership For Current Repo

| Path | Owner | Notes |
| --- | --- | --- |
| `index.html` | Worker B | Main portal page. Keep API calls aligned with backend contract. |
| `网页启动登录/SapWebLauncher/Program.cs` | Worker A | Current backend/API/launcher monolith. Prefer extracting later before heavy parallel work. |
| `网页启动登录/transactions/*.vbs` | Worker C | Transaction scripts. Keep standard output keys stable. |
| `网页启动登录/transactions/transaction-config.json` | Worker C | Transaction catalog. Coordinate with Worker B when UI depends on fields. |
| `github-ai-handoff/*.md` | Main agent / Worker A | Collaboration, PR, and handoff documents. |
| `上线安装包/**` | Main agent / Worker A | Deployment packaging. Coordinate with backend changes. |
| `data/**`, `logs/**`, `outputs/**` | No source owner | Runtime-only. Do not commit. |

## When A Task Starts

The main agent should assign workers using this template:

```text
Role:
Task:
Write scope:
Read-only references:
Do not modify:
Acceptance checks:
Report required:
- Changed files
- Summary
- Verification commands/results
- Risks or manual confirmation points
```

## Contract Change Flow

Use this flow when changing API fields, database fields, or VBS input/output:

1. Main agent states the contract change and affected surfaces.
2. Backend owner updates API/database behavior.
3. Frontend owner consumes only documented fields.
4. VBS owner keeps script input and output names stable.
5. QA verifies success path, failure path, and migration risk.

For database changes, the report must include:

```text
Tables changed:
Fields added/changed/removed:
Migration impact:
Compatibility with existing SQLite files:
API impact:
Frontend impact:
Rollback approach:
```

## Current V2 Direction

The current V2 direction is:

1. SQLite is the local runtime source of truth for run history and, in later work, configurable transaction metadata.
2. The frontend should read configuration through the local API rather than hardcoding factory lists.
3. VBS scripts should receive execution parameters from the launcher and should not hardcode fixed factory scope when the transaction supports plant input.
4. `ZFI072A` is the first end-to-end proving transaction.
5. DingTalk or other notification robots should be configured without exposing webhook secrets to frontend code.

## Current Practical Split For The Next Feature

For the next "database-backed configuration" feature, split work like this:

| Worker | Concrete task | Write scope |
| --- | --- | --- |
| Worker A | Add SQLite tables/repository/API for factories, transaction factory rules, and notification robots | Backend/API files only |
| Worker B | Build Basic Config UI with tabs: factory config, transaction config, notification robot | Frontend files only |
| Worker C | Update `ZFI072A.vbs` to consume passed `plants` and keep output keys stable | VBS/catalog files only |
| QA | Test empty DB, seeded DB, add/edit/delete config, run creation, disabled/missing factory rules, secret leakage | Read-only report |

## Minimum Acceptance Output

The final answer for multi-agent work should include:

1. Change summary.
2. Worker file list.
3. Verification commands.
4. Test results.
5. Manual confirmation points.
6. Any known risks before moving to the company server.
