using System.Globalization;

namespace IGW.Core;

/// <summary>
/// MES 结构体数组元素组名解析。
/// 兼容工程原生的 Name[index]，以及西门子 DB 导入器为了树节点可读性生成的 Name_index。
/// </summary>
public static class MesIndexedGroupName
{
    public static bool TryParse(string? name, out string baseName, out int index)
    {
        baseName = "";
        index = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        int open = name.LastIndexOf('[');
        if (open > 0 && name.Length > open + 2 && name[^1] == ']' &&
            int.TryParse(name.AsSpan(open + 1, name.Length - open - 2),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            baseName = name[..open];
            return baseName.Length > 0;
        }

        // Siemens DB 导入器把 Array[0..N] of Struct 展开为 CellData_0…CellData_N。
        int underscore = name.LastIndexOf('_');
        if (underscore > 0 && underscore < name.Length - 1 &&
            int.TryParse(name.AsSpan(underscore + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out index))
        {
            baseName = name[..underscore];
            return baseName.Length > 0;
        }

        return false;
    }
}
