#nullable enable
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PlcSimulator.Core;

namespace PlcSimulator.Import;

public sealed class SiemensDbImportResult
{
    public required SimulationProject Project { get; init; }
    public required string DataBlockName { get; init; }
    public required int DbNumber { get; init; }
    public required bool OptimizedAccess { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Imports a TIA Portal text DB export into the protocol-neutral simulation model.</summary>
public static class SiemensDbSimulationImporter
{
    private sealed record ParsedBlock(
        string SourceName,
        string BlockName,
        int DbNumber,
        bool Optimized,
        List<Member> Members);

    private sealed class Member
    {
        public required string Name { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Comment { get; init; } = string.Empty;
        public int Lower { get; init; }
        public int Upper { get; init; }
        public bool IsArray { get; init; }
        public List<Member> Children { get; } = new();
        public bool IsStructure => Children.Count > 0;
    }

    private sealed class Cursor
    {
        public int Byte;
        public int Bit;
        public void FlushBits() { if (Bit > 0) { Byte++; Bit = 0; } }
        public void Align(int boundary) { FlushBits(); if (boundary > 1) Byte = (Byte + boundary - 1) / boundary * boundary; }
    }

    private static readonly Regex NamePattern = new("DATA_BLOCK\\s+(?:\"(?<name>[^\"]+)\"|(?<name>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArrayStructPattern = new("^(?<name>[A-Za-z_][\\w]*)\\s*:\\s*Array\\s*\\[\\s*(?<lo>-?\\d+)\\s*\\.\\.\\s*(?<hi>-?\\d+)\\s*\\]\\s*of\\s*Struct\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StructPattern = new("^(?<name>[A-Za-z_][\\w]*)\\s*:\\s*Struct\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArrayScalarPattern = new("^(?<name>[A-Za-z_][\\w]*)\\s*:\\s*Array\\s*\\[\\s*(?<lo>-?\\d+)\\s*\\.\\.\\s*(?<hi>-?\\d+)\\s*\\]\\s*of\\s*(?<type>.+?)\\s*;$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScalarPattern = new("^(?<name>[A-Za-z_][\\w]*)\\s*:\\s*(?<type>.+?)\\s*;$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SiemensDbImportResult ImportFile(string path) => ImportFiles([path]);

    public static SiemensDbImportResult ImportFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = paths
            .Select(path => (SourceName: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToArray();
        return ImportSources(sources);
    }

    public static SiemensDbImportResult ImportText(string text) =>
        ImportSources([("Siemens DB", text)]);

    private static SiemensDbImportResult ImportSources(IReadOnlyList<(string SourceName, string Text)> sources)
    {
        if (sources.Count == 0) throw new InvalidDataException("没有选择 DB 文件");
        var types = new Dictionary<string, List<Member>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (Match match in Regex.Matches(source.Text, "TYPE\\s+\"(?<name>[^\"]+)\"(?<body>.*?)END_TYPE", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                types[match.Groups["name"].Value] = ParseMembers(match.Groups["body"].Value);
        var blocks = sources.Select(source => ParseBlock(source.SourceName, source.Text, types)).ToArray();
        var duplicateDb = blocks.GroupBy(x => x.DbNumber).FirstOrDefault(x => x.Count() > 1);
        if (duplicateDb is not null)
            throw new InvalidDataException($"DB{duplicateDb.Key} 重复，不能在同一模拟设备中导入多次");

        bool hasClassic = blocks.Any(x => !x.Optimized);
        bool hasPlus = blocks.Any(x => x.Optimized);
        var profile = hasClassic && hasPlus
            ? SiemensSimulationProfile.Hybrid
            : hasPlus ? SiemensSimulationProfile.Plus : SiemensSimulationProfile.Classic;
        string projectName = blocks.Length == 1 ? blocks[0].BlockName : "Siemens DB 项目";
        var project = new SimulationProject(projectName);
        var device = project.AddDevice("S7Sim");
        device.SourceChannelName = "Siemens DB";
        device.SiemensProfile = profile;
        device.ProtocolEvidence = profile switch
        {
            SiemensSimulationProfile.Hybrid => $"{blocks.Count(x => !x.Optimized)} 个非优化 DB，{blocks.Count(x => x.Optimized)} 个优化 DB",
            SiemensSimulationProfile.Plus => $"{blocks.Length} 个优化 DB；使用符号地址",
            _ => $"{blocks.Length} 个非优化 DB；使用绝对地址",
        };
        if (hasClassic) device.AddEndpoint(SimulationProtocol.SiemensS7, 102, 102);
        if (hasPlus) device.AddEndpoint(SimulationProtocol.SiemensS7Plus, 102, 102);

        var result = new SiemensDbImportResult
        {
            Project = project,
            DataBlockName = string.Join("、", blocks.Select(x => x.BlockName)),
            DbNumber = blocks.Length == 1 ? blocks[0].DbNumber : 0,
            OptimizedAccess = hasPlus && !hasClassic,
        };
        foreach (var block in blocks)
        {
            var root = device.AddGroup(block.BlockName);
            var cursor = new Cursor();
            foreach (var member in block.Members)
                AddMember(member, root, cursor, block.DbNumber, block.Optimized, block.BlockName, result.Warnings);
        }
        return result;
    }

    private static ParsedBlock ParseBlock(string sourceName, string text, IReadOnlyDictionary<string, List<Member>> types)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"DB 文件为空：{sourceName}");
        var nameMatch = NamePattern.Match(text);
        if (!nameMatch.Success) throw new InvalidDataException($"未找到 DATA_BLOCK 声明：{sourceName}");
        string blockName = nameMatch.Groups["name"].Value.Trim();
        int dbNumber = ParseDbNumber(blockName);
        bool optimized = !Regex.IsMatch(text, "S7_Optimized_Access\\s*:=\\s*'FALSE'", RegexOptions.IgnoreCase);
        List<Member> members = ExpandTypes(ParseMembers(text[nameMatch.Index..]), types, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (members.Count == 0) throw new InvalidDataException($"DB 中没有可导入的字段：{sourceName}");
        return new ParsedBlock(sourceName, blockName, dbNumber, optimized, members);
    }

    private static List<Member> ParseMembers(string text)
    {
        var roots = new List<Member>();
        var containers = new Stack<List<Member>>();
        containers.Push(roots);
        bool inside = false;
        foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            string code = raw;
            string comment = string.Empty;
            int commentAt = code.IndexOf("//", StringComparison.Ordinal);
            if (commentAt >= 0) { comment = code[(commentAt + 2)..].Trim(); code = code[..commentAt]; }
            code = code.Trim();
            code = Regex.Replace(code, "\\{[^}]*\\}", string.Empty).Trim();
            if (!inside)
            {
                if (Regex.IsMatch(code, "^(?:STRUCT\\s*$|VAR\\b)", RegexOptions.IgnoreCase)) inside = true;
                continue;
            }
            if (Regex.IsMatch(code, "^END_VAR\\b", RegexOptions.IgnoreCase))
            {
                break;
            }
            if (Regex.IsMatch(code, "^END_STRUCT\\s*;?", RegexOptions.IgnoreCase))
            {
                if (containers.Count == 1) break;
                containers.Pop();
                continue;
            }
            if (code.Length == 0) continue;
            Match match = ArrayStructPattern.Match(code.TrimEnd(';'));
            if (match.Success)
            {
                var item = new Member { Name = match.Groups["name"].Value, IsArray = true, Lower = Int(match, "lo"), Upper = Int(match, "hi"), Comment = comment };
                containers.Peek().Add(item); containers.Push(item.Children); continue;
            }
            match = StructPattern.Match(code.TrimEnd(';'));
            if (match.Success)
            {
                var item = new Member { Name = match.Groups["name"].Value, Comment = comment };
                containers.Peek().Add(item); containers.Push(item.Children); continue;
            }
            match = ArrayScalarPattern.Match(code);
            if (match.Success)
            {
                containers.Peek().Add(new Member { Name = match.Groups["name"].Value, Type = match.Groups["type"].Value.Trim(), IsArray = true, Lower = Int(match, "lo"), Upper = Int(match, "hi"), Comment = comment });
                continue;
            }
            match = ScalarPattern.Match(code);
            if (match.Success) containers.Peek().Add(new Member { Name = match.Groups["name"].Value, Type = match.Groups["type"].Value.Trim(), Comment = comment });
        }
        return roots;
    }

    private static List<Member> ExpandTypes(IEnumerable<Member> members, IReadOnlyDictionary<string, List<Member>> types, HashSet<string> resolving)
    {
        var result = new List<Member>();
        foreach (var member in members)
        {
            string typeName = member.Type.Trim().Trim('"');
            IEnumerable<Member> children = member.Children;
            bool namedType = member.Type.TrimStart().StartsWith('"');
            if (namedType)
            {
                if (!types.TryGetValue(typeName, out var definition)) throw new InvalidDataException($"未找到 UDT 定义：{typeName}");
                if (!resolving.Add(typeName)) throw new InvalidDataException($"UDT 存在循环引用：{typeName}");
                children = definition;
            }
            var expanded = new Member { Name = member.Name, Type = namedType ? string.Empty : member.Type, Comment = member.Comment,
                Lower = member.Lower, Upper = member.Upper, IsArray = member.IsArray };
            expanded.Children.AddRange(ExpandTypes(children, types, resolving));
            if (namedType) resolving.Remove(typeName);
            result.Add(expanded);
        }
        return result;
    }

    private static void AddMember(
        Member member,
        SimulationGroup parent,
        Cursor cursor,
        int db,
        bool optimized,
        string symbolParent,
        List<string> warnings)
    {
        if (member.IsStructure)
        {
            int count = member.IsArray ? checked(member.Upper - member.Lower + 1) : 1;
            for (int i = 0; i < count; i++)
            {
                int index = member.Lower + i;
                string name = member.IsArray ? $"{member.Name}_{index}" : member.Name;
                string symbolName = member.IsArray ? $"{member.Name}[{index}]" : member.Name;
                if (!optimized) cursor.Align(2);
                var group = parent.AddGroup(name);
                foreach (var child in member.Children)
                    AddMember(child, group, cursor, db, optimized, $"{symbolParent}.{symbolName}", warnings);
                if (!optimized) cursor.Align(2);
            }
            return;
        }

        int scalarCount = member.IsArray ? checked(member.Upper - member.Lower + 1) : 1;
        for (int i = 0; i < scalarCount; i++)
        {
            string tagName = member.IsArray ? $"{member.Name}[{member.Lower + i}]" : member.Name;
            var info = TypeInfo(member.Type);
            string address;
            if (info.Type == SimulationDataType.Boolean)
            {
                address = $"DB{db}.DBX{cursor.Byte}.{cursor.Bit}";
                cursor.Bit++; if (cursor.Bit == 8) { cursor.Byte++; cursor.Bit = 0; }
            }
            else
            {
                cursor.Align(info.Alignment);
                address = Address(db, cursor.Byte, info.Type, info.Size);
                cursor.Byte += info.Size;
            }
            var tag = parent.AddTag(tagName, info.Type, InitialValue(info.Type));
            if (info.Type == SimulationDataType.String)
            {
                tag.IsWideString = member.Type.TrimStart().StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase);
                tag.StringLength = tag.IsWideString ? (info.Size - 4) / 2 : info.Size - 2;
                if (tag.IsWideString) address = $"DB{db}.DBB{cursor.Byte - info.Size} (WString[{tag.StringLength}])";
            }
            tag.Description = member.Comment;
            tag.ClassicAddress = address;
            tag.SymbolicAddress = $"{symbolParent}.{tagName}";
            tag.ProtocolAddress = optimized ? tag.SymbolicAddress : tag.ClassicAddress;
            if (info.Type == SimulationDataType.Unknown) warnings.Add($"字段 {tag.SymbolPath} 的类型 {member.Type} 已保留，但暂不能协议读写");
        }
    }

    private static (SimulationDataType Type, int Size, int Alignment) TypeInfo(string raw)
    {
        string type = Regex.Replace(raw.Trim(), "\\s+", string.Empty).ToUpperInvariant();
        Match stringType = Regex.Match(type, "^STRING\\[(?<n>\\d+)\\]$");
        if (stringType.Success) return (SimulationDataType.String, int.Parse(stringType.Groups["n"].Value, CultureInfo.InvariantCulture) + 2, 2);
        Match wstringType = Regex.Match(type, "^WSTRING\\[(?<n>\\d+)\\]$");
        if (wstringType.Success) return (SimulationDataType.String, int.Parse(wstringType.Groups["n"].Value, CultureInfo.InvariantCulture) * 2 + 4, 2);
        return type switch
        {
            "BOOL" => (SimulationDataType.Boolean, 1, 1),
            "BYTE" or "USINT" or "CHAR" => (SimulationDataType.Byte, 1, 1),
            "SINT" => (SimulationDataType.SByte, 1, 1),
            "INT" => (SimulationDataType.Int16, 2, 2),
            "UINT" or "WORD" or "WCHAR" => (SimulationDataType.UInt16, 2, 2),
            "DINT" => (SimulationDataType.Int32, 4, 2),
            "UDINT" or "DWORD" => (SimulationDataType.UInt32, 4, 2),
            "REAL" => (SimulationDataType.Float32, 4, 2),
            "LREAL" => (SimulationDataType.Float64, 8, 2),
            "LINT" => (SimulationDataType.Int64, 8, 2),
            "ULINT" or "LWORD" => (SimulationDataType.UInt64, 8, 2),
            "TIME" or "TOD" or "DATE" => (SimulationDataType.UInt32, 4, 2),
            "LTIME" or "LDATE_AND_TIME" => (SimulationDataType.UInt64, 8, 2),
            _ => (SimulationDataType.Unknown, 1, 1),
        };
    }

    private static string Address(int db, int offset, SimulationDataType type, int size) => type switch
    {
        SimulationDataType.Byte or SimulationDataType.SByte => $"DB{db}.DBB{offset}",
        SimulationDataType.Int16 or SimulationDataType.UInt16 => $"DB{db}.DBW{offset}",
        SimulationDataType.Int32 or SimulationDataType.UInt32 or SimulationDataType.Float32 => $"DB{db}.DBD{offset}",
        SimulationDataType.Int64 or SimulationDataType.UInt64 or SimulationDataType.Float64 => $"DB{db}.DBB{offset}",
        SimulationDataType.String => $"DB{db}.DBB{offset} (String[{Math.Max(0, size - 2)}])",
        _ => $"DB{db}.DBB{offset}",
    };

    private static object? InitialValue(SimulationDataType type) => type switch
    {
        SimulationDataType.Boolean => false, SimulationDataType.Byte => (byte)0, SimulationDataType.SByte => (sbyte)0,
        SimulationDataType.Int16 => (short)0, SimulationDataType.UInt16 => (ushort)0, SimulationDataType.Int32 => 0,
        SimulationDataType.UInt32 => 0u, SimulationDataType.Int64 => 0L, SimulationDataType.UInt64 => 0UL,
        SimulationDataType.Float32 => 0f, SimulationDataType.Float64 => 0d, SimulationDataType.String => string.Empty, _ => null,
    };

    private static int ParseDbNumber(string blockName)
    {
        Match match = Regex.Match(blockName, "DB(?<n>\\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException($"无法从 {blockName} 识别 DB 编号");
        return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    private static int Int(Match match, string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
}
