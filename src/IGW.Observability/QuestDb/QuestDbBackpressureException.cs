namespace IGW.Observability;

/// <summary>Normal history-import flow control; not a failed database write.</summary>
public sealed class QuestDbBackpressureException() : IOException("旧日志后台导入正在等待数据库追赶。");
