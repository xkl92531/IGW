using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

public sealed record ProjectParameterOption(
    string Value,
    string Label,
    string FullPath,
    string Scope,
    string Kind,
    string? Description,
    string? Address);

public sealed record ProjectParameterOptionsResponse(
    IReadOnlyList<ProjectParameterOption> Items,
    bool Truncated);

/// <summary>
/// Searches the persisted project model for MES-configurable tags/groups. This is deliberately
/// independent from DataHub and device drivers so configuration remains available while offline.
/// </summary>
public static class ProjectParameterSearch
{
    private const string TagKind = "tag";
    private const string GroupKind = "group";
    private const string ReadAccess = "read";
    private const string WriteAccess = "write";

    public static async Task<ProjectParameterOptionsResponse> SearchAsync(
        IProjectRepository repository,
        string? query,
        string? scopeGroup,
        string? kind,
        string? access,
        int? limit,
        CancellationToken cancellationToken,
        bool? scopeOnly = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string normalizedKind = NormalizeKind(kind);
        string normalizedAccess = NormalizeAccess(access);
        string needle = (query ?? "").Trim();
        string scope = (scopeGroup ?? "").Trim().Trim('.');
        int take = Math.Clamp(limit ?? 100, 1, 100);
        ProjectNodeKind nodeKind = normalizedKind == TagKind ? ProjectNodeKind.Tag : ProjectNodeKind.Group;
        TagAccess? requiredAccess = nodeKind == ProjectNodeKind.Tag
            ? normalizedAccess == WriteAccess ? TagAccess.Write : TagAccess.Read
            : null;
        var items = new List<ProjectParameterOption>(take + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (scope.Length > 0)
        {
            ProjectPage<ProjectNodeSearchRow> scoped = await repository.SearchNodesAsync(
                new ProjectNodeSearchQuery(scope, needle, nodeKind, requiredAccess, false, 0, take + 1, CountTotal: false),
                cancellationToken).ConfigureAwait(false);
            AddRows(scoped.Items, "CurrentGroup", items, seen, take + 1);
            // Opening an existing selector supplies its complete path. Once that exact scoped
            // value is found, a second project-wide substring scan cannot improve the result.
            if (items.Any(item => string.Equals(item.Value, needle, StringComparison.OrdinalIgnoreCase)))
                return ToResponse(items, take);
        }

        if (!(scopeOnly ?? normalizedAccess == WriteAccess) && needle.Length >= 2 && items.Count <= take)
        {
            ProjectPage<ProjectNodeSearchRow> global = await repository.SearchNodesAsync(
                new ProjectNodeSearchQuery(null, needle, nodeKind, requiredAccess, false, 0, take + 1, CountTotal: false),
                cancellationToken).ConfigureAwait(false);
            foreach (ProjectNodeSearchRow row in global.Items)
            {
                if (items.Count > take || !seen.Add(row.ActualPath)) continue;
                string rowScope = scope.Length > 0
                    && (string.Equals(row.DevicePath, DevicePath(scope), StringComparison.OrdinalIgnoreCase))
                    ? "CurrentDevice"
                    : "OtherDevice";
                items.Add(ToOption(row, rowScope));
            }
        }

        return ToResponse(items, take);
    }

    private static ProjectParameterOptionsResponse ToResponse(List<ProjectParameterOption> items, int take)
    {
        bool truncated = items.Count > take;
        return new ProjectParameterOptionsResponse(
            truncated ? items.Take(take).ToArray() : items,
            truncated);
    }

    private static void AddRows(
        IEnumerable<ProjectNodeSearchRow> rows,
        string scope,
        ICollection<ProjectParameterOption> target,
        ISet<string> seen,
        int capacity)
    {
        foreach (ProjectNodeSearchRow row in rows)
        {
            if (target.Count >= capacity) return;
            if (seen.Add(row.ActualPath)) target.Add(ToOption(row, scope));
        }
    }

    private static ProjectParameterOption ToOption(ProjectNodeSearchRow row, string scope) => new(
        row.ActualPath,
        row.Name,
        row.ActualPath,
        scope,
        row.Kind == ProjectNodeKind.Tag ? TagKind : GroupKind,
        row.Description,
        row.Address);

    private static string DevicePath(string scope)
    {
        int first = scope.IndexOf('.');
        if (first < 0) return scope;
        int second = scope.IndexOf('.', first + 1);
        return second < 0 ? scope : scope[..second];
    }

    public static ProjectParameterOptionsResponse Search(
        IgwProject project,
        string? query,
        string? scopeGroup,
        string? kind,
        string? access,
        int? limit,
        CancellationToken cancellationToken,
        bool? scopeOnly = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedKind = NormalizeKind(kind);
        string normalizedAccess = NormalizeAccess(access);
        string needle = (query ?? "").Trim();
        string scope = (scopeGroup ?? "").Trim().Trim('.');
        int take = Math.Clamp(limit ?? 100, 1, 100);
        var collector = new Collector(needle, normalizedKind, normalizedAccess, take, cancellationToken);

        var location = LocateScope(project, scope, cancellationToken);
        if (location is not null)
        {
            collector.VisitScopedGroup(location.Value.Group, scope, "CurrentGroup");
            if (collector.IsFull) return collector.ToResponse();
        }

        // One-character filters stay within the current group. This prevents accidental
        // million-point scans while still allowing filtering of the default candidates.
        if ((scopeOnly ?? normalizedAccess == WriteAccess) || needle.Length < 2) return collector.ToResponse();

        if (location is not null)
        {
            collector.VisitDevice(
                location.Value.EffectiveDevice,
                location.Value.DevicePath,
                "CurrentDevice",
                scope);
            if (collector.IsFull) return collector.ToResponse();
        }

        foreach (var channel in project.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(channel.Name)) continue;
            foreach (var device in channel.Devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(device.Name)) continue;
                string devicePath = $"{channel.Name}.{device.Name}";
                if (location is not null &&
                    string.Equals(devicePath, location.Value.DevicePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                collector.VisitDevice(TemplateExpander.ExpandInProject(project, device), devicePath, "OtherDevice", null);
                if (collector.IsFull) return collector.ToResponse();
            }
        }

        return collector.ToResponse();
    }

    private static string NormalizeKind(string? value) => (value ?? TagKind).Trim().ToLowerInvariant() switch
    {
        TagKind => TagKind,
        GroupKind => GroupKind,
        _ => throw new ArgumentException("kind 仅支持 tag 或 group", nameof(value)),
    };

    private static string NormalizeAccess(string? value) => (value ?? ReadAccess).Trim().ToLowerInvariant() switch
    {
        ReadAccess => ReadAccess,
        WriteAccess => WriteAccess,
        _ => throw new ArgumentException("access 仅支持 read 或 write", nameof(value)),
    };

    private static ScopeLocation? LocateScope(IgwProject project, string scope, CancellationToken cancellationToken)
    {
        if (scope.Length == 0) return null;
        foreach (var channel in project.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(channel.Name)) continue;
            foreach (var device in channel.Devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(device.Name)) continue;
                string devicePath = $"{channel.Name}.{device.Name}";
                if (!scope.StartsWith(devicePath + ".", StringComparison.OrdinalIgnoreCase)) continue;

                var effective = TemplateExpander.ExpandInProject(project, device);
                var group = FindGroup(effective.Groups, devicePath, scope, cancellationToken);
                if (group is not null) return new ScopeLocation(devicePath, effective, group);
            }
        }
        return null;
    }

    private static TagGroupDef? FindGroup(
        IReadOnlyList<TagGroupDef> groups,
        string parentPath,
        string scope,
        CancellationToken cancellationToken)
    {
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(group.Name)) continue;
            string path = $"{parentPath}.{group.Name}";
            if (string.Equals(path, scope, StringComparison.OrdinalIgnoreCase)) return group;
            if (scope.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))
            {
                var found = FindGroup(group.SubGroups, path, scope, cancellationToken);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private readonly record struct ScopeLocation(
        string DevicePath,
        DeviceDef EffectiveDevice,
        TagGroupDef Group);

    private sealed class Collector
    {
        private readonly string _needle;
        private readonly string _kind;
        private readonly string _access;
        private readonly int _limit;
        private readonly CancellationToken _cancellationToken;
        private readonly List<ProjectParameterOption> _items;
        private int _visited;

        public Collector(
            string needle,
            string kind,
            string access,
            int limit,
            CancellationToken cancellationToken)
        {
            _needle = needle;
            _kind = kind;
            _access = access;
            _limit = limit;
            _cancellationToken = cancellationToken;
            _items = new List<ProjectParameterOption>(limit + 1);
        }

        public bool IsFull => _items.Count > _limit;

        public ProjectParameterOptionsResponse ToResponse()
        {
            IReadOnlyList<ProjectParameterOption> items = IsFull
                ? _items.GetRange(0, _limit)
                : _items;
            return new ProjectParameterOptionsResponse(items, IsFull);
        }

        public void VisitScopedGroup(TagGroupDef group, string fullPath, string scope)
        {
            VisitGroup(group, fullPath, scope, includeGroup: true, excludedSubtree: null);
        }

        public void VisitDevice(DeviceDef device, string devicePath, string scope, string? excludedSubtree)
        {
            if (_kind == TagKind)
            {
                foreach (var tag in device.Tags)
                {
                    CheckCancellation();
                    if (IsFull) return;
                    VisitTag(tag, $"{devicePath}.{tag.Name}", scope, excludedSubtree);
                }
            }

            foreach (var group in device.Groups)
            {
                CheckCancellation();
                if (IsFull) return;
                string groupPath = $"{devicePath}.{group.Name}";
                VisitGroup(group, groupPath, scope, includeGroup: true, excludedSubtree);
            }
        }

        private void VisitGroup(
            TagGroupDef group,
            string fullPath,
            string scope,
            bool includeGroup,
            string? excludedSubtree)
        {
            CheckCancellation();
            if (IsFull || IsWithin(fullPath, excludedSubtree)) return;

            if (_kind == GroupKind && includeGroup && Matches(group.Name, fullPath))
                Add(group.Name, fullPath, scope, GroupKind, group.Description, null);
            if (IsFull) return;

            if (_kind == TagKind)
            {
                foreach (var tag in group.Tags)
                {
                    CheckCancellation();
                    if (IsFull) return;
                    VisitTag(tag, $"{fullPath}.{tag.Name}", scope, excludedSubtree);
                }
            }

            foreach (var child in group.SubGroups)
            {
                CheckCancellation();
                if (IsFull) return;
                VisitGroup(child, $"{fullPath}.{child.Name}", scope, includeGroup: true, excludedSubtree);
            }
        }

        private void VisitTag(TagDef tag, string fullPath, string scope, string? excludedSubtree)
        {
            if (string.IsNullOrWhiteSpace(tag.Name) || IsWithin(fullPath, excludedSubtree)) return;
            if (!AccessMatches(tag.Access) || !Matches(tag.Name, fullPath)) return;
            if (_items.Any(item => string.Equals(item.Value, fullPath, StringComparison.OrdinalIgnoreCase))) return;
            _items.Add(new(fullPath, tag.Name, fullPath, scope, TagKind, tag.Description, tag.Address));
        }

        private void Add(
            string label,
            string fullPath,
            string scope,
            string kind,
            string? description,
            string? address)
        {
            _items.Add(new ProjectParameterOption(fullPath, label, fullPath, scope, kind, description, address));
        }

        private bool AccessMatches(TagAccess access) => _access == WriteAccess
            ? access is TagAccess.Write or TagAccess.ReadWrite
            : access is TagAccess.Read or TagAccess.ReadWrite;

        private bool Matches(string label, string fullPath) =>
            _needle.Length == 0 ||
            label.Contains(_needle, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(_needle, StringComparison.OrdinalIgnoreCase);

        private static bool IsWithin(string fullPath, string? subtree) =>
            !string.IsNullOrWhiteSpace(subtree) &&
            (string.Equals(fullPath, subtree, StringComparison.OrdinalIgnoreCase) ||
             fullPath.StartsWith(subtree + ".", StringComparison.OrdinalIgnoreCase));

        private void CheckCancellation()
        {
            if ((_visited++ & 0xff) == 0) _cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
