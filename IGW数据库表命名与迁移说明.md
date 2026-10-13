# IGW 专属数据库表

本次改动统一使用 `IGW_` 表名前缀。工程 JSON、API 字段、点位路径和日志属性名称保持原格式。下列名称是修改后的代码目标，6.75 当前运行的 1.7.25 尚未包含此改动。

| 存储 | IGW 专属表 |
| --- | --- |
| 工程 `project.db` | `IGW_project_meta`、`IGW_channels`、`IGW_devices`、`IGW_tag_groups`、`IGW_tags`、`IGW_tag_options`、`IGW_project_sections`、`IGW_mes_tasks`、`IGW_mes_task_index`、`IGW_flow_templates`、`IGW_device_templates`、`IGW_secret_entries`、`IGW_schema_history` |
| 界面偏好 `ui-preferences.db` | `IGW_ui_preferences` |
| 历史采样 `history.db` | `IGW_samples` |
| MES 日志 `mesupload.db`（或配置的路径） | `IGW_mes_log` |
| MES 接口收件箱 `mes-interface-inbox.db` | `IGW_interface_inbox`、`IGW_interface_state`、`IGW_interface_bodies`、`IGW_interface_configuration` |
| 已启用数据库同步的 SQLite 工作库 | `IGW_outbox`、`IGW_sync` |
| SQL Server / PostgreSQL / MySQL 同步元数据 | `IGW_sync_meta` |
| 外部数据库镜像 | `IGW_<来源>_<逻辑表名>`，例如 `IGW_project_tags`、`IGW_ui_ui_preferences`、`IGW_history_samples`、`IGW_mes_upload_log_mes_log` |
| QuestDB 日志中心 | `IGW_logs`、`IGW_payloads` |

账号目前保存在 `accounts.json`，不是数据库表；SQLite 系统表（例如 `sqlite_sequence`）不属于 IGW 专属表。工程操作使用的临时表也已增加 `IGW_` 前缀。

## 升级与保留数据

- SQLite 按各组件明确列出的表进行事务改名，验证工程归属或辅助表结构，保留 rowid、列值、自增序号、索引及外键。旧工程的 001/002 迁移文件保持原样，在执行时转换表标识符。
- 若旧名和新名同时存在，或结构与 IGW 不符，拒绝自动合并或覆盖。空工程首次迁移失败能够重试；仅允许结构完全匹配的空历史表恢复。
- 外部数据库只迁移具有 IGW 同步代次和匹配结构证据的旧镜像；未证明属于 IGW 的产线表保留。运行数据按主键合并，保留远端独有历史和待同步删除。辅助库稍后初始化时也执行迁移检查。
- QuestDB 在原表上做元数据改名，保留表 ID、目录、WAL、日志 UUID 去重及报文引用，不复制历史日志。旧缓存仅在发送时转换 measurement 表名。
- MySQL 的 `lower_case_table_names=1` 会把物理表名存成小写 `igw_...`；代码按服务器实际目录名称检查列，仍与无前缀的产线表分开。

## 已完成的现场副本验证

使用 6.75 发布前的独立备份副本，验证 13 张工程表改名前后所有 rowid、列值及结构列名一致。副本包含 611,763 个点位、78,604 个分组、6 台设备；完整性检查 `ok`，外键违规 0。表名迁移耗时约 47 ms，重复打开保持幂等。该验证没有修改正在运行的工程库。

外部 SQL Server、PostgreSQL、MySQL 的真实服务集成用例需要相应测试连接，本次未连接产线数据库运行它们。
