using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>一次局部修改对运行态的影响范围(由 <see cref="ProjectMutationPatcher"/> 给出)。</summary>
public sealed class ProjectPatchSummary
{
    /// <summary>内容有变化的设备(新工程里的全名)。</summary>
    public HashSet<string> ChangedDevices { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>从运行态移除的设备(旧工程里的全名，含改名前的旧名)。</summary>
    public HashSet<string> RemovedDevices { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>被删除或改名(路径失效)的点位/组旧路径前缀。</summary>
    public List<string> RemovedPaths { get; } = [];
    public bool ChannelsAddedOrRemoved { get; set; }
    /// <summary>删除或改名前的通道名(OPC UA 需要移除对应的通道文件夹)。</summary>
    public HashSet<string> RemovedChannels { get; } = new(StringComparer.Ordinal);
    /// <summary>实时点值流参数是否变化(需要重建实时流)。</summary>
    public bool TagStreamChanged { get; set; }
    public bool MesChanged { get; set; }
    public bool SettingsChanged { get; set; }
    public bool OpcUaChanged { get; set; }
    public bool HistoryChanged { get; set; }

    public bool SouthboundChanged => ChangedDevices.Count > 0 || RemovedDevices.Count > 0;
}

public sealed record ProjectPatch(IgwProject Project, ProjectPatchSummary Summary);

/// <summary>
/// 把已经写入数据库的局部修改直接套到内存中的工程上，避免百万点工程每次保存都从数据库整体重读。
/// 只复制受影响的通道/设备(结构复制，点位对象共享)，其余部分与旧工程共用同一批对象，旧工程保持不变。
/// 语义与 SQLite 局部修改执行器一致；遇到不支持的修改返回 null，由调用方回退到整体重读。
/// </summary>
public static class ProjectMutationPatcher
{
    /// <summary>
    /// “点位/组 → 所在设备”索引跟着工程实例缓存：百万点工程每次保存都重建要分配上百 MB。
    /// 合并成功后把更新过的索引转给新工程；合并失败时索引已不可信，直接丢弃，下次重建。
    /// </summary>
    private sealed record LocationIndex(Dictionary<string, string> Groups, Dictionary<string, string> Tags);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IgwProject, LocationIndex> s_indexes = new();

    private static readonly System.Reflection.MethodInfo s_memberwiseClone = typeof(object).GetMethod(
        "MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    public static ProjectPatch? TryApply(IgwProject current, ProjectMutation mutation)
    {
        try
        {
            s_indexes.TryGetValue(current, out LocationIndex? cached);
            s_indexes.Remove(current);
            var context = new PatchContext(current, cached);
            IReadOnlyList<ProjectMutation> items = mutation.Kind == ProjectMutationKind.Batch
                ? JsonSerializer.Deserialize<List<ProjectMutation>>(mutation.DocumentJson ?? "[]", ProjectStore.Options) ?? []
                : [mutation];
            foreach (ProjectMutation item in items)
                if (!context.Apply(item)) return null;
            ProjectPatch patch = context.Finish();
            s_indexes.AddOrUpdate(patch.Project, context.Index);
            return patch;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return null;
        }
    }

    private sealed class PatchContext
    {
        private readonly IgwProject _old;
        private readonly IgwProject _project;
        private readonly ProjectPatchSummary _summary = new();
        private readonly HashSet<ChannelDef> _clonedChannels = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<DeviceDef> _clonedDevices = new(ReferenceEqualityComparer.Instance);
        /// <summary>原工程设备对象 → 所在原通道；用于在新工程里找到对应副本。</summary>
        private Dictionary<string, (ChannelDef Channel, DeviceDef Device)>? _devicesById;
        private Dictionary<string, string>? _groupDevice;
        private Dictionary<string, string>? _tagDevice;
        private readonly Dictionary<string, string> _oldDeviceNames = new(StringComparer.Ordinal);

        public PatchContext(IgwProject old, LocationIndex? cached)
        {
            _old = old;
            _groupDevice = cached?.Groups;
            _tagDevice = cached?.Tags;
            _project = new IgwProject
            {
                SchemaVersion = old.SchemaVersion, Name = old.Name, Description = old.Description, KepProperties = old.KepProperties,
                Settings = old.Settings, OpcUa = old.OpcUa, History = old.History, MqttPublish = old.MqttPublish,
                ComputedTags = old.ComputedTags, Schedules = old.Schedules, Security = old.Security, MesUpload = old.MesUpload,
                Templates = old.Templates, Channels = [.. old.Channels],
            };
        }

        /// <summary>两个索引都已建立时交给新工程缓存。</summary>
        public LocationIndex Index => new(GroupDevice(), TagDevice());

        /// <summary>整块移除的设备/组：其下所有组和点位的位置记录一并删掉(同一批里可能用原 ID 重建)。</summary>
        private void ForgetSubtree(IEnumerable<TagGroupDef> groups, IEnumerable<TagDef> tags)
        {
            foreach (TagDef tag in tags) _tagDevice?.Remove(tag.Id);
            foreach (TagGroupDef group in groups)
            {
                _groupDevice?.Remove(group.Id);
                ForgetSubtree(group.SubGroups, group.Tags);
            }
        }

        public ProjectPatch Finish()
        {
            foreach (ChannelDef channel in _project.Channels)
                foreach (DeviceDef device in channel.Devices)
                    if (_clonedDevices.Contains(device)) _summary.ChangedDevices.Add(channel.Name + "." + device.Name);
            // 改名/移动：旧全名不再存在时按移除处理。
            var current = _project.Channels.SelectMany(c => c.Devices.Select(d => c.Name + "." + d.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string oldName in _oldDeviceNames.Values)
                if (!current.Contains(oldName)) _summary.RemovedDevices.Add(oldName);
            return new ProjectPatch(_project, _summary);
        }

        public bool Apply(ProjectMutation item)
        {
            switch (item.Kind)
            {
                case ProjectMutationKind.UpsertEntity:
                    if (string.IsNullOrWhiteSpace(item.DocumentJson)) return false;
                    return item.EntityKind switch
                    {
                        ProjectNodeKind.Channel => UpsertChannel(Read<ChannelDef>(item.DocumentJson)),
                        ProjectNodeKind.Device => UpsertDevice(item.TargetId, Read<DeviceDef>(item.DocumentJson)),
                        ProjectNodeKind.Group => UpsertGroup(item.TargetId, Read<TagGroupDef>(item.DocumentJson)),
                        ProjectNodeKind.Tag => UpsertTag(item.TargetId, Read<TagDef>(item.DocumentJson)),
                        _ => false,
                    };
                case ProjectMutationKind.DeleteEntities:
                    foreach (string id in item.EntityIds)
                        if (!Delete(item.EntityKind, id)) return false;
                    return true;
                case ProjectMutationKind.ReplaceProjectSections:
                    return ReplaceSections(item);
                case ProjectMutationKind.SaveMesTasks:
                    return SaveMesTasks(item);
                default:
                    return false;
            }
        }

        private static T Read<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, ProjectStore.Options) ?? throw new InvalidDataException("修改文档为空。");

        // ---------- 查找与写时复制 ----------

        private Dictionary<string, (ChannelDef Channel, DeviceDef Device)> DevicesById()
        {
            if (_devicesById is not null) return _devicesById;
            _devicesById = new(StringComparer.Ordinal);
            foreach (ChannelDef channel in _project.Channels)
                foreach (DeviceDef device in channel.Devices)
                    _devicesById[device.Id] = (channel, device);
            return _devicesById;
        }

        private ChannelDef ChannelById(string id) =>
            _project.Channels.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"通道 {id} 不存在。");

        private ChannelDef CloneChannel(ChannelDef channel)
        {
            if (_clonedChannels.Contains(channel)) return channel;
            var copy = new ChannelDef
            {
                Id = channel.Id, Name = channel.Name, DriverId = channel.DriverId, Description = channel.Description,
                Settings = channel.Settings, KepProperties = channel.KepProperties, Devices = [.. channel.Devices],
            };
            _project.Channels[_project.Channels.IndexOf(channel)] = copy;
            _clonedChannels.Add(copy);
            if (_devicesById is not null)
                foreach (DeviceDef device in copy.Devices) _devicesById[device.Id] = (copy, device);
            return copy;
        }

        /// <summary>取设备在新工程中的可修改副本(连同所在通道)。</summary>
        private (ChannelDef Channel, DeviceDef Device) EditableDevice(string deviceId)
        {
            if (!DevicesById().TryGetValue(deviceId, out var found)) throw new KeyNotFoundException($"设备 {deviceId} 不存在。");
            if (_clonedDevices.Contains(found.Device)) return found;
            ChannelDef channel = CloneChannel(found.Channel);
            _oldDeviceNames.TryAdd(deviceId, found.Channel.Name + "." + found.Device.Name);
            DeviceDef copy = CloneDeviceStructure(found.Device);
            channel.Devices[channel.Devices.IndexOf(found.Device)] = copy;
            _clonedDevices.Add(copy);
            _devicesById![deviceId] = (channel, copy);
            return (channel, copy);
        }

        private static DeviceDef CloneDeviceStructure(DeviceDef device) => new()
        {
            Id = device.Id, Name = device.Name, Endpoint = device.Endpoint, Description = device.Description,
            Settings = device.Settings, KepProperties = device.KepProperties, TemplateRef = device.TemplateRef, Delta = device.Delta,
            Groups = device.Groups.Select(CloneGroupStructure).ToList(), Tags = [.. device.Tags],
        };

        private static TagGroupDef CloneGroupStructure(TagGroupDef group) => new()
        {
            Id = group.Id, Name = group.Name, Description = group.Description, KepProperties = group.KepProperties,
            SubGroups = group.SubGroups.Select(CloneGroupStructure).ToList(), Tags = [.. group.Tags],
        };

        private Dictionary<string, string> GroupDevice()
        {
            if (_groupDevice is not null) return _groupDevice;
            _groupDevice = new(StringComparer.Ordinal);
            void Walk(List<TagGroupDef> groups, string deviceId)
            {
                foreach (TagGroupDef group in groups)
                {
                    _groupDevice[group.Id] = deviceId;
                    Walk(group.SubGroups, deviceId);
                }
            }
            foreach (ChannelDef channel in _project.Channels)
                foreach (DeviceDef device in channel.Devices) Walk(device.Groups, device.Id);
            return _groupDevice;
        }

        private Dictionary<string, string> TagDevice()
        {
            if (_tagDevice is not null) return _tagDevice;
            _tagDevice = new(StringComparer.Ordinal);
            void Walk(List<TagGroupDef> groups, string deviceId)
            {
                foreach (TagGroupDef group in groups)
                {
                    foreach (TagDef tag in group.Tags) _tagDevice[tag.Id] = deviceId;
                    Walk(group.SubGroups, deviceId);
                }
            }
            foreach (ChannelDef channel in _project.Channels)
                foreach (DeviceDef device in channel.Devices)
                {
                    foreach (TagDef tag in device.Tags) _tagDevice[tag.Id] = device.Id;
                    Walk(device.Groups, device.Id);
                }
            return _tagDevice;
        }

        /// <summary>设备或组(按 ID)在新工程中的可修改容器：下级组列表 + 点位列表 + 路径。</summary>
        private Container EditableContainer(string id)
        {
            if (DevicesById().ContainsKey(id))
            {
                var (channel, device) = EditableDevice(id);
                return new Container(device.Id, null, device.Groups, device.Tags, channel.Name + "." + device.Name);
            }
            if (!GroupDevice().TryGetValue(id, out string? deviceId)) throw new KeyNotFoundException($"父节点 {id} 不存在。");
            var (ownerChannel, ownerDevice) = EditableDevice(deviceId);
            return FindGroupContainer(ownerDevice, ownerChannel.Name + "." + ownerDevice.Name, id)
                ?? throw new KeyNotFoundException($"标记组 {id} 不存在。");
        }

        private static Container? FindGroupContainer(DeviceDef device, string devicePath, string groupId)
        {
            Container? Walk(List<TagGroupDef> groups, string parentPath, string parentId)
            {
                foreach (TagGroupDef group in groups)
                {
                    string path = parentPath + "." + group.Name;
                    if (group.Id == groupId) return new Container(group.Id, group, group.SubGroups, group.Tags, path) { ParentGroups = groups, ParentId = parentId };
                    if (Walk(group.SubGroups, path, group.Id) is { } found) return found;
                }
                return null;
            }
            return Walk(device.Groups, devicePath, device.Id);
        }

        /// <summary>在已复制的设备里查找点位所在列表。</summary>
        private static (List<TagDef> List, string ParentPath, string ParentId)? FindTag(DeviceDef device, string devicePath, string tagId)
        {
            if (device.Tags.Any(t => t.Id == tagId)) return (device.Tags, devicePath, device.Id);
            (List<TagDef>, string, string)? Walk(List<TagGroupDef> groups, string parentPath)
            {
                foreach (TagGroupDef group in groups)
                {
                    string path = parentPath + "." + group.Name;
                    if (group.Tags.Any(t => t.Id == tagId)) return (group.Tags, path, group.Id);
                    if (Walk(group.SubGroups, path) is { } found) return found;
                }
                return null;
            }
            return Walk(device.Groups, devicePath);
        }

        private sealed record Container(string Id, TagGroupDef? Group, List<TagGroupDef> Groups, List<TagDef> Tags, string Path)
        {
            public List<TagGroupDef>? ParentGroups { get; init; }
            public string? ParentId { get; init; }
        }

        // ---------- 各类修改 ----------

        private bool UpsertChannel(ChannelDef document)
        {
            if (string.IsNullOrWhiteSpace(document.Id) || document.Devices.Count > 0) return false;
            ChannelDef? existing = _project.Channels.FirstOrDefault(c => c.Id == document.Id);
            if (existing is null)
            {
                _project.Channels.Add(new ChannelDef
                {
                    Id = document.Id, Name = document.Name, DriverId = document.DriverId, Description = document.Description,
                    Settings = document.Settings, KepProperties = document.KepProperties,
                });
                _summary.ChannelsAddedOrRemoved = true;
                return true;
            }
            bool runtimeChanged = existing.Name != document.Name || existing.DriverId != document.DriverId
                || JsonHash.Of(existing.Settings) != JsonHash.Of(document.Settings);
            ChannelDef channel = CloneChannel(existing);
            if (existing.Name != document.Name) _summary.ChannelsAddedOrRemoved = true;   // OPC UA 通道文件夹需要重建
            if (runtimeChanged)
                foreach (DeviceDef device in channel.Devices.ToArray()) EditableDevice(device.Id);
            if (existing.Name != document.Name)
            {
                _summary.RemovedPaths.Add(existing.Name);
                _summary.RemovedChannels.Add(existing.Name);
            }
            channel.Name = document.Name;
            channel.DriverId = document.DriverId;
            channel.Description = document.Description;
            channel.Settings = document.Settings;
            channel.KepProperties = document.KepProperties;
            return true;
        }

        private bool UpsertDevice(string? targetChannelId, DeviceDef document)
        {
            if (string.IsNullOrWhiteSpace(document.Id) || document.Groups.Count + document.Tags.Count > 0) return false;
            if (!DevicesById().ContainsKey(document.Id))
            {
                if (targetChannelId is null) return false;
                ChannelDef target = CloneChannel(ChannelById(targetChannelId));
                var created = new DeviceDef
                {
                    Id = document.Id, Name = document.Name, Endpoint = document.Endpoint, Description = document.Description,
                    Settings = document.Settings, KepProperties = document.KepProperties, TemplateRef = document.TemplateRef, Delta = document.Delta,
                };
                target.Devices.Add(created);
                _clonedDevices.Add(created);
                _devicesById![created.Id] = (target, created);
                return true;
            }
            var (channel, device) = EditableDevice(document.Id);
            string oldPath = channel.Name + "." + device.Name;
            if (targetChannelId is not null && targetChannelId != channel.Id)
            {
                ChannelDef target = CloneChannel(ChannelById(targetChannelId));
                channel.Devices.Remove(device);
                target.Devices.Add(device);
                _devicesById![device.Id] = (target, device);
                channel = target;
            }
            device.Name = document.Name;
            device.Endpoint = document.Endpoint;
            device.Description = document.Description;
            device.Settings = document.Settings;
            device.KepProperties = document.KepProperties;
            device.TemplateRef = document.TemplateRef;
            device.Delta = document.Delta;
            if (!string.Equals(oldPath, channel.Name + "." + device.Name, StringComparison.Ordinal)) _summary.RemovedPaths.Add(oldPath);
            return true;
        }

        private bool UpsertGroup(string? targetId, TagGroupDef document)
        {
            if (string.IsNullOrWhiteSpace(document.Id) || document.SubGroups.Count + document.Tags.Count > 0) return false;
            Container? current = null;
            if (GroupDevice().ContainsKey(document.Id))
            {
                try { current = EditableContainer(document.Id); }
                catch (KeyNotFoundException) { _groupDevice!.Remove(document.Id); }
            }
            if (current is null)
            {
                if (targetId is null) return false;
                Container parent = EditableContainer(targetId);
                parent.Groups.Add(new TagGroupDef
                {
                    Id = document.Id, Name = document.Name, Description = document.Description, KepProperties = document.KepProperties,
                });
                _groupDevice![document.Id] = DeviceOf(targetId);
                return true;
            }
            TagGroupDef group = current.Group!;
            if (targetId is not null && targetId != current.ParentId)
            {
                // 跨设备移动时数据库只改了组本身的设备归属，这里不做推断，交给整体重读。
                if (DeviceOf(targetId) != DeviceOf(document.Id)) return false;
                Container parent = EditableContainer(targetId);
                current.ParentGroups!.Remove(group);
                parent.Groups.Add(group);
                _summary.RemovedPaths.Add(current.Path);
            }
            else if (group.Name != document.Name) _summary.RemovedPaths.Add(current.Path);
            group.Name = document.Name;
            group.Description = document.Description;
            group.KepProperties = document.KepProperties;
            return true;
        }

        private string DeviceOf(string containerId) =>
            DevicesById().ContainsKey(containerId) ? containerId
            : GroupDevice().TryGetValue(containerId, out string? deviceId) ? deviceId
            : throw new KeyNotFoundException($"节点 {containerId} 不存在。");

        private bool UpsertTag(string? targetId, TagDef document)
        {
            if (string.IsNullOrWhiteSpace(document.Id)) return false;
            string? currentDevice = TagDevice().GetValueOrDefault(document.Id);
            (List<TagDef> List, string ParentPath, string ParentId)? located = null;
            if (currentDevice is not null && DevicesById().ContainsKey(currentDevice))
            {
                var (ownerChannel, ownerDevice) = EditableDevice(currentDevice);
                located = FindTag(ownerDevice, ownerChannel.Name + "." + ownerDevice.Name, document.Id);
            }
            if (located is not { } found)
            {
                if (targetId is null) return false;
                EditableContainer(targetId).Tags.Add(document);
                _tagDevice![document.Id] = DeviceOf(targetId);
                return true;
            }
            int index = found.List.FindIndex(t => t.Id == document.Id);
            TagDef old = found.List[index];
            if (targetId is not null && targetId != found.ParentId)
            {
                found.List.RemoveAt(index);
                EditableContainer(targetId).Tags.Add(document);
                _tagDevice![document.Id] = DeviceOf(targetId);
                _summary.RemovedPaths.Add(found.ParentPath + "." + old.Name);
                return true;
            }
            found.List[index] = document;
            if (old.Name != document.Name) _summary.RemovedPaths.Add(found.ParentPath + "." + old.Name);
            return true;
        }

        private bool Delete(ProjectNodeKind kind, string id)
        {
            switch (kind)
            {
                case ProjectNodeKind.Channel:
                {
                    ChannelDef channel = ChannelById(id);
                    foreach (DeviceDef device in channel.Devices)
                    {
                        _oldDeviceNames.TryAdd(device.Id, channel.Name + "." + device.Name);
                        ForgetSubtree(device.Groups, device.Tags);
                    }
                    _project.Channels.Remove(channel);
                    _summary.RemovedPaths.Add(channel.Name);
                    _summary.RemovedChannels.Add(channel.Name);
                    _summary.ChannelsAddedOrRemoved = true;
                    _devicesById = null;
                    return true;
                }
                case ProjectNodeKind.Device:
                {
                    var (channel, device) = EditableDevice(id);
                    channel.Devices.Remove(device);
                    _devicesById!.Remove(id);
                    ForgetSubtree(device.Groups, device.Tags);
                    _summary.RemovedPaths.Add(channel.Name + "." + device.Name);
                    return true;
                }
                case ProjectNodeKind.Group:
                {
                    Container current = EditableContainer(id);
                    current.ParentGroups!.Remove(current.Group!);
                    _groupDevice!.Remove(id);
                    ForgetSubtree(current.Group!.SubGroups, current.Group!.Tags);
                    _summary.RemovedPaths.Add(current.Path);
                    return true;
                }
                case ProjectNodeKind.Tag:
                {
                    string deviceId = TagDevice().GetValueOrDefault(id) ?? throw new KeyNotFoundException($"标记 {id} 不存在。");
                    var (channel, device) = EditableDevice(deviceId);
                    var found = FindTag(device, channel.Name + "." + device.Name, id) ?? throw new KeyNotFoundException();
                    TagDef tag = found.List.First(t => t.Id == id);
                    found.List.Remove(tag);
                    _tagDevice!.Remove(id);
                    _summary.RemovedPaths.Add(found.ParentPath + "." + tag.Name);
                    return true;
                }
                default:
                    return false;
            }
        }

        private bool ReplaceSections(ProjectMutation item)
        {
            if (string.IsNullOrWhiteSpace(item.DocumentJson)) return false;
            var sections = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DocumentJson, ProjectStore.Options) ?? [];
            foreach (string key in item.EntityIds)
            {
                if (!sections.TryGetValue(key, out JsonElement section)) return false;
                switch (key)
                {
                    case "settings":
                        ProjectSettings before = _project.Settings;
                        _project.Settings = section.Deserialize<ProjectSettings>(ProjectStore.Options) ?? new();
                        _summary.SettingsChanged = true;
                        _summary.TagStreamChanged |= JsonHash.Of(before.TagStream) != JsonHash.Of(_project.Settings.TagStream);
                        break;
                    case "opcUa":
                        _project.OpcUa = section.Deserialize<OpcUaSettings>(ProjectStore.Options) ?? new();
                        _summary.OpcUaChanged = true;
                        break;
                    case "history":
                        _project.History = section.Deserialize<HistorySettings>(ProjectStore.Options) ?? new();
                        _summary.HistoryChanged = true;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private bool SaveMesTasks(ProjectMutation item)
        {
            if (string.IsNullOrWhiteSpace(item.DocumentJson)) return false;
            var batch = JsonSerializer.Deserialize<MesTaskBatchSave>(item.DocumentJson, ProjectStore.Options);
            if (batch is null) return false;
            MesUploadSettings old = _project.MesUpload;
            // 只复制设置对象和任务列表；未改动的任务对象共享。
            var mes = (MesUploadSettings)s_memberwiseClone.Invoke(old, null)!;
            mes.Tasks = [.. old.Tasks];
            foreach (MesTask task in batch.Tasks)
            {
                int index = mes.Tasks.FindIndex(t => t.Id == task.Id);
                if (index >= 0) mes.Tasks[index] = task;
                else mes.Tasks.Add(task);
            }
            if (batch.CollectIntervalMs is int period)
                foreach (string id in batch.PeriodTaskIds)
                {
                    int index = mes.Tasks.FindIndex(t => t.Id == id);
                    if (index < 0) continue;
                    MesTask copy = JsonSerializer.Deserialize<MesTask>(JsonSerializer.Serialize(mes.Tasks[index], ProjectStore.Options), ProjectStore.Options)!;
                    copy.CollectIntervalMs = period;
                    mes.Tasks[index] = copy;
                }
            _project.MesUpload = mes;
            _summary.MesChanged = true;
            return true;
        }
    }
}
