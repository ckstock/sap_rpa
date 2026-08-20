# SAP RPA V2 数据库字段设计

更新时间：2026-06-24

本文档用于交接 SAP RPA V2 的 SQLite 字段设计。当前数据库仍以 `SapWebLauncher` 内置迁移为准；本文档同时记录下一步“可编排 SAP 报表链路”需要新增的表和字段，方便后续 AI 或开发人员直接落地迁移。

## 设计原则

1. SQLite 是 V2 的本地主配置源和运行审计库。
2. 页面只通过本地 API 读写配置，不直接访问 SQLite。
3. SAP GUI/VBS 串行执行；高并发请求先入队，数据库记录排队、执行、完成、失败和重跑状态。
4. 大结果文件不塞进 URL、命令行或 VBS 参数，只传 `result_id` 或 `result_file`。
5. 敏感信息不得明文入库：SAP 密码、真实 appSecret、token、私有服务器地址、个人密钥必须放在 DPAPI、Windows 凭据、环境变量或本地未提交配置中。
6. 所有配置类表保留迁移友好字段：`created_at`、`updated_at`、`created_by`、`updated_by`、`enabled/is_active`、`sort_order`。

## 当前已实现表

### `schema_migrations`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `version` | INTEGER PK | 已执行迁移版本。 |
| `applied_at` | TEXT | 执行时间。 |

### `app_settings`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `setting_key` | TEXT PK | 设置项键。 |
| `setting_value` | TEXT | 设置值。不得保存明文密码或真实 secret。 |
| `updated_at` | TEXT | 更新时间。 |

当前常用键：`sap_password_storage`、`queue_mode`、`runtime_root`、`script_root`、`output_root`、`database_path`、`credential_config_path`。

### `transactions`

事务码主数据。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `tcode` | TEXT PK | 事务码，例如 `ZFI072A`。 |
| `name` | TEXT | 中文名称。 |
| `stage` | TEXT | 阶段/分类，例如并行启动、采购价月表。 |
| `script_file` | TEXT | VBS 文件名。 |
| `icon` | TEXT | 页面图标或分类标识。 |
| `params_json` | TEXT JSON | 页面参数定义。 |
| `factory_rule` | TEXT | 工厂规则说明。 |
| `fixed_plants_json` | TEXT JSON | 固定工厂列表。 |
| `default_group` | TEXT | 默认业务范围/组。 |
| `automation` | TEXT | 执行方式标识。 |
| `timeout_seconds` | INTEGER | 单次事务超时秒数；`0` 表示使用默认值。 |
| `retry_count` | INTEGER | 自动重试次数。 |
| `script_version` | TEXT | 脚本版本。 |
| `script_hash` | TEXT | 脚本 hash，用于审计。 |
| `script_metadata_json` | TEXT JSON | 脚本元数据。 |
| `enabled` | INTEGER | 是否启用。 |
| `updated_at` | TEXT | 更新时间。 |

### `plants`

工厂主数据。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `code` | TEXT PK | 工厂代码。 |
| `name` | TEXT | 工厂名称，例如平湖九厂、平湖二厂。 |
| `business_area` | TEXT | 业务范围/事业部编码。 |
| `enabled` | INTEGER | 是否启用；删除后前端不展示停用工厂。 |
| `sort_order` | INTEGER | 排序。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

### `plant_groups`

业务范围/工厂集合配置。页面文案应展示为“业务范围”，不要再展示“工厂组”。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | TEXT PK | 业务范围 ID。 |
| `name` | TEXT | 业务范围名称。 |
| `short_name` | TEXT | 简称。 |
| `description` | TEXT | 说明。 |
| `zfi019nl_areas_json` | TEXT JSON | ZFI019NL 可用业务范围。 |
| `zfi080_areas_json` | TEXT JSON | ZFI080 可用业务范围。 |
| `zfi072_plants_json` | TEXT JSON | ZFI072A 默认工厂集合。 |
| `zco019_plants_json` | TEXT JSON | ZCO019 默认工厂集合。 |
| `enabled` | INTEGER | 是否启用。 |
| `sort_order` | INTEGER | 排序。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

### `plant_group_members`

业务范围与工厂的多对多关系。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `group_id` | TEXT PK | 业务范围 ID。 |
| `plant_code` | TEXT PK | 工厂代码。 |
| `sort_order` | INTEGER | 排序。 |
| `created_at` | TEXT | 创建时间。 |

索引：`idx_plant_group_members_plant(plant_code)`。

### `transaction_plant_rules`

事务码与业务范围/工厂规则。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `tcode` | TEXT PK | 事务码。 |
| `factory_rule` | TEXT | 规则说明。 |
| `default_group` | TEXT | 默认业务范围。 |
| `fixed_plants_json` | TEXT JSON | 最终可执行工厂列表。 |
| `selectable_group_ids_json` | TEXT JSON | 页面可选业务范围 ID。 |
| `business_area_mode` | TEXT | 业务范围解析模式，例如 `byPlant`。 |
| `business_areas_json` | TEXT JSON | 业务范围列表。 |
| `enabled` | INTEGER | 是否启用。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

执行时以此表解析最终工厂列表，VBS 不硬编码工厂。

### `runs`

一次执行请求或父子 run 的核心表。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `run_id` | TEXT PK | 运行 ID。 |
| `transaction_code` | TEXT | 事务码。 |
| `operator_id` | TEXT | 操作人 ID。 |
| `operator_name` | TEXT | 操作人姓名。 |
| `operator_dept` | TEXT | 操作人部门。 |
| `ding_talk_user_id` | TEXT | 钉钉用户 ID；当前联调可使用默认 `11464769`，也可由外部门户 URL token payload 的 `Account` 派生；正式应来自后端已验证登录态。 |
| `status` | TEXT | `queued/running/success/failed/partial_failed/canceled/skipped`。 |
| `request_json` | TEXT JSON | 请求参数快照，不保存密码或真实 secret。 |
| `sap_status_type` | TEXT | SAP 状态栏类型。 |
| `sap_status_text` | TEXT | SAP 状态栏文本。 |
| `message` | TEXT | 执行摘要或失败原因。 |
| `script_file` | TEXT | 执行脚本文件。 |
| `script_hash` | TEXT | 执行脚本 hash。 |
| `source` | TEXT | 来源：页面、定时任务、重跑等。 |
| `notify_target` | TEXT | 通知目标标识，不保存 appSecret。 |
| `priority` | INTEGER | 队列优先级。 |
| `attempt` | INTEGER | 内部尝试计数。 |
| `max_attempts` | INTEGER | 最大尝试次数。 |
| `run_type` | TEXT | `single/parent/child/chain/step`。 |
| `parent_run_id` | TEXT | 父 run。 |
| `batch_item_key` | TEXT | 拆批项键，例如工厂代码。 |
| `batch_index` | INTEGER | 拆批序号。 |
| `batch_total` | INTEGER | 拆批总数。 |
| `attempt_no` | INTEGER | 第几次重跑。 |
| `summary_json` | TEXT JSON | 父 run 汇总，记录成功/失败工厂、耗时等。 |
| `source_parent_run_id` | TEXT | 重跑来源父 run。 |
| `rerun_of_run_id` | TEXT | 当前 run 重跑自哪个 run。 |
| `locked_by` | TEXT | 队列 worker 锁持有者。 |
| `locked_at` | TEXT | 加锁时间。 |
| `queued_at` | TEXT | 入队时间。 |
| `started_at` | TEXT | 开始时间。 |
| `finished_at` | TEXT | 完成时间。 |
| `duration_ms` | INTEGER | 执行耗时。 |

索引：`idx_runs_status_queued_at(status, queued_at)`、`idx_runs_transaction_finished(transaction_code, finished_at)`、`idx_runs_parent(parent_run_id, batch_index, attempt_no)`。

### `run_batch_items`

多工厂拆批执行明细。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `parent_run_id` | TEXT | 父 run。 |
| `child_run_id` | TEXT UNIQUE | 子 run。 |
| `plant_code` | TEXT | 工厂代码。 |
| `batch_index` | INTEGER | 拆批序号。 |
| `attempt_no` | INTEGER | 重跑次数。 |
| `status` | TEXT | 子项状态。 |
| `message` | TEXT | 子项摘要。 |
| `started_at` | TEXT | 开始时间。 |
| `finished_at` | TEXT | 完成时间。 |
| `duration_ms` | INTEGER | 耗时。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |

### `run_params`

运行参数快照。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `run_id` | TEXT | run ID。 |
| `param_key` | TEXT | 参数名。 |
| `param_value` | TEXT | 参数值；不得保存密码、token、secret。 |
| `created_at` | TEXT | 创建时间。 |

唯一约束：`UNIQUE(run_id, param_key)`。

### `run_result_logs`

运行日志明细，页面黑底日志框应从 API 读取此类持久日志。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `run_id` | TEXT | run ID。 |
| `level` | TEXT | `INFO/WARN/ERROR`。 |
| `message` | TEXT | 日志内容，不写明文密钥。 |
| `created_at` | TEXT | 日志时间。 |

### `run_files`

运行输出文件登记。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `run_id` | TEXT | run ID。 |
| `file_type` | TEXT | `output/log/result/screenshot`。 |
| `file_name` | TEXT | 文件名。 |
| `file_path` | TEXT | 本机路径或相对输出路径。 |
| `file_size` | INTEGER | 文件大小。 |
| `created_at` | TEXT | 创建时间。 |

### `run_logs`

旧版运行摘要表，保留兼容。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `run_id` | TEXT PK | run ID。 |
| `tcode` | TEXT | 事务码。 |
| `status` | TEXT | 状态。 |
| `started_at` | TEXT | 开始时间。 |
| `finished_at` | TEXT | 结束时间。 |
| `duration_ms` | INTEGER | 耗时。 |
| `message` | TEXT | 摘要。 |
| `created_at` | TEXT | 创建时间。 |

### `script_cache`

脚本缓存与审计。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `tcode` | TEXT PK | 事务码。 |
| `script_file` | TEXT | 脚本文件。 |
| `script_hash` | TEXT | 脚本 hash。 |
| `script_text` | TEXT | 脚本文本缓存。 |
| `cached_at` | TEXT | 缓存时间。 |

### `notification_robots`

通知机器人配置。前端不得返回明文 secret。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | TEXT PK | 机器人 ID。 |
| `name` | TEXT | 名称。 |
| `robot_type` | TEXT | `dingtalk` 等。 |
| `target_label` | TEXT | 展示标签。 |
| `webhook_protected` | TEXT | 受保护 webhook。 |
| `secret_protected` | TEXT | 受保护 secret。 |
| `enabled` | INTEGER | 是否启用。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

### `notification_robot_bindings`

通知绑定规则。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `robot_id` | TEXT | 机器人 ID。 |
| `event_name` | TEXT | 事件名，例如 run_started、run_finished、run_failed。 |
| `tcode` | TEXT | 事务码过滤。 |
| `plant_group_id` | TEXT | 业务范围过滤。 |
| `enabled` | INTEGER | 是否启用。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |

唯一约束：`UNIQUE(robot_id, event_name, tcode, plant_group_id)`。

### `schedule_tasks`

定时任务配置。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | TEXT PK | 任务 ID。 |
| `name` | TEXT | 任务名称。 |
| `tcode` | TEXT | 事务码。 |
| `plants_json` | TEXT JSON | 工厂列表。 |
| `default_business_scope` | TEXT | 默认业务范围。 |
| `cron` | TEXT | Cron 表达式。 |
| `frequency` | TEXT | 频率，例如 daily/weekly/monthly。 |
| `run_time` | TEXT | 执行时间。 |
| `weekday` | TEXT | 执行星期。weekly 必填并默认周一；monthly 有值时按每月首个指定星期几触发，空值保留旧的每月固定日期规则。 |
| `enabled` | INTEGER | 是否启用。 |
| `notify_enabled` | INTEGER | 是否通知。 |
| `notify_on_success` | INTEGER | 成功是否通知。 |
| `notify_on_failure` | INTEGER | 失败是否通知。 |
| `notify_target` | TEXT | 通知目标标识。 |
| `params_json` | TEXT JSON | 其他参数。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

### `schedule_task_runs`

定时任务触发记录。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `task_id` | TEXT | 定时任务 ID。 |
| `run_id` | TEXT | 创建出的 run。 |
| `trigger_type` | TEXT | 触发方式。 |
| `scheduled_at` | TEXT | 计划时间。 |
| `triggered_at` | TEXT | 实际触发时间。 |
| `status` | TEXT | 触发状态。 |
| `message` | TEXT | 触发摘要。 |
| `created_at` | TEXT | 创建时间。 |

## 下一步建议新增表：可编排 SAP 报表链路

### `chain_definitions`

链路模板表，定义“先跑哪些步骤、依赖关系、失败策略”。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | TEXT PK | 链路模板 ID。 |
| `code` | TEXT UNIQUE | 链路编码。 |
| `name` | TEXT | 链路名称。 |
| `description` | TEXT | 说明。 |
| `strategy` | TEXT | `fail-fast/continue-on-error/conditional/manual-review`。 |
| `entry_params_json` | TEXT JSON | 页面入口参数定义。 |
| `enabled` | INTEGER | 是否启用。 |
| `sort_order` | INTEGER | 排序。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |
| `created_by` | TEXT | 创建人。 |
| `updated_by` | TEXT | 更新人。 |

### `chain_steps`

链路步骤定义。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | TEXT PK | 步骤 ID。 |
| `chain_id` | TEXT | 所属链路。 |
| `step_key` | TEXT | 步骤键，例如 `A_FETCH`、`B_FETCH`、`MERGE_RESULT`、`C_GUI`。 |
| `name` | TEXT | 步骤名称。 |
| `step_type` | TEXT | `report_fetch/transform/validate/vbs/manual_review/notify`。 |
| `depends_on_json` | TEXT JSON | 依赖步骤键数组。 |
| `input_mapping_json` | TEXT JSON | 从页面参数、工厂、前置 result 映射到当前入参。 |
| `output_schema_json` | TEXT JSON | 当前步骤输出 schema。 |
| `validation_json` | TEXT JSON | 必填字段、类型、空值、行数等校验规则。 |
| `report_name` | TEXT | SAP 报表名或函数名。 |
| `transaction_code` | TEXT | 需要 SAP GUI 时对应事务码。 |
| `script_file` | TEXT | 需要 VBS 时对应脚本。 |
| `timeout_seconds` | INTEGER | 步骤超时。 |
| `retry_count` | INTEGER | 步骤重试次数。 |
| `on_error` | TEXT | `fail-chain/skip-dependents/continue/manual-review`。 |
| `enabled` | INTEGER | 是否启用。 |
| `sort_order` | INTEGER | 执行排序。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |

建议索引：`idx_chain_steps_chain_sort(chain_id, sort_order)`、`idx_chain_steps_key(chain_id, step_key)`。

### `chain_runs`

一次链路执行的父级记录。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `chain_run_id` | TEXT PK | 链路运行 ID。 |
| `chain_id` | TEXT | 链路模板 ID。 |
| `chain_code` | TEXT | 链路编码快照。 |
| `name` | TEXT | 链路名称快照。 |
| `operator_id` | TEXT | 操作人 ID。 |
| `operator_name` | TEXT | 操作人姓名。 |
| `ding_talk_user_id` | TEXT | 通知用户 ID；规则同普通 run，不保存 JWT/Bearer 原文。 |
| `source` | TEXT | 页面、定时任务、重跑等。 |
| `status` | TEXT | `queued/running/success/failed/partial_failed/canceled/manual_review`。 |
| `strategy` | TEXT | 执行策略快照。 |
| `request_json` | TEXT JSON | 原始请求快照。 |
| `summary_json` | TEXT JSON | 汇总：成功步骤、失败步骤、跳过步骤、输出文件。 |
| `error_code` | TEXT | 链路级错误码。 |
| `error_message` | TEXT | 链路级错误摘要。 |
| `started_at` | TEXT | 开始时间。 |
| `finished_at` | TEXT | 结束时间。 |
| `duration_ms` | INTEGER | 总耗时。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |

建议索引：`idx_chain_runs_status_created(status, created_at)`、`idx_chain_runs_operator(operator_id, created_at)`。

### `chain_step_runs`

链路步骤执行明细。它是追溯失败、跳过、重跑的核心表。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `step_run_id` | TEXT PK | 步骤运行 ID。 |
| `chain_run_id` | TEXT | 所属链路运行。 |
| `step_id` | TEXT | 步骤定义 ID。 |
| `step_key` | TEXT | 步骤键快照。 |
| `name` | TEXT | 步骤名称快照。 |
| `step_type` | TEXT | 步骤类型快照。 |
| `status` | TEXT | `pending/queued/running/success/failed/skipped/manual_review/canceled`。 |
| `depends_on_json` | TEXT JSON | 实际依赖快照。 |
| `input_json` | TEXT JSON | 当前步骤输入快照；不得包含密码/secret。 |
| `output_json` | TEXT JSON | 小结果摘要。 |
| `result_id` | TEXT | 结构化结果 ID。 |
| `result_file` | TEXT | 大结果文件路径。 |
| `result_version` | INTEGER | 结果版本。 |
| `result_hash` | TEXT | 结果 hash。 |
| `validation_status` | TEXT | `not_checked/passed/failed`。 |
| `sap_message_type` | TEXT | SAP 返回消息类型。 |
| `sap_message_text` | TEXT | SAP 返回消息文本。 |
| `error_code` | TEXT | 错误码。 |
| `error_message` | TEXT | 错误摘要。 |
| `skip_reason` | TEXT | 跳过原因，例如依赖失败。 |
| `attempt_no` | INTEGER | 当前尝试次数。 |
| `rerun_of_step_run_id` | TEXT | 重跑来源步骤。 |
| `reuse_result_from_step_run_id` | TEXT | 复用哪个旧结果。 |
| `started_at` | TEXT | 开始时间。 |
| `finished_at` | TEXT | 结束时间。 |
| `duration_ms` | INTEGER | 耗时。 |
| `created_at` | TEXT | 创建时间。 |
| `updated_at` | TEXT | 更新时间。 |

建议索引：`idx_chain_step_runs_chain(chain_run_id, step_key)`、`idx_chain_step_runs_status(status, created_at)`。

### `chain_results`

结构化结果索引。小结果可直接存 `payload_json`；大结果写文件，只在表里存摘要和路径。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `result_id` | TEXT PK | 结果 ID。 |
| `chain_run_id` | TEXT | 链路 run。 |
| `step_run_id` | TEXT | 产生该结果的步骤 run。 |
| `result_type` | TEXT | `json/csv/xlsx/text/binary`。 |
| `schema_json` | TEXT JSON | 结果字段 schema。 |
| `payload_json` | TEXT JSON | 小结果内容或摘要。 |
| `file_path` | TEXT | 大结果文件路径。 |
| `file_size` | INTEGER | 文件大小。 |
| `row_count` | INTEGER | 行数。 |
| `hash` | TEXT | 文件或 payload hash。 |
| `version` | INTEGER | 结果版本。 |
| `created_at` | TEXT | 创建时间。 |
| `created_by_step_run_id` | TEXT | 创建来源步骤。 |

### `chain_step_logs`

链路步骤日志。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `chain_run_id` | TEXT | 链路 run。 |
| `step_run_id` | TEXT | 步骤 run，可为空表示链路级日志。 |
| `level` | TEXT | `INFO/WARN/ERROR`。 |
| `message` | TEXT | 日志内容，不写明文密钥。 |
| `data_json` | TEXT JSON | 结构化补充信息。 |
| `created_at` | TEXT | 创建时间。 |

### `notification_outbox`

通知外发箱，避免 DingTalk OpenAPI 失败阻塞 SAP GUI 串行队列。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | INTEGER PK | 自增 ID。 |
| `target_type` | TEXT | `dingtalk_user/dingtalk_robot`。 |
| `target_id` | TEXT | 钉钉用户 ID 或机器人 ID。 |
| `event_name` | TEXT | 通知事件。 |
| `title` | TEXT | 消息标题。 |
| `content` | TEXT | 消息正文。 |
| `payload_json` | TEXT JSON | 结构化 payload。 |
| `status` | TEXT | `pending/sending/sent/failed`。 |
| `attempt_no` | INTEGER | 尝试次数。 |
| `next_retry_at` | TEXT | 下次重试时间。 |
| `last_error` | TEXT | 最近错误摘要。 |
| `created_at` | TEXT | 创建时间。 |
| `sent_at` | TEXT | 发送成功时间。 |

## 状态机建议

### 普通 run

`queued -> running -> success/failed/partial_failed/canceled`

多工厂父 run：

1. 父 run 创建为 `queued`。
2. 为每个工厂创建子 run 和 `run_batch_items`。
3. 子 run 串行执行。
4. 父 run 根据子 run 汇总：全部成功为 `success`，部分失败为 `partial_failed`，全部失败为 `failed`。
5. `POST /api/runs/{parentRunId}/rerun-failed` 只重跑失败工厂，并记录 `rerun_of_run_id`、`source_parent_run_id`。

### chainRun/stepRun

链路：

`queued -> running -> success/failed/partial_failed/manual_review/canceled`

步骤：

`pending -> queued -> running -> success/failed/skipped/manual_review/canceled`

依赖失败时：

1. `fail-fast`：当前步骤 `failed`，依赖它的步骤全部 `skipped`，链路 `failed`。
2. `continue-on-error`：非关键步骤失败后可继续执行不依赖它的步骤，链路最终 `partial_failed`。
3. `conditional`：按条件判断后续步骤是否执行，被条件排除的步骤写 `skipped` 和 `skip_reason`。
4. `manual-review`：步骤进入 `manual_review`，链路暂停，等待人工确认。

## result 存储规则

1. 小结果：写 `chain_results.payload_json`，并保存 `schema_json`、`row_count`、`hash`。
2. 大结果：写 `outputs/<chainRunId>/<stepRunId>/result.json`、`.csv` 或 `.xlsx`，数据库只保存 `result_id`、`file_path`、`file_size`、`hash`、`row_count`。
3. 传给后续步骤或 VBS 的参数只能是 `result_id`、`result_file`、`result_version`，不能把大 JSON 直接塞进命令行。
4. 结果重跑必须产生新 `result_version`，除非明确记录 `reuse_result_from_step_run_id`。

## 敏感信息字段规则

不得明文写入以下字段或日志：

- `request_json`
- `params_json`
- `input_json`
- `output_json`
- `payload_json`
- `message`
- `error_message`
- `data_json`
- `app_settings.setting_value`

禁止明文保存：SAP 密码、SAP 登录 token、外部门户 JWT/Bearer token、钉钉 `appKey/appSecret/agentId` 真实值、webhook secret、公司私有接口密钥、个人机器路径中含用户名的敏感片段。必须用占位符、DPAPI、Windows 凭据、环境变量或本机未提交 `config.local.json`。URL token 只允许派生员工号等非密钥标识后写入相应业务字段。

## 最小落地版本建议

第一阶段不必一次实现所有链路表，可先做：

1. 保留当前 `runs`、`run_batch_items`、`run_result_logs`、`run_files`。
2. 新增 `chain_runs`、`chain_step_runs`、`chain_results`、`chain_step_logs`。
3. 暂时把链路定义写在后端配置文件或 `chain_steps` 简化表里。
4. API 先支持创建链路、查询链路、查询步骤、重跑失败步骤。
5. 页面先展示链路进度、步骤状态、失败原因、输出文件和重跑按钮。

后续再增加完整 `chain_definitions`、`chain_steps`、`notification_outbox` 和可视化编排。
