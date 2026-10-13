using IGW.Abstractions;
using Opc.Ua;

namespace IGW.Drivers.OpcUaClient;

/// <summary>OPC UA Client 地址 = 远程 NodeId(如 ns=2;s=Tag1 或 ns=2;i=1234)。
/// 按 NodeId 直接读,不做地址块合并,返回占位 TagAddress。</summary>
public sealed class OpcUaNodeAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = default;
        error = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "空地址(应为 OPC UA NodeId,如 ns=2;s=Tag1)";
            return false;
        }
        try { _ = NodeId.Parse(address.Trim()); }
        catch (Exception ex) { error = "NodeId 解析失败:" + ex.Message; return false; }
        result = new TagAddress(0, 0, 1); // OPC UA 按 NodeId 读,地址占位
        return true;
    }
}
