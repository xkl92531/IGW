using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 原生组件(igw_s7plus.dll / libigw_s7plus.so)是单独编译分发的，容器里很容易只换了网关没换它。
/// 网关兼容 ABI 1～3，但低版本没有逐项坏点隔离：一个解析不了的符号会把同一批点全部拖成坏值。
/// 因此实际加载的版本必须在设备诊断里直接可见。
/// </summary>
public class SiemensPlusAbiVisibilityTests
{
    [Fact]
    public void Gateway_accepts_older_native_abis_so_the_loaded_version_must_be_reported()
    {
        Assert.True(SiemensPlusNative.MinimumAbiVersion < SiemensPlusNative.AbiVersion,
            "既然允许比随附版本旧的原生库，就必须把实际版本报出来");
        Assert.True(SiemensPlusNative.IsSupportedAbi(SiemensPlusNative.MinimumAbiVersion));
        Assert.True(SiemensPlusNative.IsSupportedAbi(SiemensPlusNative.AbiVersion));
        Assert.False(SiemensPlusNative.IsSupportedAbi(SiemensPlusNative.AbiVersion + 1));
    }
}
