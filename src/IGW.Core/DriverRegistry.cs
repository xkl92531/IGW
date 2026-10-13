using IGW.Abstractions;

namespace IGW.Core;

/// <summary>驱动注册表:driverId → 工厂。MVP 显式注册,后续可反射扫描 Drivers.* 程序集。</summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IDriverFactory factory)
    {
        if (!_factories.TryAdd(factory.DriverId, factory))   // 重复 Id 静默覆盖会让"到底用哪个驱动"不可见 → 直接失败,逼调用方改用不同 Id
            throw new InvalidOperationException($"驱动 Id 重复注册:'{factory.DriverId}'(已有 {_factories[factory.DriverId].GetType().Name},又注册 {factory.GetType().Name})。请为其中一个改用不同 DriverId。");
    }

    /// <summary>授权过滤：返回 false 的驱动不能新建通道、运行时不采集。网关按授权码设置，默认全部可用。</summary>
    public Func<string, bool> Allows { get; set; } = static _ => true;

    public bool TryGet(string driverId, out IDriverFactory factory) =>
        _factories.TryGetValue(driverId, out factory!);

    public IReadOnlyList<DriverDescriptor> Descriptors =>
        _factories.Values.Select(f => WithLicense(f.Describe())).ToList();

    private DriverDescriptor WithLicense(DriverDescriptor descriptor) => Allows(descriptor.DriverId)
        ? descriptor
        : new DriverDescriptor
        {
            DriverId = descriptor.DriverId,
            DisplayName = descriptor.DisplayName,
            AddressHelp = descriptor.AddressHelp,
            ChannelSettings = descriptor.ChannelSettings,
            DeviceSettings = descriptor.DeviceSettings,
            Licensed = false,
        };

    public IReadOnlyCollection<string> DriverIds => _factories.Keys.ToArray();
}
