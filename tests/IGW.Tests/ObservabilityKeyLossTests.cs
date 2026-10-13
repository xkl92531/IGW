using System.Text.Json.Nodes;
using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// Docker 部署每次升级都会重建容器：容器里 /usr/share/IGW/secrets 下的观测主密钥随之丢失，
/// observability.json 里加密的旧 OpenObserve 凭据解不开，网关原先直接启动失败(6.75 上踩过)。
/// 这个凭据早已没人用(日志改为网关自带存储)，解不开时换一个新的、写回文件，照常启动，保留天数和存储位置不丢。
/// </summary>
public sealed class ObservabilityKeyLossTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "igw-observability-key", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { }
    }

    [Fact]
    public void Settings_still_load_when_the_saved_credential_cannot_be_decrypted()
    {
        ObservabilitySettings created = ObservabilitySettings.Initialize(_data, Path.Combine(_data, "logs"), null, _ => true);
        string file = Path.Combine(_data, ObservabilitySettings.FileName);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["retentionDays"] = 14;
        // 换了主密钥的密文：随机字节，任何密钥都解不开
        json["protectedPassword"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(60));
        File.WriteAllText(file, json.ToJsonString());

        ObservabilitySettings loaded = ObservabilitySettings.Load(_data)!;
        Assert.Equal(14, loaded.RetentionDays);
        Assert.Equal(created.StorageDirectory, loaded.StorageDirectory);
        Assert.Equal(created.InstanceId, loaded.InstanceId);
        Assert.False(string.IsNullOrEmpty(loaded.Password));

        // 新凭据已写回：下次启动直接解得开，不再每次重新生成
        ObservabilitySettings again = ObservabilitySettings.Load(_data)!;
        Assert.Equal(loaded.Password, again.Password);
    }
}
