using System.Collections.Concurrent;

namespace IGW.Core;

public interface IScanDemandLease : IDisposable { }

public enum ScanDemandPriority
{
    Process = 0,
    Interactive = 1,
    Background = 2,
}

public readonly record struct EffectiveScanPeriod(
    string TagId,
    int Handle,
    int PeriodMs,
    ScanDemandPriority Priority = ScanDemandPriority.Process);

public sealed record ScanDemandOwner(string Kind, string Id, string Name, string Scope);
public sealed record ScanDemandOwnerRegistration(ScanDemandOwner Owner, IReadOnlyList<string> TagIds);

public sealed record DeviceScanDemandSnapshot(
    long Version,
    IReadOnlyList<EffectiveScanPeriod> EffectivePeriods,
    IReadOnlyList<ScanDemandOwnerRegistration>? OwnerScopes = null)
{
    public static readonly DeviceScanDemandSnapshot Empty = new(
        0, Array.Empty<EffectiveScanPeriod>(), Array.Empty<ScanDemandOwnerRegistration>());
    public IReadOnlyList<ScanDemandOwnerRegistration> OwnerRegistrations =>
        OwnerScopes ?? Array.Empty<ScanDemandOwnerRegistration>();

    public IReadOnlyList<ScanDemandOwner> OwnersFor(IEnumerable<string> tagIds)
    {
        var requested = tagIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return [];
        var owners = new Dictionary<string, ScanDemandOwner>(StringComparer.OrdinalIgnoreCase);
        foreach (ScanDemandOwnerRegistration registration in OwnerRegistrations)
        {
            if (!registration.TagIds.Any(requested.Contains)) continue;
            ScanDemandOwner owner = registration.Owner;
            owners.TryAdd(owner.Kind + ":" + owner.Id, owner);
            if (owners.Count >= 32) break;
        }
        return owners.Values.OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class ScanDemandRegistry
{
    private readonly record struct DemandPoint(string TagId, int Handle);
    private sealed record Registration(
        long Id,
        string Owner,
        ScanDemandOwner OwnerInfo,
        int PeriodMs,
        ScanDemandPriority Priority,
        DemandPoint[] Points);

    private sealed class DeviceState
    {
        public readonly object Gate = new();
        public readonly Dictionary<long, Registration> Registrations = new();
        public long Version;
        public DeviceScanDemandSnapshot Snapshot = DeviceScanDemandSnapshot.Empty;
    }

    private readonly DataHub _hub;
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;

    public ScanDemandRegistry(DataHub hub) => _hub = hub;

    public IScanDemandLease Acquire(
        string owner,
        IEnumerable<string> tagIds,
        int periodMs,
        ScanDemandPriority priority = ScanDemandPriority.Process,
        string? ownerName = null,
        string? scope = null)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));

        long id = Interlocked.Increment(ref _nextId);
        var byDevice = new Dictionary<string, List<DemandPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (string tagId in tagIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_hub.TryGetHandleId(tagId, out int handle)) continue;
            string device = DeviceNameOf(tagId);
            if (!byDevice.TryGetValue(device, out var points)) byDevice[device] = points = new();
            points.Add(new DemandPoint(tagId, handle));
        }

        foreach (var pair in byDevice)
        {
            var state = _devices.GetOrAdd(pair.Key, _ => new DeviceState());
            lock (state.Gate)
            {
                state.Registrations[id] = new Registration(
                    id, owner, DescribeOwner(owner, ownerName, scope), periodMs, priority, pair.Value.ToArray());
                Rebuild(state);
            }
        }

        return new Lease(this, id, byDevice.Keys.ToArray());
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DeviceScanDemandSnapshot, HashSet<string>> _demandedTags = new();

    /// <summary>该点是否在某个常驻采集需求(如 MES 判定点)里。</summary>
    public bool IsDemanded(string tagId)
    {
        var snapshot = Snapshot(DeviceNameOf(tagId));
        if (snapshot.EffectivePeriods.Count == 0) return false;
        var tags = _demandedTags.GetValue(snapshot, static item =>
            item.EffectivePeriods.Select(period => period.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return tags.Contains(tagId);
    }

    public bool HasDemand(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state) && state.Snapshot.EffectivePeriods.Count > 0;

    public long Version(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state) ? Volatile.Read(ref state.Version) : 0;

    public DeviceScanDemandSnapshot Snapshot(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state)
            ? Volatile.Read(ref state.Snapshot)
            : DeviceScanDemandSnapshot.Empty;

    public void RefreshDevice(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var state)) return;
        lock (state.Gate)
        {
            foreach (var pair in state.Registrations.ToArray())
            {
                var refreshed = pair.Value.Points
                    .Select(point => _hub.TryGetHandleId(point.TagId, out int handle)
                        ? new DemandPoint(point.TagId, handle)
                        : new DemandPoint(point.TagId, -1))
                    .Where(point => point.Handle >= 0)
                    .ToArray();
                state.Registrations[pair.Key] = pair.Value with { Points = refreshed };
            }
            Rebuild(state);
        }
    }

    private void Release(long id, string[] devices)
    {
        foreach (string device in devices)
        {
            if (!_devices.TryGetValue(device, out var state)) continue;
            lock (state.Gate)
                if (state.Registrations.Remove(id)) Rebuild(state);
        }
    }

    private static void Rebuild(DeviceState state)
    {
        var periods = new Dictionary<string, (int Handle, int PeriodMs, ScanDemandPriority Priority)>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in state.Registrations.Values)
        foreach (var point in registration.Points)
        {
            if (!periods.TryGetValue(point.TagId, out var current))
                periods[point.TagId] = (point.Handle, registration.PeriodMs, registration.Priority);
            else
                periods[point.TagId] = (
                    point.Handle,
                    Math.Min(current.PeriodMs, registration.PeriodMs),
                    (ScanDemandPriority)Math.Min((int)current.Priority, (int)registration.Priority));
        }

        long version = ++state.Version;
        state.Snapshot = new DeviceScanDemandSnapshot(version,
            periods.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new EffectiveScanPeriod(
                    x.Key, x.Value.Handle, x.Value.PeriodMs, x.Value.Priority))
                .ToArray(),
            state.Registrations.Values
                .OrderBy(registration => registration.OwnerInfo.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(registration => registration.OwnerInfo.Id, StringComparer.OrdinalIgnoreCase)
                .Select(registration => new ScanDemandOwnerRegistration(
                    registration.OwnerInfo,
                    registration.Points.Select(point => point.TagId).ToArray()))
                .ToArray());
    }

    private static ScanDemandOwner DescribeOwner(string owner, string? ownerName, string? scope)
    {
        string value = Bounded(owner, 256, "unknown");
        int separator = value.IndexOf(':');
        string kind = separator > 0 ? value[..separator] : value;
        string id = separator > 0 && separator + 1 < value.Length ? value[(separator + 1)..] : value;
        return new ScanDemandOwner(
            kind.ToLowerInvariant(), id,
            Bounded(ownerName, 256, id),
            Bounded(scope, 512, ""));
    }

    private static string Bounded(string? value, int limit, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit];
    }

    private static string DeviceNameOf(string tagId)
    {
        int first = tagId.IndexOf('.');
        int second = first < 0 ? -1 : tagId.IndexOf('.', first + 1);
        return second < 0 ? tagId : tagId[..second];
    }

    private sealed class Lease(ScanDemandRegistry owner, long id, string[] devices) : IScanDemandLease
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(id, devices);
        }
    }
}
