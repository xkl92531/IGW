namespace IGW.Abstractions;

/// <summary>一个 MES 任务的运行状态快照(供 REST/界面查看)。Devices=该任务引用点位/DeviceRef 涉及的设备全名(通道.设备),异常中心根因折叠用。</summary>
public sealed record MesTaskStatus(string Id, string Name, string Group, bool Enabled, long Ok, long Err, string LastError, string LastAt,
    IReadOnlyList<string> Devices, IReadOnlyList<MesNodeStatus> Nodes);

/// <summary>
/// 流程图节点运行状态快照(画布实时高亮用):idle/run/wait/ok/fail + 原因 + 处于当前状态的毫秒数 + 执行次数。
/// Stuck=卡住判定(手工阈值优先,否则自学习 P95×3;样本不足不判);BaselineMs=所用基线(手工值或 P95,未就绪=0);
/// FailStreak=连续失败次数(&gt;0 且正在 run/wait = 重试中,异常中心视为"仍在失败",防止条目在重试窗口内闪烁);
/// Detail=结构化归因(仅异常态携带;MesWaitDetail / MesHttpFailDetail / MesWriteFailDetail,按 kind 区分)。
/// </summary>
public sealed record MesNodeStatus(string Id, string Title, string State, string Info, long AgeMs, long Hits, bool Stuck, long BaselineMs, long FailStreak, object? Detail);

/// <summary>等待类节点归因:逐条件"期望 vs 实际"(未命中的即卡住原因)。</summary>
public sealed record MesWaitDetail(IReadOnlyList<MesCondCheck> Conds)
{
    public string Kind => "wait";
}

/// <summary>一个等待条件的核对:Actual=求值现场捕获的实际值;Bad=点位不可读/质量异常;Branch=所属分支标签(条件节点多分支等待时)。</summary>
public sealed record MesCondCheck(string TagId, string Op, string Expect, string Actual, bool Met, bool Bad, string? Branch = null);

/// <summary>HTTP 上报失败归因:网络层失败时 StatusCode=0,错误摘要在 BodySnippet。</summary>
public sealed record MesHttpFailDetail(int StatusCode, string BodySnippet, long Attempt, string Url)
{
    public string Kind => "http";
    public string Category => StatusCode == 0 ? "mes-no-response"
        : StatusCode is >= 200 and < 300 ? "business-ng"
        : "mes-http-error";
}

/// <summary>上报已成功送达，但报文包含离线数据范围或质量异常点位。</summary>
public sealed record MesDataQualityDetail(
    IReadOnlyList<string> OfflineSources,
    IReadOnlyList<string> BadTags)
{
    public string Kind => "data-quality";
    public string Category => "plc-communication";
}

/// <summary>
/// 回写失败归因:只携带失败的点(下发值/读回值/阶段/原因/重试次数)。
/// 状态快照被界面高频轮询，失败点很多时只带前 <see cref="MaxItems"/> 个，Total 给出真实失败数。
/// </summary>
public sealed record MesWriteFailDetail(IReadOnlyList<MesWriteFailItem> Items, int Total)
{
    public const int MaxItems = 50;

    public MesWriteFailDetail(IReadOnlyList<MesWriteFailItem> items)
        : this(items.Count > MaxItems ? items.Take(MaxItems).ToArray() : items, items.Count) { }

    public string Kind => "write";
}

public sealed record MesWriteFailItem(
    string TagId,
    string Written,
    string ReadBack,
    int Attempts,
    uint StatusCode = 0x80000000,
    string StatusName = "Bad",
    string Stage = "未知",
    string Error = "");

/// <summary>节点流转事件(每任务内存环形缓冲):一次状态变化,DurMs=上一状态段的持续毫秒。结构体:缓冲数组复用,追加零堆分配。</summary>
public readonly record struct MesFlowEvent(string NodeId, string State, string Info, DateTime TsUtc, long DurMs);
