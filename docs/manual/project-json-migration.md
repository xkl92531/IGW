# IGW / KEP JSON 工程迁移

## 文件格式

IGW 导出一个 JSON 文件，根节点同时包含：

- `project`：KEP 原生工程层级和协议属性，供目标 KEP 版本做兼容性验证或导入。
- `igw`：IGW schema v4 扩展，保存稳定 ID、数组/嵌套数组、字符串长度、权限、写确认、MES 任务与流程图、模板、运行开关及其他 IGW 设置。

纯 KEP JSON（只有 `project`，没有 `igw`）也可以导入 IGW。此时 IGW 为通道、设备、组和点位生成确定性 ID，并使用 IGW 默认设置。KEP 数组若没有可证明的长度/维度信息会被拒绝，不能猜测长度后静默导入。

工程 JSON 只包含可迁移的配置。运行日志、历史采集数据、许可证、机器绑定信息、证书私钥和导入预检临时文件不会写入导出文件；目标电脑应分别部署许可证和证书。公开证书如属于通道配置可以保留引用，但私钥本体不会导出。

## 导出

```http
GET /api/v1/project/export?includePortableSecrets=false
```

需要迁移凭据时，将 `includePortableSecrets` 设为 `true`，并通过请求头传迁移密码：

```http
X-Igw-Migration-Password: <一次性迁移密码>
```

凭据使用 PBKDF2-SHA256（310,000 次）和 AES-256-GCM 加密；密码不写入工程文件。不要把迁移密码放在 URL、脚本日志或工单正文里。

## 两阶段导入

1. `POST /api/v1/project/import/inspect`，使用 `multipart/form-data` 上传字段 `file`。
2. 检查返回的数量、兼容性问题、`token` 和 `fileHash`。
3. `POST /api/v1/project/import/commit`，再次上传同一个 `file`，并提交 `token`、`fileHash`；有加密凭据时再提交 `migrationPassword`。

预检不修改工程。提交时会重新计算上传文件 SHA-256；文件、token 或 hash 任一不一致都返回 `import_file_changed`，数据库修订号和运行态保持不变。成功提交通过配置协调器持久化并应用运行态，修订号只增加一次。

界面中的 JSON“打开工程”和“导出工程”都走上述服务端流式接口，不由浏览器执行整份工程格式转换。超过 50,000 点的 Web 工程配置自动进入分页工作区：树节点按展开加载，当前组点位每页最多 200 条，搜索和批量变更由 SQLite 执行；JSON 导入/导出仍是唯一允许的全工程交换路径。

## KEP 实机往返探针

工具默认只连接、验证输入并备份 KEP 工程，不执行写入：

```powershell
dotnet run --project tools/IGW.KepProjectProbe -- \
  --input project.json \
  --base-url http://127.0.0.1:57412/ \
  --backup kep-backup.json \
  --output kep-reexport.json
```

只有在隔离的测试 KEP、备份已验证且明确允许替换时，才追加：

```text
--allow-project-replace
```

如需认证，用户名可用 `--username`，密码通过 `--password-env KEP_PASSWORD` 指定环境变量名。工具不会把密码写进报告。往返报告会列出驱动、通道/设备属性、缺失组/点、地址、类型、权限和扫描周期的完整路径差异。

## 2026-09-01 本地验证证据

以下结果仅证明当前 IGW 代码和本地模拟器数据，不等同于现场 PLC 或目标 KEP 版本认证：

- 交换专项自动化：34 项通过，覆盖 KEP 层级、IGW 扩展、协议映射、加密凭据、预检/导入和 HTTP API。
- 10,000 个同级点匹配：优化前约 34.4 秒；索引化后测试低于 1 秒，消除了平方级扫描。
- 1,000,000 点本地完整往返：导出 17,423 ms，预检 16,925 ms，导入 62,653 ms。
- 1,000,000 点 JSON：820,339,603 字节；峰值工作集 9,631,453,184 字节。
- 1,000,000 点编辑器边界测试：树分支按需请求，点位表最多渲染 200 行，多选复制/移动由单次带修订号的事务请求执行。
- 源库与目标库：点位数量一致、稳定 ID/配置文档哈希一致、工程修订从 1 到 2、可迁移密钥恢复成功。
- 本地结果文件：`artifacts/project-exchange-million.json`。

当前导出是流式的，不构造完整 JSON 字符串。导入已经避免平方级匹配，但当前 schema-v4 解析和最终工程快照应用仍会物化完整工程，因此百万点峰值内存约 9.63 GB；在小内存工控机上应先评估内存，后续可进一步改成逐实体写入 staging SQLite 再切换数据库。

## 证据边界

- “本地 schema/golden 测试通过”只证明字段映射和无损恢复逻辑。
- “IGW 百万点往返通过”只证明本机 SQLite/JSON 交换规模和一致性。
- “目标 KEP 可直接导入”必须由上述探针在实际 KEP 版本上完成备份、导入、重新导出和零阻断差异后才能确认。
- 模拟器读写不代表真实 PLC 协议时序、权限和固件行为。
- 真实 PLC 读写仍需现场按协议、权限、长度、地址和写后读回分别验收。
