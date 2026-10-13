using IGW.Abstractions;

namespace IGW.Core;

/// <summary>工程节点路径变化后，迁移 MES 任务中明确表示 PLC 组或点位路径的字段。</summary>
public static class MesReferenceMigration
{
    public static int Rewrite(MesTask task, string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(task);
        string source = (oldPath ?? string.Empty).Trim().TrimEnd('.');
        string target = (newPath ?? string.Empty).Trim().TrimEnd('.');
        if (source.Length == 0 || target.Length == 0 || string.Equals(source, target, StringComparison.Ordinal))
            return 0;

        int changed = 0;
        string RewriteRequired(string value)
        {
            string current = value ?? string.Empty;
            string next = RewritePath(current, source, target);
            if (!string.Equals(current, next, StringComparison.Ordinal)) changed++;
            return next;
        }
        string? RewriteOptional(string? value)
        {
            if (value is null) return null;
            return RewriteRequired(value);
        }
        void RewriteConditions(IEnumerable<MesCondition> conditions)
        {
            foreach (MesCondition condition in conditions)
                if (string.Equals(condition.Source, "Tag", StringComparison.OrdinalIgnoreCase))
                    condition.TagId = RewriteRequired(condition.TagId);
        }
        void RewritePayload(MesPayload payload)
        {
            foreach (MesDataRange range in payload.DataRanges)
            {
                string prior = range.SourcePath;
                range.SourcePath = RewriteRequired(range.SourcePath);
                range.ArrayLengthTagIds = range.ArrayLengthTagIds.Select(RewriteRequired).ToArray();
                if (!string.Equals(prior, range.SourcePath, StringComparison.Ordinal)
                    && string.Equals(range.OutputPathMode, "Auto", StringComparison.OrdinalIgnoreCase))
                    range.OutputPath = string.Join('.', range.SourcePath.Split('.').Skip(1));
            }
            foreach (MesField field in payload.Fields) field.TagId = RewriteRequired(field.TagId);
            foreach (MesExtraField field in payload.ExtraFields)
                if (string.Equals(field.Source, "Tag", StringComparison.OrdinalIgnoreCase))
                    field.Path = RewriteRequired(field.Path);
            foreach (MesStruct structure in payload.Structs)
            {
                structure.BaseGroup = RewriteRequired(structure.BaseGroup);
                structure.CountTagId = RewriteOptional(structure.CountTagId);
                foreach (MesField member in structure.Members) member.TagId = RewriteRequired(member.TagId);
            }
        }
        void RewriteWriteBack(MesWriteBack writeBack)
        {
            foreach (MesResponseWrite item in writeBack.FromResponse) item.TagId = RewriteRequired(item.TagId);
            foreach (MesManualWrite item in writeBack.Manual)
            {
                item.TagId = RewriteRequired(item.TagId);
                item.CopyFromTagId = RewriteOptional(item.CopyFromTagId);
            }
        }

        string? previousGroupRef = task.GroupRef;
        task.GroupRef = RewriteOptional(task.GroupRef);
        if (!string.Equals(previousGroupRef, task.GroupRef, StringComparison.Ordinal) && task.GroupRef is not null)
        {
            string[] segments = task.GroupRef.Split('.');
            if (segments.Length >= 2)
            {
                string deviceRef = string.Join('.', segments.Take(2));
                if (!string.Equals(task.DeviceRef, deviceRef, StringComparison.Ordinal)) changed++;
                task.DeviceRef = deviceRef;
            }
        }
        task.Trigger.TagId = RewriteRequired(task.Trigger.TagId);
        task.Heartbeat.TagId = RewriteRequired(task.Heartbeat.TagId);
        task.Handshake.MesFlagTagId = RewriteRequired(task.Handshake.MesFlagTagId);
        task.Handshake.PlcFlagTagId = RewriteRequired(task.Handshake.PlcFlagTagId);
        RewritePayload(task.Payload);
        foreach (MesStep step in task.Steps)
        {
            RewriteConditions(step.Conditions);
            RewriteWriteBack(step.WriteBack);
        }
        if (task.Flow is not null)
        {
            foreach (MesNode node in task.Flow.Nodes)
            {
                RewriteConditions(node.Conditions);
                foreach (MesCondBranch branch in node.Branches) RewriteConditions(branch.Conditions);
                RewritePayload(node.Payload);
                RewriteWriteBack(node.WriteBack);
                node.SwitchTag = RewriteRequired(node.SwitchTag);
            }
        }
        return changed;
    }

    private static string RewritePath(string value, string oldPath, string newPath)
    {
        if (string.Equals(value, oldPath, StringComparison.Ordinal)) return newPath;
        return value.StartsWith(oldPath + ".", StringComparison.Ordinal)
            ? newPath + value[oldPath.Length..]
            : value;
    }
}
