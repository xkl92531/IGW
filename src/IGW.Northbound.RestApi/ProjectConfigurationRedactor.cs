using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

/// <summary>生成可编辑但不可复用密钥的工程快照，并在保存前恢复未修改的密钥。</summary>
public static class ProjectConfigurationRedactor
{
    public const string UnchangedSecret = "__IGW_SECRET_UNCHANGED__";

    public static IgwProject Create(IgwProject source)
    {
        var copy = ProjectStore.Clone(source);
        foreach (var user in copy.Security?.Users ?? [])
        {
            bool configured = !string.IsNullOrEmpty(user.Password) || !string.IsNullOrEmpty(user.PasswordHash);
            user.Password = configured ? UnchangedSecret : string.Empty;
            user.PasswordHash = string.Empty;
        }

        copy.MqttPublish.Password = Redacted(copy.MqttPublish.Password);
        copy.MesUpload.AuthBearerToken = Redacted(copy.MesUpload.AuthBearerToken);
        foreach (var task in copy.MesUpload.Tasks)
            foreach (string key in task.Listener.Headers.Keys.Where(ProjectSecrets.IsSecretHeader).ToArray())
                task.Listener.Headers[key] = Redacted(task.Listener.Headers[key]);
        foreach (var user in copy.OpcUa.Users) user.Password = Redacted(user.Password);
        RedactSettings(copy);
        return copy;
    }

    public static void MergeUnchangedSecrets(IgwProject live, IgwProject edited)
    {
        var liveUsers = (live.Security?.Users ?? [])
            .ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
        foreach (var user in edited.Security?.Users ?? [])
        {
            if (user.Password == UnchangedSecret && liveUsers.TryGetValue(user.Username, out var current))
            {
                user.Password = current.Password;
                user.PasswordHash = current.PasswordHash;
            }
            else
            {
                // 客户端不能提交或复用哈希；新明文会在成功保存时转换。
                user.PasswordHash = string.Empty;
            }
        }

        edited.MqttPublish.Password = Restored(edited.MqttPublish.Password, live.MqttPublish.Password);
        MergeMesSecrets(live.MesUpload, edited.MesUpload);
        MergeOpcUaUsers(live.OpcUa.Users, edited.OpcUa.Users);
        MergeSettings(live, edited);
    }

    public static void MergeMesSecrets(MesUploadSettings live, MesUploadSettings edited)
    {
        edited.AuthBearerToken = Restored(edited.AuthBearerToken, live.AuthBearerToken);
        foreach (var task in edited.Tasks)
        {
            var original = live.Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (original is null) continue;
            foreach (string key in task.Listener.Headers.Keys.Where(ProjectSecrets.IsSecretHeader).ToArray())
                if (task.Listener.Headers[key] == UnchangedSecret && original.Listener.Headers.TryGetValue(key, out var value))
                    task.Listener.Headers[key] = value;
        }
    }

    /// <summary>按需加载的配置页拿到的是脱敏值;局部修改保存前把未改动的占位符换回当前真实值。</summary>
    public static ProjectMutation RestoreMutationSecrets(IgwProject live, ProjectMutation mutation)
    {
        switch (mutation.Kind)
        {
            case ProjectMutationKind.Batch:
                var items = System.Text.Json.JsonSerializer.Deserialize<List<ProjectMutation>>(
                    mutation.DocumentJson ?? "[]", ProjectStore.Options) ?? [];
                return mutation with
                {
                    DocumentJson = System.Text.Json.JsonSerializer.Serialize(
                        items.Select(item => item.Kind == ProjectMutationKind.Batch ? item : RestoreMutationSecrets(live, item)).ToList(),
                        ProjectStore.Options),
                };
            case ProjectMutationKind.UpsertEntity when mutation.EntityKind == ProjectNodeKind.Channel
                && mutation.DocumentJson?.Contains(UnchangedSecret, StringComparison.Ordinal) == true:
                var channel = System.Text.Json.JsonSerializer.Deserialize<ChannelDef>(mutation.DocumentJson, ProjectStore.Options)!;
                if (live.Channels.FirstOrDefault(c => c.Id == channel.Id) is { } liveChannel)
                    RestoreDictionary(liveChannel.Settings, channel.Settings);
                return mutation with { DocumentJson = System.Text.Json.JsonSerializer.Serialize(channel, ProjectStore.Options) };
            case ProjectMutationKind.UpsertEntity when mutation.EntityKind == ProjectNodeKind.Device
                && mutation.DocumentJson?.Contains(UnchangedSecret, StringComparison.Ordinal) == true:
                var device = System.Text.Json.JsonSerializer.Deserialize<DeviceDef>(mutation.DocumentJson, ProjectStore.Options)!;
                if (live.Channels.SelectMany(c => c.Devices).FirstOrDefault(d => d.Id == device.Id) is { } liveDevice)
                    RestoreDictionary(liveDevice.Settings, device.Settings);
                return mutation with { DocumentJson = System.Text.Json.JsonSerializer.Serialize(device, ProjectStore.Options) };
            case ProjectMutationKind.SaveMesTasks when mutation.DocumentJson?.Contains(UnchangedSecret, StringComparison.Ordinal) == true:
                var batch = System.Text.Json.JsonSerializer.Deserialize<MesTaskBatchSave>(mutation.DocumentJson, ProjectStore.Options)!;
                MergeMesSecrets(live.MesUpload, new MesUploadSettings { Tasks = batch.Tasks });
                return mutation with { DocumentJson = System.Text.Json.JsonSerializer.Serialize(batch, ProjectStore.Options) };
            case ProjectMutationKind.ReplaceProjectSections when mutation.DocumentJson?.Contains(UnchangedSecret, StringComparison.Ordinal) == true:
                var sections = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                    mutation.DocumentJson, ProjectStore.Options)!;
                if (sections.TryGetValue("opcUa", out var opcUaJson))
                {
                    var opcUa = System.Text.Json.JsonSerializer.Deserialize<OpcUaSettings>(opcUaJson, ProjectStore.Options)!;
                    MergeOpcUaUsers(live.OpcUa.Users, opcUa.Users);
                    sections["opcUa"] = System.Text.Json.JsonSerializer.SerializeToElement(opcUa, ProjectStore.Options);
                }
                return mutation with { DocumentJson = System.Text.Json.JsonSerializer.Serialize(sections, ProjectStore.Options) };
            default:
                return mutation;
        }
    }

    private static void MergeOpcUaUsers(IReadOnlyList<OpcUaUser> live, IEnumerable<OpcUaUser> edited)
    {
        var current = live.ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
        foreach (var user in edited)
            if (user.Password == UnchangedSecret && current.TryGetValue(user.Username, out var existing))
                user.Password = existing.Password;
    }

    private static void RedactSettings(IgwProject project)
    {
        foreach (var channel in project.Channels)
        {
            RedactDictionary(channel.Settings);
            foreach (var device in channel.Devices) RedactDictionary(device.Settings);
        }
        foreach (var template in project.Templates) RedactDictionary(template.DefaultSettings);
    }

    private static void MergeSettings(IgwProject live, IgwProject edited)
    {
        var liveChannels = live.Channels.ToDictionary(channel => channel.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in edited.Channels)
        {
            if (!liveChannels.TryGetValue(channel.Name, out var liveChannel)) continue;
            RestoreDictionary(liveChannel.Settings, channel.Settings);
            var liveDevices = liveChannel.Devices.ToDictionary(device => device.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var device in channel.Devices)
                if (liveDevices.TryGetValue(device.Name, out var liveDevice))
                    RestoreDictionary(liveDevice.Settings, device.Settings);
        }

        var liveTemplates = live.Templates.ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var template in edited.Templates)
            if (liveTemplates.TryGetValue(template.Name, out var liveTemplate))
                RestoreDictionary(liveTemplate.DefaultSettings, template.DefaultSettings);
    }

    private static void RedactDictionary(IDictionary<string, string> settings)
    {
        foreach (string key in settings.Keys.Where(ProjectSecrets.IsSecretSettingKey).ToArray())
            settings[key] = Redacted(settings[key]);
    }

    private static void RestoreDictionary(
        IReadOnlyDictionary<string, string> live, IDictionary<string, string> edited)
    {
        foreach (string key in edited.Keys.Where(ProjectSecrets.IsSecretSettingKey).ToArray())
            if (edited[key] == UnchangedSecret && live.TryGetValue(key, out string? value))
                edited[key] = value;
    }

    private static string Redacted(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : UnchangedSecret;

    private static string Restored(string edited, string live) =>
        edited == UnchangedSecret ? live : edited;
}
