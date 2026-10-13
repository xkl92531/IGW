namespace IGW.Core;

/// <summary>带工程版本前置条件的配置应用结果。</summary>
public sealed record ProjectUpdateResult(
    bool Applied,
    bool Conflict,
    string Revision,
    string? Error = null,
    bool MesTasksReconciled = false,
    string? Detail = null)
{
    public static ProjectUpdateResult Success(string revision) => new(true, false, revision);
    public static ProjectUpdateResult RevisionConflict(string revision) =>
        new(false, true, revision, "project_changed");
}
