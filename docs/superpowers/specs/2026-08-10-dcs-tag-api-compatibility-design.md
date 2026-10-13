# DCS Tag API Compatibility Design

## 1. Goal

Add a DCS-compatible PLC API surface to IGW so an existing DCS client only changes the server IP and port. The HTTP paths, methods, request property names, response property names, and response shapes remain compatible with the DCS `TagController` found under:

`E:\wx\xwechat_files\jimaku753_9747\msg\file\2026-08\DCS`

The existing IGW `/api/v1/plc/*` API remains available and unchanged.

## 2. Architecture

Implement a parallel compatibility adapter rather than renaming or replacing the IGW API:

- `DcsTagCompatibilityApi` registers `/Tag/*` endpoints and their Swagger metadata.
- `DcsTagContracts` defines the DCS request and response models using camel-case JSON.
- `DcsTagValueConverter` maps DCS data types, IGW data types, and JSON values in both directions.
- `DcsTagProjectUpdater` implements the `AddTags` project mutation as an atomic clone, validate, save, and hot-reload operation.
- PLC reads use `DataHub`; PLC writes use `RuntimeEngine.WriteAsync` so device serialization and driver error handling remain owned by IGW.

The compatibility adapter must not copy the DCS communication stack or bypass the IGW write queue.

## 3. Routes and Contracts

The adapter exposes all eleven DCS routes:

| Method | Route | Request | Successful `backResult` |
|---|---|---|---|
| GET | `/Tag/ReadConditionCache?groupName=...` | Query string | Cached group object or `null` |
| POST | `/Tag/ReadTag?tag=...` | Query string | One PLC value |
| POST | `/Tag/ReadTag1` | `{"tag":"full.path"}` | One PLC value |
| POST | `/Tag/ReadTags` | `["tag1","tag2"]` | JSON-serialized value strings |
| POST | `/Tag/ReadTagsAsList` | `["tag1","tag2"]` | `[{"key":"tag1","value":...}]` |
| POST | `/Tag/ReadTagsAsDictionary` | `["tag1","tag2"]` | `{"tag1":...,"tag2":...}` |
| POST | `/Tag/WriteTag` | `{"tag":{"key":"tag1","value":...}}` | Boolean |
| POST | `/Tag/WriteTags` | `{"tags":[{"key":"tag1","value":...}]}` | Boolean |
| POST | `/Tag/GetTagValueTypeByTagName` | `["tag1","tag2"]` | Direct dictionary, without an envelope |
| POST | `/Tag/CheckTagValueValidity` | `{"tags":[{"key":"tag1","value":...}]}` | Boolean |
| POST | `/Tag/AddTags` | DCS `TagUpdateModel[]` | String or `null`, matching the DCS envelope type |

The common response envelope is:

```json
{
  "httpStatusCode": 200,
  "isSuccess": true,
  "backResult": null,
  "errorMessage": null,
  "exceptionMessage": null
}
```

`httpStatusCode` reflects the real HTTP status instead of reproducing the DCS bug that often leaves the enum at zero. Existing consumers can continue using `isSuccess` and `backResult` without changes.

### 3.1 Validation and Status Rules

- Null, empty, or malformed requests return HTTP 400.
- An unknown tag, read-quality failure, read-only write target, unsupported value, or driver write failure returns HTTP 200 with `isSuccess=false` and a DCS-style Chinese `errorMessage`.
- Batch write validates every item before the first PLC write. Invalid input causes no partial write.
- Batch read preserves request ordering.
- `ReadTags` intentionally preserves the DCS JSON-string-list format.
- `ReadConditionCache` returns `null` when no matching cache exists. The inspected DCS project currently has every condition-cache write call commented out, so this is its normal behavior.

## 4. Name Compatibility

Read and write operations require a full tag path and do not use fuzzy or leaf-name matching.

`AddTags` reproduces the DCS logical naming convention:

```text
通用.<CommunicationType>_<Ip>.<TagName>
```

IGW channels bind one driver per channel, while DCS can place mixed protocol devices under one `通用` channel. To preserve both systems' invariants:

- The first compatible driver may use the physical IGW channel name `通用`.
- Additional drivers use deterministic internal channel names such as `通用_S1500` or `通用_Omr_Fins`.
- Device settings persist the original DCS communication type and logical alias.
- The `/Tag/*` adapter resolves the DCS logical full path to the physical IGW full path before accessing `DataHub` or `RuntimeEngine`.
- Native `/api/v1/plc/*` callers continue using physical IGW paths.

Alias resolution must be deterministic, case-insensitive, and reject ambiguous aliases.

## 5. Automatic Type Mapping

`AddTags` converts DCS `KepwareTagDataTypeEnum` values automatically:

| DCS type | IGW representation | DCS type name returned |
|---|---|---|
| String | `IgwDataType.String` | `String` |
| Boolean | `IgwDataType.Bool` | `Boolean` |
| Char | `IgwDataType.Char` | `Char` |
| Byte | `IgwDataType.Byte` | `Byte` |
| Short | `IgwDataType.Int16` | `Short` |
| Word | `IgwDataType.UInt16` | `Word` |
| Long | `IgwDataType.Int32` | `Long` |
| DWord | `IgwDataType.UInt32` | `DWord` |
| Float | `IgwDataType.Float` | `Float` |
| Double | `IgwDataType.Double` | `Double` |
| Date | ISO date/time stored through the string representation | `Date` |
| StringArray | `String` with `IsArray=true` | `StringArray` |
| BooleanArray | `Bool` with `IsArray=true` | `BooleanArray` |
| ByteArray | `ByteArray` | `ByteArray` |
| ShortArray | `Int16` with `IsArray=true` | `ShortArray` |
| WordArray | `UInt16` with `IsArray=true` | `WordArray` |
| LongArray | `Int32` with `IsArray=true` | `LongArray` |
| DWordArray | `UInt32` with `IsArray=true` | `DWordArray` |
| FloatArray | `Float` with `IsArray=true` | `FloatArray` |

`WriteTag` and `WriteTags` look up the configured IGW tag definition and convert the incoming JSON value automatically. The caller does not submit a separate type name. String-form numeric values are accepted when they fit the target type. Overflow, malformed values, and invalid array elements fail validation before writing.

Array acceptance in the compatibility layer does not hide driver limitations. If the selected PLC driver cannot write that array form, the response reports the real write failure.

## 6. `AddTags` Communication Mapping

| DCS communication type | IGW driver | Device setting |
|---|---|---|
| OpcUa | `OpcUa.Client` | OPC UA URL from `ip` and `port` |
| S1200 | `Siemens.S7` | `model=S7-1200` |
| S1500 | `Siemens.S7` | `model=S7-1500` |
| S200Smart | `Siemens.S7` | `model=S7-200Smart` |
| S200 | `Siemens.S7` | `model=S7-200` |
| S300 | `Siemens.S7` | `model=S7-300` |
| S400 | `Siemens.S7` | `model=S7-400` |
| FX5U | `Mitsubishi.Mc` | `mcVersion=Qna_3E` |
| Omr_NJ | `Omron.Cip` | Default NJ/NX settings |
| Omr_Fins | `Omron.Fins` | Default FINS settings |
| MemoryCache | `Simulator` | Simulation settings |

`Inovance_H5U` remains accepted by the contract but returns an explicit unsupported-communication-type error because IGW has no native H5U driver. It must not be silently mapped to generic Modbus because DCS H5U addresses are not guaranteed to be Modbus register addresses.

Port handling is corrected rather than copying a DCS defect:

- A non-zero request port is preserved.
- A zero request port selects the driver protocol default.
- Repeated `AddTags` calls update or reuse the same compatible channel, device, address, and tag instead of creating duplicates.
- `AddTags` only adds or updates; it never deletes unrelated project content.

All `AddTags` items are validated before cloning and applying the project. `ProjectStore.Validate` runs before `IGatewayHost.ReloadAsync`. A validation or reload failure leaves the current project and runtime intact.

## 7. Read and Write Data Flow

### Read

1. Resolve a DCS logical alias to an IGW tag ID when needed.
2. Enforce branch read permission.
3. Call `DataHub.Touch` to activate demand-based acquisition.
4. Read the current `DataValue` from `DataHub`.
5. Return success only for a good-quality value; otherwise return a DCS envelope with an actionable error.

### Write

1. Resolve the DCS logical alias.
2. Confirm the tag exists and is writable.
3. Convert the JSON value using the tag definition.
4. Validate the entire batch.
5. Send writes through `RuntimeEngine.WriteAsync`.
6. Read back through `DataHub`; retry failed or unconfirmed writes up to two times.
7. Return `backResult=true` only when every requested write succeeds and confirms.

Writes for different devices may execute concurrently. Writes targeting the same device remain serialized by the IGW engine.

## 8. Security

- When project security is disabled, `/Tag/*` is callable without authentication, matching the DCS deployment behavior.
- When project security is enabled, `/Tag/*` uses the existing IGW HTTP Basic authentication.
- Read routes enforce `ReadBranches` after alias resolution.
- `WriteTag`, `WriteTags`, and `AddTags` require `CanWrite=true`.
- `CheckTagValueValidity` requires read access but performs no PLC write.

The middleware must classify DCS read and write routes explicitly rather than relying only on the `/api/v1` prefix.

## 9. Swagger

The existing `/swagger` document includes both API families:

- `PLC 操作`: existing `/api/v1/plc/*` endpoints.
- `DCS 兼容接口`: all eleven `/Tag/*` endpoints.

Each DCS operation declares the original route name, HTTP method, request schema, response envelope, and Chinese summary. Internal engineering, licensing, and MES management routes remain excluded.

## 10. Testing

Implementation follows test-driven development. Tests must fail before production changes and cover:

1. All eleven route templates and HTTP methods.
2. Camel-case request and response JSON snapshots.
3. Scalar, date, and array mappings in both directions.
4. Single read, JSON-string-list read, key/value-list read, and dictionary read.
5. Single and batch write, including string-form numbers and read-back confirmation.
6. Empty input, unknown tags, bad quality, read-only tags, overflow, malformed arrays, and driver failures.
7. Batch validation preventing partial writes.
8. `AddTags` add, update, idempotent repeat, default-port handling, unsupported H5U, and rollback on validation failure.
9. DCS alias resolution across multiple driver-specific IGW channels.
10. Security enforcement for anonymous, authenticated read-only, branch-limited, and writable users.
11. Swagger inclusion of the DCS routes and continued exclusion of internal routes.
12. Regression coverage proving existing `/api/v1/plc/*` behavior is unchanged.

## 11. Non-goals

- Replacing or removing the native IGW PLC API.
- Copying DCS communication libraries or HslCommunication into IGW.
- Adding a new native Inovance H5U driver as part of this compatibility slice.
- Reproducing DCS defects such as non-zero ports being changed to zero, silent `AddTags` exceptions, or a zero `httpStatusCode` on successful HTTP responses.
