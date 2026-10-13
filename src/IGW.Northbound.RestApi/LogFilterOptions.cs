using IGW.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using IGW.Core;

namespace IGW.Northbound.RestApi;

public sealed record LogFilterProtocolOption(string Id, string Name);
public sealed record LogFilterChannelOption(string Name, string Protocol);
public sealed record LogFilterDeviceOption(string Channel, string Name, string Path);
/// <summary>开启了采集流程的标记组：选中后按它绑定的任务查(任务的接口调用、回写、判定和为它采集的记录)。</summary>
public sealed record LogFilterGroupOption(string Name, string Path, string Channel, string Device, string TaskId, string TaskName);

/// <summary>
/// 日志中心的查询条件选项：协议、通道、设备下拉，标记组输入提示。按当前工程给出，
/// 走 /api/v1/log-center 下(日志中心的查看权限即可)，只有日志查看权限、看不到工程配置的账号也能用。
/// </summary>
public sealed record LogFilterOptions(
    IReadOnlyList<LogFilterProtocolOption> Protocols,
    IReadOnlyList<LogFilterChannelOption> Channels,
    IReadOnlyList<LogFilterDeviceOption> Devices,
    IReadOnlyList<LogFilterGroupOption> Groups)
{
    public const string Path = "/api/v1/log-center/filter-options";

    public static void Map(IEndpointRouteBuilder app, IGatewayHost host) =>
        app.MapGet(Path, () => Results.Ok(Build(host.Project, host.Drivers)));

    /// <summary>协议用通道的驱动 Id(采集、状态日志里记的就是它)，显示驱动名；只列工程里用到的。</summary>
    public static LogFilterOptions Build(IgwProject project, IEnumerable<DriverDescriptor> drivers)
    {
        var names = drivers.GroupBy(driver => driver.DriverId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        var channels = project.Channels.Where(channel => !string.IsNullOrWhiteSpace(channel.Name))
            .Select(channel => new LogFilterChannelOption(channel.Name, channel.DriverId)).ToArray();
        var protocols = channels.Select(channel => channel.Protocol).Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new LogFilterProtocolOption(id, names.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name) ? name : id))
            .OrderBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var devices = project.Channels.Where(channel => !string.IsNullOrWhiteSpace(channel.Name))
            .SelectMany(channel => channel.Devices.Where(device => !string.IsNullOrWhiteSpace(device.Name))
                .Select(device => new LogFilterDeviceOption(channel.Name, device.Name, channel.Name + "." + device.Name)))
            .ToArray();
        var groups = project.MesUpload.Tasks
            .Where(task => task.Enabled && !string.IsNullOrWhiteSpace(task.GroupRef))
            .Select(task =>
            {
                string path = task.GroupRef!.Trim();
                string[] parts = path.Split('.');
                return new LogFilterGroupOption(parts[^1], path, parts[0],
                    parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0], task.Id, task.Name);
            })
            .ToArray();
        return new LogFilterOptions(protocols, channels, devices, groups);
    }
}
