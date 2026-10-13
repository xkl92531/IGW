using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IGW.Licensing;

/// <summary>
/// 授权码编解码 + ECDSA(P-256/SHA256)签名验签。
/// 授权码格式:IGW1.&lt;base64url(payloadJson)&gt;.&lt;base64url(sig)&gt;
/// 公钥内嵌于此(验签用);私钥只在发码工具 IgwKeygen 手里(签发用)——别人拿不到私钥就造不出有效码。
/// </summary>
public static class LicenseCode
{
    // 公钥(ECDSA P-256 SubjectPublicKeyInfo,base64)。由 IgwKeygen genkey 生成并回填。私钥只在 IgwKeygen 手里。
    public const string PublicKeyB64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWTr6giEhV3SINwuCzKAB9Bho95EnjSylF7RlZhwruOZhg+PDbZQ/tP/heEBClt4t2bta5r4vclOm7KOAVcohRQ==";

    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>把授权内容签成授权码(仅发码工具用,需传私钥)。</summary>
    public static string Sign(LicensePayload payload, ECDsa privateKey)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, J);
        byte[] sig = privateKey.SignData(body, HashAlgorithmName.SHA256);
        return "IGW1." + B64Url.Encode(body) + "." + B64Url.Encode(sig);
    }

    /// <summary>验证授权码并取出内容。签名无效/格式错 → 返回 false。</summary>
    public static bool Verify(string code, out LicensePayload? payload)
    {
        payload = null;
        try
        {
            var parts = (code ?? "").Trim().Split('.');
            if (parts.Length != 3 || parts[0] != "IGW1") return false;
            byte[] body = B64Url.Decode(parts[1]);
            byte[] sig = B64Url.Decode(parts[2]);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyB64), out _);
            if (!ecdsa.VerifyData(body, sig, HashAlgorithmName.SHA256)) return false;
            payload = JsonSerializer.Deserialize<LicensePayload>(body, J);
            return payload != null;
        }
        catch { return false; }
    }

    public static bool HasEmbeddedKey => !PublicKeyB64.Contains("REPLACE_WITH_KEYGEN");
}

/// <summary>base64url(无填充)编解码。</summary>
internal static class B64Url
{
    public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
