namespace IGW.Abstractions;

/// <summary>
/// MES 上报(整合自 HsjmDcs 的「采集单元 + 步骤」):在 IGW 已采集的点位上配置
/// "触发 → 取点拼 JSON → HTTP 上报 MES → 按响应回写 PLC"。直接读 DataHub 缓存,
/// 不再经 OPC UA 二次读;回写走引擎写队列。序列化进 .igwproj,经 MES 专用保存接口热重载。
/// </summary>
public sealed class MesUploadSettings
{
    public bool Enabled { get; set; }
    public string DefaultMesUrl { get; set; } = "";          // 默认上报地址(任务/步骤可覆盖)
    public string AuthBearerToken { get; set; } = "";        // 可选 Bearer 鉴权(留空=无)
    public int HttpTimeoutMs { get; set; } = 30000;
    public int QueueCapacity { get; set; } = 1000;           // 上报有界队列容量(满=丢最旧+告警,不阻塞采集)
    public string LogDbPath { get; set; } = "mesupload.db";  // 上报日志 SQLite
    public List<MesTask> Tasks { get; set; } = new();
    public List<MesFlowTemplate> FlowTemplates { get; set; } = new();   // 流程模板库(画布任务的可复用"印章");旧工程缺省空

    /// <summary>
    /// 分层采集总开关(默认开)。开启后只有触发点、条件点、握手/心跳等判定点按采集周期常驻轮询;
    /// 上报点位不再常态拉取,改为触发条件满足后立即按需批量读取,读到的即触发时刻的权威值。
    /// 可由 <see cref="MesTask.TieredAcquisition"/> 按任务关闭。
    /// </summary>
    public bool TieredAcquisition { get; set; } = true;
}

/// <summary>
/// MES 流程模板:画布任务存成的可复用"印章"——套用即拷贝断开,不保持关联(与设备模板的"活关联"不同,任务天生要被个性化)。
/// 套用 = 深拷贝 <see cref="Task"/>,把所有以 <see cref="SourceGroupRef"/> 为前缀的点/组引用深度重写为目标组前缀。
/// </summary>
public sealed class MesFlowTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";              // 工程内唯一
    public string? Description { get; set; }
    public string SourceGroupRef { get; set; } = "";    // 提取时的源组路径(通道.设备.组)
    public MesTask Task { get; set; } = new();          // 完整任务体(Id/GroupRef/Enabled 在套用时重置)
}

/// <summary>一个上报任务 = HsjmDcs 的「采集单元 + 其步骤」合并成一个对象。</summary>
public sealed class MesTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool FlowAvailable { get; set; } = true;          // 数据块是否开放 MES 流程/采集上报功能；关闭时不得运行
    public bool Enabled { get; set; } = true;                // 该标记组是否采集(单独开关,各组互不影响)
    public string? GroupRef { get; set; }                    // 绑定的标记组完整路径(= 该 DB 块);一个组 0/1 个采集配置
    public string? DeviceRef { get; set; }                   // 关联设备(通道.设备),供状态/握手,可空
    public int CollectIntervalMs { get; set; } = 1000;       // 采集周期:本组全部点按此周期读入缓存
    public bool ConfirmRewrite { get; set; } = true;         // 写后读回确认(全局,各步骤回写共用)
    public MesTrigger Trigger { get; set; } = new();
    public MesInterfaceListener Listener { get; set; } = new();
    public MesPayload Payload { get; set; } = new();
    public List<MesStep> Steps { get; set; } = new();
    public MesHeartbeat Heartbeat { get; set; } = new();
    public MesHandshake Handshake { get; set; } = new();
    public MesFlow? Flow { get; set; }                       // 可视化流程图(Dify 式);非空且有节点则按图执行,否则退回线性 Steps

    /// <summary>
    /// 本任务是否分层采集。null=跟随 <see cref="MesUploadSettings.TieredAcquisition"/> 总开关;
    /// false=本任务全部点位仍按 <see cref="CollectIntervalMs"/> 常驻轮询(需要上报点位连续采样时使用)。
    /// </summary>
    public bool? TieredAcquisition { get; set; }
}

/// <summary>可视化流程(节点图):节点 + 连线。引擎从 Trigger 节点起按连线执行,Branch 按上一步成功/失败分流。</summary>
public sealed class MesFlow
{
    public List<MesNode> Nodes { get; set; } = new();
    public List<MesEdge> Edges { get; set; } = new();
}

/// <summary>流程节点。按 Type 取用对应配置(复用现有子模型)。</summary>
public sealed class MesNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";                   // Trigger / Upload / Branch / Switch / Writeback / ResponseWriteback / Delay / FixedData
    public string Title { get; set; } = "";
    public double X { get; set; }                            // 画布坐标
    public double Y { get; set; }
    public string InSide { get; set; } = "Left";             // 输入口在哪侧:Left/Right/Top/Bottom
    public string OutSide { get; set; } = "Right";           // 输出口在哪侧:Left/Right/Top/Bottom
    public List<MesCondition> Conditions { get; set; } = new(); // 旧工程兼容字段；Trigger 已忽略，条件统一使用 Condition.Branches
    public MesPayload Payload { get; set; } = new();         // Upload:上报内容
    public MesHttp Http { get; set; } = new();               // Upload:HTTP 参数
    public List<MesCapture> Captures { get; set; } = new();  // Upload:响应后捕获变量
    public MesWriteBack WriteBack { get; set; } = new();     // Writeback / ResponseWriteback:回写
    public int DelayMs { get; set; }                         // Delay:延时毫秒
    public string SwitchTag { get; set; } = "";              // Switch:读哪个点判值
    public List<string> Cases { get; set; } = new();         // Switch:各分支的值(端口 case0..caseN;另有 default)
    public bool Continuous { get; set; }                     // 已不使用：条件满足就处理(旧工程里的值忽略)
    public int StuckThresholdMs { get; set; }                // 卡住判定手工阈值(毫秒;0=仅用自学习基线)。手工优先于 P95×3
    public List<MesCondBranch> Branches { get; set; } = new(); // Condition(条件):多分支,每分支一组条件(AND);端口 b0..bN + default(都不满足)
    public string JudgeField { get; set; } = "";             // Branch(判成败)单条件判据:响应字段(旧字段,兼容)
    public string JudgeOp { get; set; } = "Eq";              //   比较(Eq/Ne/Gt/Ge/Lt/Le/Regex)
    public string JudgeValue { get; set; } = "";             //   期望值(响应字段 op 此值 → 成功)
    public List<MesJudgeCond> JudgeConds { get; set; } = new(); // Branch(判成败)多条件:非空则用它(按 JudgeCombine 组合);空且 JudgeField 也空=用上报节点的成功判定
    public string JudgeCombine { get; set; } = "All";        //   多条件组合:All(全部满足才成功)/ Any(任一满足即成功)
    public List<MesFixedValue> FixedValues { get; set; } = new(); // FixedData(固定值):后面的上报一起打包进 JSON；没有上报时当作接口返回供回写
}

/// <summary>判成败的一个条件:比较 MES 响应里的某字段。</summary>
public sealed class MesJudgeCond
{
    public string Field { get; set; } = "";                  // 响应字段名
    public string Op { get; set; } = "Eq";                   // Eq/Ne/Gt/Ge/Lt/Le/Regex
    public string Value { get; set; } = "";                  // 期望值
}

/// <summary>条件节点的一个分支:一组条件(全满足)+ 标签(=出口名)。</summary>
public sealed class MesCondBranch
{
    public string Label { get; set; } = "";
    public List<MesCondition> Conditions { get; set; } = new();
}

/// <summary>连线:从某节点的某端口 → 目标节点。Branch 节点有 success/fail 两个端口,其余用 out。</summary>
public sealed class MesEdge
{
    public string From { get; set; } = "";
    public string FromPort { get; set; } = "out";            // out / success / fail
    public string To { get; set; } = "";
}

/// <summary>触发:何时执行本任务。</summary>
public sealed class MesTrigger
{
    public string Mode { get; set; } = "OnChange";           // OnChange / Interval / Always / InterfaceEvent
    public string TagId { get; set; } = "";                  // 触发点完整路径(Mode != Interval 时用)
    public string Op { get; set; } = "Eq";                   // Eq/Ne/Gt/Ge/Lt/Le/Regex/Changed
    public string Value { get; set; } = "";                  // 比较值(Op=Regex 时为正则)
    public int IntervalMs { get; set; } = 1000;              // Mode=Interval 的周期
}

/// <summary>上报内容:取哪些点、拼成什么 JSON。</summary>
public sealed class MesPayload
{
    public string Mode { get; set; } = "";                   // DataRanges / FieldMapping;空=旧工程按已有内容自动识别
    public string FieldKeyMode { get; set; } = MesFieldKeyModes.Name; // 仅保留旧工程序列化兼容；输出由 ParameterCode / AsDictionary 决定
    public List<MesDataRange> DataRanges { get; set; } = new(); // 范围配置:整 DB/结构体引用;可与映射配置同时保留,由 Mode 决定当前生效项
    public List<MesField> Fields { get; set; } = new();      // 普通点 → JSON 字段
    public List<MesStruct> Structs { get; set; } = new();    // 结构体数组 → JSON 数组
    public Dictionary<string, string> Append { get; set; } = new(); // 固定附加字段(WorkStationCode/StepCode/工位号…)
    public List<MesExtraField> ExtraFields { get; set; } = new(); // 有类型的业务字段，支持固定值/点位/接口消息
    public MesNumberFormat Number { get; set; } = new();     // 报文里小数的位数与取法；无穷大/无穷小始终改成 ±99999999
    public List<MesFixedValue> FixedValues { get; set; } = new(); // 有类型的固定值，始终放在 JSON 根层(取代只能填文本的 Append)
}

/// <summary>一个固定值：字段名、类型、值；在「固定值」节点里还可以指定写回的 PLC 点位。</summary>
public sealed class MesFixedValue
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = MesFixedValueTypes.Text; // Text 文本 / Int 整数 / Decimal 小数 / Bool 布尔
    public string Value { get; set; } = "";
    public string? TagId { get; set; }                       // 写回点位(只有「固定值」节点用；空=不写回)
}

public static class MesFixedValueTypes
{
    public const string Text = "Text";
    public const string Int = "Int";
    public const string Decimal = "Decimal";
    public const string Bool = "Bool";

    /// <summary>按类型转成 JSON 值；值和类型对不上时抛出，报文不会带着错值发出去。</summary>
    public static System.Text.Json.Nodes.JsonNode? ToJson(MesFixedValue item)
    {
        string name = string.IsNullOrWhiteSpace(item.Name) ? "(未命名)" : item.Name.Trim();
        string value = (item.Value ?? "").Trim();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (item.DataType)
        {
            case Int:
                if (long.TryParse(value, System.Globalization.NumberStyles.Integer, invariant, out long integer)) return integer;
                throw new InvalidDataException($"固定值 {name} 不是整数：{item.Value}");
            case Decimal:
                if (decimal.TryParse(value, System.Globalization.NumberStyles.Float, invariant, out decimal number)) return number;
                throw new InvalidDataException($"固定值 {name} 不是数字：{item.Value}");
            case Bool:
                if (value is "1") return true;
                if (value is "0") return false;
                if (bool.TryParse(value, out bool flag)) return flag;
                throw new InvalidDataException($"固定值 {name} 不是布尔值(true/false)：{item.Value}");
            default:
                return item.Value ?? "";
        }
    }

    /// <summary>写回 PLC 用的文本：数字、布尔按规范写法，交给驱动按点位类型转换。</summary>
    public static string ToWriteText(MesFixedValue item) => ToJson(item) switch
    {
        System.Text.Json.Nodes.JsonValue json when json.TryGetValue(out bool flag) => flag ? "true" : "false",
        System.Text.Json.Nodes.JsonValue json when json.TryGetValue(out string? text) => text ?? "",
        var json => json?.ToJsonString() ?? "",
    };

    /// <summary>名字不能为空、不能重复。</summary>
    public static void Validate(IEnumerable<MesFixedValue>? items)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item?.Name)) throw new InvalidDataException("固定值的字段名不能为空。");
            if (!names.Add(item.Name.Trim())) throw new InvalidDataException("固定值字段名重复：" + item.Name);
            ToJson(item);
        }
    }
}

/// <summary>上报报文里的小数怎么写。整数、字符串不受影响。</summary>
public sealed class MesNumberFormat
{
    public int? Decimals { get; set; }                       // 保留几位小数(0..10)；空=按原值
    public string Rounding { get; set; } = MesNumberRounding.Round; // Round 四舍五入 / Truncate 直接舍去 / Ceiling 进一
}

public static class MesNumberRounding
{
    public const string Round = "Round";
    public const string Truncate = "Truncate";
    public const string Ceiling = "Ceiling";
}

public sealed class MesExtraField
{
    public string Name { get; set; } = ""; // 输出对象点路径，如 product.code
    public string DataType { get; set; } = "string"; // string / int32 / double / bool / json
    public string Source { get; set; } = "Fixed"; // Fixed / Tag / Input
    public string Value { get; set; } = ""; // string 为原文；其他类型为 JSON 文本
    public string Path { get; set; } = "";
    public bool Required { get; set; }
}

/// <summary>可由配置入口和运行时共用的业务字段校验与严格类型转换。</summary>
public static class MesExtraFieldValidation
{
    public static void Validate(MesPayload payload, bool automatic = true)
    {
        var names = new List<string>();
        foreach (var field in payload.ExtraFields ?? [])
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Name.Split('.').Any(p =>
                    string.IsNullOrWhiteSpace(p) || p.Trim() != p || p.IndexOfAny(['[', ']', '/', '$']) >= 0))
                throw new InvalidDataException("业务字段输出路径无效：" + field?.Name);
            if (names.Any(name => Conflicts(name, field.Name)))
                throw new InvalidDataException("业务字段输出路径重复或冲突：" + field.Name);
            names.Add(field.Name);
            if (field.DataType is not ("string" or "int32" or "double" or "bool" or "json"))
                throw new InvalidDataException("业务字段类型无效：" + field.Name);
            if (field.Source is not ("Fixed" or "Tag" or "Input"))
                throw new InvalidDataException("业务字段来源无效：" + field.Name);
            if (field.Source == "Fixed") ConvertValue(field, field.Value, true);
            else if (string.IsNullOrWhiteSpace(field.Path))
                throw new InvalidDataException("业务字段来源路径不能为空：" + field.Name);
            var ordinary = payload.Append.Keys.AsEnumerable();
            bool fieldConflict = false;
            if (!MesPayloadModes.IsDataRanges(payload))
            {
                fieldConflict = payload.Fields.Where(f => (!automatic || string.IsNullOrWhiteSpace(f.ParameterCode)) && !MesParameterRouting.IsCovered(f.TagId, payload.Structs))
                    .Select(f => MesFieldKeyModes.Resolve(payload, f))
                    .Any(name => !string.IsNullOrWhiteSpace(name) && Conflicts(name, field.Name));
                ordinary = ordinary.Concat(payload.Structs.Where(s => (!automatic || !s.AsDictionary) && (s.Members.Count > 0 || s.Structs.Count > 0)).Select(s => s.AliasName));
            }
            else ordinary = ordinary.Concat(payload.DataRanges.Where(r => !string.IsNullOrWhiteSpace(r.OutputPath)).Select(r => r.OutputPath));
            if (fieldConflict || ordinary.Any(name => !string.IsNullOrWhiteSpace(name) && Conflicts(name, field.Name)))
                throw new InvalidDataException("业务字段与采集字段冲突：" + field.Name);
        }
    }

    private static bool Conflicts(string left, string right) => left == right || left.StartsWith(right + ".", StringComparison.Ordinal) || right.StartsWith(left + ".", StringComparison.Ordinal);

    public static System.Text.Json.JsonElement ConvertValue(MesExtraField field, object? value, bool fixedValue = false)
    {
        try
        {
            var element = fixedValue && field.DataType != "string"
                ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(string.IsNullOrWhiteSpace((string?)value) ? "null" : (string)value!)
                : System.Text.Json.JsonSerializer.SerializeToElement(value);
            if (element.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined ||
                (element.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString())))
            {
                if (field.Required) throw new InvalidDataException("必填业务字段缺失：" + field.Name);
                return System.Text.Json.JsonSerializer.SerializeToElement<object?>(null);
            }
            bool valid = field.DataType switch
            {
                "string" => element.ValueKind == System.Text.Json.JsonValueKind.String,
                "int32" => element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt32(out _),
                "double" => element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number),
                "bool" => element.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                "json" => true,
                _ => false,
            };
            if (!valid) throw new InvalidDataException($"业务字段 {field.Name} 的值不符合 {field.DataType} 类型。");
            return element;
        }
        catch (System.Text.Json.JsonException error) { throw new InvalidDataException("业务字段 JSON 值无效：" + field.Name, error); }
    }
}

/// <summary>旧 FieldKeyMode 值的读写兼容、原始字段名及显式 parameterDic 路由的校验。</summary>
public static class MesFieldKeyModes
{
    public const string Name = "Name";
    public const string ItemId = "ItemId";
    public const string ParameterDictionaryName = "parameterDic";

    public static bool UsesItemId(MesPayload payload) =>
        string.Equals(payload.FieldKeyMode, ItemId, StringComparison.OrdinalIgnoreCase);

    public static string Resolve(MesPayload payload, MesField field) =>
        string.IsNullOrWhiteSpace(field.FieldName) ? field.TagId.Split('.').Last() : field.FieldName;

    /// <summary>启用字典路由时外层字面键不能占用 parameterDic；也校验固定值节点动态合并的字段。</summary>
    public static void ValidateReservedRootNames(MesPayload payload, IEnumerable<string?> names)
    {
        if (!MesParameterRouting.HasRoutes(payload) || MesPayloadModes.IsDataRanges(payload)) return;
        foreach (string? name in names)
            if (string.Equals(name?.Trim(), ParameterDictionaryName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"MES 根字段不能使用保留名称 {ParameterDictionaryName}：{name}");
    }

    public static void Validate(MesPayload payload, Func<MesStruct, string, IEnumerable<string>>? elements = null, bool automatic = true)
    {
        // Name/ItemId 仅接受旧工程值，不再选择输出格式；未知旧值仍报告配置错误。
        if (!string.IsNullOrWhiteSpace(payload.FieldKeyMode) &&
            !string.Equals(payload.FieldKeyMode, Name, StringComparison.OrdinalIgnoreCase) && !UsesItemId(payload))
            throw new InvalidDataException("MES FieldKeyMode 仅支持 Name/ItemId：" + payload.FieldKeyMode);

        if (!automatic || MesPayloadModes.IsDataRanges(payload)) return;
        ValidateReservedRootNames(payload, payload.Structs.Where(item => !item.AsDictionary).Select(item => item.AliasName)
            .Concat(payload.Fields.Where(f => string.IsNullOrWhiteSpace(f.ParameterCode) && !MesParameterRouting.IsCovered(f.TagId, payload.Structs)).Select(f => Resolve(payload, f)))
            .Concat(payload.Append.Keys)
            .Concat((payload.FixedValues ?? []).Select(item => item.Name))
            .Concat((payload.ExtraFields ?? []).Select(item => item?.Name?.Split('.')[0])));
        MesParameterRouting.Validate(MesParameterRouting.Routes(payload, elements));
    }
}

public static class MesPayloadModes
{
    public const string DataRanges = "DataRanges";
    public const string FieldMapping = "FieldMapping";

    public static string Resolve(MesPayload payload)
    {
        if (string.Equals(payload.Mode, DataRanges, StringComparison.OrdinalIgnoreCase)) return DataRanges;
        if (string.Equals(payload.Mode, FieldMapping, StringComparison.OrdinalIgnoreCase)) return FieldMapping;
        return (payload.DataRanges?.Count ?? 0) > 0 ? DataRanges : FieldMapping;
    }

    public static bool IsDataRanges(MesPayload payload) => Resolve(payload) == DataRanges;

    public static bool IsValid(string? mode) => string.IsNullOrWhiteSpace(mode) ||
        string.Equals(mode, DataRanges, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, FieldMapping, StringComparison.OrdinalIgnoreCase);
}

/// <summary>上报数据范围:引用一个 DB/结构体/数组或单个点,只保存来源、输出路径、少量排除项和字段改名。</summary>
public sealed class MesDataRange
{
    public string SourcePath { get; set; } = "";       // 完整来源路径:通道.设备.DB[.结构体|.数组|.点]
    public string Scope { get; set; } = "Group";       // Group / Struct / Array / Tag
    public string OutputPath { get; set; } = "";       // 点分 JSON 路径;Auto 且空时由来源推导
    public string OutputPathMode { get; set; } = "Auto"; // Auto / Custom
    public string IncludeMode { get; set; } = "All";   // 首版固定 All
    public List<string> Excludes { get; set; } = new(); // 相对 SourcePath 的点位路径
    public string OfflineMode { get; set; } = "Null";  // 首版固定 Null
    public int[] ArrayStart { get; set; } = [];         // Scope=Array 时使用 PLC 实际下界；空=整个数组
    public int[] ArrayLengths { get; set; } = [];       // 每维数量；空=整个数组
    public string ArrayLengthMode { get; set; } = "Fixed"; // Fixed / Tag，二选一
    public string[] ArrayLengthTagIds { get; set; } = [];  // Tag 模式每维一个整数点位
    public string ArrayMemberPath { get; set; } = "";  // 可选结构体成员路径；空=完整元素
    /// <summary>
    /// 报文键改名:结构路径(按原名,相对 SourcePath,如 Counter、UDT1.A、Cells[].Temp;数组元素共用 [])→新键名。
    /// 为空时报文用点名/组名，与旧版完全一致。
    /// </summary>
    public Dictionary<string, string>? Names { get; set; }
    /// <summary>
    /// 报文里不输出的数组元素成员(结构路径，如 Cells[].Ok，数组的所有元素一起去掉)。
    /// 只影响报文，不改变采集；数据块/结构体下的普通点仍用 Excludes。
    /// </summary>
    public List<string>? Omit { get; set; }
}

/// <summary>一个上报字段(点 → JSON 字段)。</summary>
public sealed class MesField
{
    public string TagId { get; set; } = "";                  // IGW 点完整路径
    public string FieldName { get; set; } = "";              // JSON 字段名(别名);空=用点名
    public string? ParameterCode { get; set; }               // MES 参数码(可选)
    public double Scale { get; set; } = 1.0;                 // 数值缩放(默认 1)
    public string? Format { get; set; }                      // 数值/时间格式(可选)
}

/// <summary>结构体数组上报:把 基准组 下 N 个元素的指定成员拼成 JSON 数组。</summary>
public sealed class MesStruct
{
    public bool AsDictionary { get; set; }                  // 整个子树按完整 PLC 点路径平铺到根 parameterDic
    public string Kind { get; set; } = "Array";              // Array(名字[下标].成员→JSON对象数组) / Object(子组→嵌套对象) / List(子组成员→值数组)
    public string BaseGroup { get; set; } = "";              // 基准组路径(如 …OCVStruct;Object 时=子组路径;Enum 时=前缀路径)
    public string AliasName { get; set; } = "";              // JSON 字段名
    public string CountMode { get; set; } = "Fixed";         // Fixed(固定下标 base[i]) / Tag(数量取点) / Enum(枚举:自动扫 BaseGroup 下所有「前缀[下标]」叶子组,不管嵌套/补零)
    public int StartIndex { get; set; }                      // 起始下标(0 基或 1 基;自动识别会按实际填)
    public int FixedCount { get; set; } = 1;
    public string? CountTagId { get; set; }                  // CountMode=Tag 的数量点
    public bool TakeIndex { get; set; }                      // 参数码是否带下标
    public List<MesField> Members { get; set; } = new();     // 每元素要取的成员 → 字段
    public List<MesStruct> Structs { get; set; } = new();    // 嵌套的子结构体(结构体里套结构体)：BaseGroup 写相对上一层元素的路径，如 GlueData / CellGlueData
}

/// <summary>上报步骤(可多步序列;对标 HsjmDcs Step)。</summary>
public sealed class MesStep
{
    public string Name { get; set; } = "";
    public int DelayMsBefore { get; set; }                   // 步前延时
    public List<MesCondition> Conditions { get; set; } = new(); // 额外触发条件(全部满足才执行)
    public string Transport { get; set; } = "Http";          // Http / Mq
    public MesHttp Http { get; set; } = new();
    public MesMq Mq { get; set; } = new();
    public MesWriteBack WriteBack { get; set; } = new();
    public List<MesCapture> Captures { get; set; } = new();  // 把响应字段存成变量(任务内跨步/回写/模板复用 {变量名})
}

/// <summary>把 MES 响应字段捕获成任务级变量,后续步骤的模板/回写可用 {变量名} 引用。</summary>
public sealed class MesCapture
{
    public string ResponseField { get; set; } = "";
    public string VarName { get; set; } = "";
}

public sealed class MesCondition
{
    public string Source { get; set; } = "Tag";             // Tag / Input / Mapped / Response / Variable
    public string Path { get; set; } = "";                  // Non-Tag source path; Tag keeps TagId for compatibility
    public string TagId { get; set; } = "";
    public string Op { get; set; } = "Eq";                   // Eq/Ne/Gt/Ge/Lt/Le/Regex
    public string Value { get; set; } = "";
    public string ArrayQuantifier { get; set; } = "Any"; // 数组条件:Any / All
    public int[] ArrayStart { get; set; } = [];           // 空=整个数组
    public int[] ArrayLengths { get; set; } = [];
    public string ArrayMemberPath { get; set; } = "";
}

/// <summary>HTTP 上报参数。</summary>
public sealed class MesHttp
{
    public string JsonTemplate { get; set; } = "";
    public List<MesJsonMapping> ResponseMappings { get; set; } = new();
    public string Url { get; set; } = "";                    // 空=用 MesUploadSettings.DefaultMesUrl
    public string Method { get; set; } = "POST";
    public int TimeoutSec { get; set; } = 30;                 // 等待 MES 响应超时;超时取消本次请求,0=使用全局超时
    public string SuccessKey { get; set; } = "httpStatusCode"; // 响应里判成功的键
    public string SuccessValue { get; set; } = "200";        // 成功值
    public string ParameterDicName { get; set; } = "parameterDic"; // 响应里回写字典的键
    // 自定义报文模板(可选):留空=按 Payload.Fields 自动拼平铺 JSON;
    // 填了=用此模板,占位符 {字段名} 或 {完整点路径} 在运行时换成实时值,附加字段同样可用 {键}。
    public string BodyTemplate { get; set; } = "";
}

public sealed class MesJsonMapping
{
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
}

public sealed class MesInterfaceListener
{
    public string Protocol { get; set; } = "SignalR";
    public string ReceiveMethod { get; set; } = "ReceiveBarcode";
    public string SubscribeMethod { get; set; } = "";
    public string ReplyMethod { get; set; } = "";
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string SubscriptionTemplate { get; set; } = "";
    public string ResponseTemplate { get; set; } = "";
    public string IgnoreResponseTemplate { get; set; } = "";
    public string EventName { get; set; } = "";
    public string IdField { get; set; } = "requestId";
    public List<string> ChangeFields { get; set; } = new();
    public bool TriggerOnFirst { get; set; } = true;
    public int ReconnectMs { get; set; } = 1000;
    public int IdleTimeoutSec { get; set; } = 90;
    public int MaxMessageBytes { get; set; } = 1048576;
    public List<MesJsonMapping> InputMappings { get; set; } = new();
}

/// <summary>RabbitMQ 发布(可选)。</summary>
public sealed class MesMq
{
    public string Exchange { get; set; } = "";
    public string RoutingKey { get; set; } = "";
}

/// <summary>回写 PLC:把 MES 返回值/固定值写回点位,可读回确认。</summary>
public sealed class MesWriteBack
{
    public List<MesResponseWrite> FromResponse { get; set; } = new(); // MES 响应字段 → 点
    public List<MesManualWrite> Manual { get; set; } = new();         // 固定/复制写
    public bool Confirm { get; set; } = true;                // 写后读回确认

    private int _retries = DefaultRetries;

    /// <summary>
    /// 回读不一致时最多重写几次。默认 10，与引擎历来的行为一致。
    ///
    /// 这个字段过去没有接通过：界面上没有入口，值是序列化自己写进去的——
    /// int 不是 null，WhenWritingNull 拦不住，于是每次保存工程都把当时的
    /// C# 默认值 2 固化进 JSON。所以存量工程里那个 2 不是谁配的，
    /// 是个从未生效过的默认值；而引擎那边一直按 10 在跑。
    ///
    /// 只改默认值救不了存量工程:JSON 里有 2 就用 2,轮不到默认值,
    /// 现场重写上限会从 10 掉到 2,写入成功率下降且绝不会有人归因到这次改动。
    /// 所以在 setter 里把旧默认值一律当作"没配过"归一到 10,
    /// 反序列化、接口导入、配置器各条路径一次覆盖。
    ///
    /// 代价是配不出正好 2 次——要少重试就填 1 或 0。用这个换掉
    /// "现场悄悄从 10 退到 2"是值的。
    /// </summary>
    public int Retries
    {
        get => _retries;
        set => _retries = value == LegacyRetries ? DefaultRetries : value;
    }

    /// <summary>引擎历来的重写上限，也是这个字段现在的默认值。</summary>
    public const int DefaultRetries = 10;

    /// <summary>接通之前那个从未生效的默认值,加载时一律视为"没配过"。</summary>
    private const int LegacyRetries = 2;
}

public sealed class MesResponseWrite
{
    public string ResponseField { get; set; } = "";          // MES 响应(parameterDic)里的字段
    public string TagId { get; set; } = "";                  // 写到哪个点
}

public sealed class MesManualWrite
{
    public string TagId { get; set; } = "";
    public string? Value { get; set; }                       // 固定值
    public string? CopyFromTagId { get; set; }               // 或从另一点复制
    public bool WriteAlone { get; set; }                     // 单独写(不进批量)
}

/// <summary>心跳:周期把一个计数(0..上限循环)写给某点,让 MES/PLC 知道在线。</summary>
public sealed class MesHeartbeat
{
    public bool Enabled { get; set; }
    public string TagId { get; set; } = "";
    public int MaxLimit { get; set; } = 1000;
    public int IntervalMs { get; set; } = 1000;
}

/// <summary>握手(可选):MES 标志位 + PLC 标志位,用于上报前后同步。</summary>
public sealed class MesHandshake
{
    public string MesFlagTagId { get; set; } = "";
    public string PlcFlagTagId { get; set; } = "";
}
