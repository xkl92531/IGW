using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Core;

public sealed record SiemensDbImportIssue(string Path, string Type, string Reason);

public sealed record SiemensDbParseResult(
    int DbNumber,
    string DbName,
    bool Optimized,
    TagGroupDef Root,
    IReadOnlyList<SiemensDbImportIssue> Skipped,
    int SizeBytes,
    bool DbNumberKnown = true)   // false：文件里没写 DB 号(TIA 按符号名导出)，用的是调用方给的默认值
{
    public int TagCount => Count(Root);

    private static int Count(TagGroupDef group) =>
        group.Tags.Count + group.SubGroups.Sum(Count);
}

/// <summary>
/// Siemens DB/SCL 类型解析器。数组始终生成一个逻辑 TagDef，并保存维度、PLC 下界、步长及递归结构体布局。
/// </summary>
public static class SiemensDbTypeParser
{
    private enum TokenKind { Word, Number, Quoted, Symbol, Comment }
    private sealed record Token(TokenKind Kind, string Text, int Offset);
    private sealed record Dimension(int Lower, int Upper);
    private abstract record TypeNode;
    private sealed record ScalarNode(string Name, int Bytes, IgwDataType DataType, bool IsBool) : TypeNode;
    private sealed record StringNode(bool Wide, int Length) : TypeNode;
    private sealed record ArrayNode(IReadOnlyList<Dimension> Dimensions, TypeNode Element) : TypeNode;
    private sealed record StructNode(IReadOnlyList<FieldNode> Fields) : TypeNode;
    private sealed record NamedNode(string Name) : TypeNode;
    private sealed record FieldNode(string Name, TypeNode Type, string Description);

    private static readonly IReadOnlyDictionary<string, ScalarNode> Scalars =
        new Dictionary<string, ScalarNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOOL"] = new("BOOL", 0, IgwDataType.Bool, true),
            ["BYTE"] = new("BYTE", 1, IgwDataType.Byte, false),
            ["USINT"] = new("USINT", 1, IgwDataType.Byte, false),
            ["SINT"] = new("SINT", 1, IgwDataType.Char, false),
            ["CHAR"] = new("CHAR", 1, IgwDataType.Char, false),
            ["WCHAR"] = new("WCHAR", 2, IgwDataType.UInt16, false),
            ["WORD"] = new("WORD", 2, IgwDataType.UInt16, false),
            ["UINT"] = new("UINT", 2, IgwDataType.UInt16, false),
            ["INT"] = new("INT", 2, IgwDataType.Int16, false),
            ["DATE"] = new("DATE", 2, IgwDataType.UInt16, false),
            ["S5TIME"] = new("S5TIME", 2, IgwDataType.UInt16, false),
            ["DWORD"] = new("DWORD", 4, IgwDataType.UInt32, false),
            ["UDINT"] = new("UDINT", 4, IgwDataType.UInt32, false),
            ["DINT"] = new("DINT", 4, IgwDataType.Int32, false),
            ["REAL"] = new("REAL", 4, IgwDataType.Float, false),
            ["TIME"] = new("TIME", 4, IgwDataType.UInt32, false),
            ["TOD"] = new("TOD", 4, IgwDataType.UInt32, false),
            ["TIME_OF_DAY"] = new("TIME_OF_DAY", 4, IgwDataType.UInt32, false),
            ["LDATE"] = new("LDATE", 4, IgwDataType.UInt32, false),
            ["LREAL"] = new("LREAL", 8, IgwDataType.Double, false),
            ["LINT"] = new("LINT", 8, IgwDataType.Int64, false),
            ["LWORD"] = new("LWORD", 8, IgwDataType.UInt64, false),
            ["ULINT"] = new("ULINT", 8, IgwDataType.UInt64, false),
            ["LTIME"] = new("LTIME", 8, IgwDataType.Int64, false),
            ["LTOD"] = new("LTOD", 8, IgwDataType.UInt64, false),
            ["LTIME_OF_DAY"] = new("LTIME_OF_DAY", 8, IgwDataType.UInt64, false),
            ["DATE_AND_TIME"] = new("DATE_AND_TIME", 8, IgwDataType.ByteArray, false),
            ["DT"] = new("DT", 8, IgwDataType.ByteArray, false),
            ["LDT"] = new("LDT", 8, IgwDataType.ByteArray, false),
            ["DTL"] = new("DTL", 12, IgwDataType.ByteArray, false),
        };

    private static readonly IReadOnlyDictionary<string, string> Unsupported =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANY"] = "ANY 是运行时指针类型，不能作为普通采集叶子",
            ["POINTER"] = "POINTER 是运行时指针类型，不能作为普通采集叶子",
            ["REF_TO"] = "REF_TO 是运行时引用类型，不能作为普通采集叶子",
            ["VARIANT"] = "VARIANT 是运行时引用类型，不能作为普通采集叶子",
        };

    public static SiemensDbParseResult Parse(string source, int defaultDb = 1,
        IEnumerable<string>? additionalTypeSources = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var allSource = additionalTypeSources is null
            ? source
            : string.Join(Environment.NewLine, additionalTypeSources.Append(source));
        var allTokens = Tokenize(allSource);
        var sourceTokens = Tokenize(source);
        var namedTypes = ParseNamedTypes(allTokens);
        var (dbNumber, dbName, dbNumberKnown) = FindDb(source, defaultDb);
        var optimized = Regex.IsMatch(source,
            "S7_Optimized_Access\\s*:=\\s*['\\\"]?TRUE['\\\"]?", RegexOptions.IgnoreCase);
        var root = new TagGroupDef { Name = "root" };
        var issues = new List<SiemensDbImportIssue>();

        var dbIndex = FindWord(sourceTokens, "DATA_BLOCK");
        if (dbIndex < 0)
            return new SiemensDbParseResult(dbNumber, dbName, optimized, root, issues, 0, dbNumberKnown);
        var beginIndex = FindWord(sourceTokens, "BEGIN", dbIndex + 1);
        var varIndex = FindWord(sourceTokens, "VAR", dbIndex + 1);
        var structIndex = FindWord(sourceTokens, "STRUCT", dbIndex + 1);
        var hasVar = varIndex >= 0 && (beginIndex < 0 || varIndex < beginIndex);
        var hasStruct = structIndex >= 0 && (beginIndex < 0 || structIndex < beginIndex);
        if (!hasVar && !hasStruct)
            return new SiemensDbParseResult(dbNumber, dbName, optimized, root, issues, 0, dbNumberKnown);

        var parser = new DeclarationParser(sourceTokens, hasVar && (!hasStruct || varIndex < structIndex)
            ? varIndex
            : structIndex);
        var ast = hasVar && (!hasStruct || varIndex < structIndex)
            ? parser.ParseVarBlock()
            : parser.ParseStruct();
        var compiler = new LayoutCompiler(dbNumber, dbName, optimized, namedTypes, issues);
        compiler.EmitRoot(ast, root);
        return new SiemensDbParseResult(dbNumber, dbName, optimized, root, issues, compiler.SizeBytes, dbNumberKnown);
    }

    private sealed class DeclarationParser(IReadOnlyList<Token> tokens, int index)
    {
        public int Index { get; private set; } = index;

        public StructNode ParseStruct()
        {
            if (!Take("STRUCT")) throw new InvalidDataException("缺少 STRUCT 声明");
            return ParseFieldsUntil("END_STRUCT");
        }

        public StructNode ParseVarBlock()
        {
            if (!Take("VAR")) throw new InvalidDataException("缺少 VAR 声明");
            return ParseFieldsUntil("END_VAR");
        }

        private StructNode ParseFieldsUntil(string endToken)
        {
            var fields = new List<FieldNode>();
            var pendingComments = new List<string>();
            while (Index < tokens.Count && !Peek(endToken))
            {
                if (Current?.Kind == TokenKind.Comment)
                {
                    pendingComments.Add(Current.Text);
                    Index++;
                    continue;
                }
                if (Peek("{")) { SkipBraceBlock(); continue; }

                var start = Index;
                var name = Current;
                if (name is null || name.Kind is not (TokenKind.Word or TokenKind.Quoted))
                {
                    Index++;
                    continue;
                }
                Index++;
                // TIA 导出的字段属性写在名字后面：Name { S7_SetPoint := 'False'} : Type;
                while (Peek("{")) SkipBraceBlock();
                if (!Take(":")) { Index = start + 1; continue; }
                var type = ParseType();
                if (type is null) { Index = start + 1; continue; }
                while (Peek("{")) SkipBraceBlock();
                if (Take(":=")) SkipInitialization();
                Take(";");
                while (Current?.Kind == TokenKind.Comment)
                {
                    pendingComments.Add(Current.Text);
                    Index++;
                }
                fields.Add(new FieldNode(name.Text, type,
                    string.Join(" ", pendingComments.Where(static text => !string.IsNullOrWhiteSpace(text)))));
                pendingComments.Clear();
            }
            Take(endToken);
            Take(";");
            return new StructNode(fields);
        }

        private TypeNode? ParseType()
        {
            if (Peek("ARRAY")) return ParseArray();
            if (Peek("STRUCT")) return ParseStruct();
            var token = Current;
            if (token is null || token.Kind is not (TokenKind.Word or TokenKind.Quoted)) return null;
            Index++;
            if (token.Text.Equals("STRING", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("WSTRING", StringComparison.OrdinalIgnoreCase))
            {
                var length = 254;
                if (Take("["))
                {
                    if (Current?.Kind == TokenKind.Number && int.TryParse(Current.Text,
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        length = Math.Max(1, parsed);
                        Index++;
                    }
                    Take("]");
                }
                return new StringNode(token.Text.StartsWith("W", StringComparison.OrdinalIgnoreCase), length);
            }
            return Scalars.TryGetValue(token.Text, out var scalar) ? scalar : new NamedNode(token.Text);
        }

        private ArrayNode ParseArray()
        {
            Take("ARRAY");
            if (!Take("[")) throw new InvalidDataException("ARRAY 缺少 [");
            var dimensions = new List<Dimension>();
            while (Index < tokens.Count && !Peek("]"))
            {
                if (!TryTakeInt(out var lower) || !Take("..") || !TryTakeInt(out var upper) || upper < lower)
                    throw new InvalidDataException("ARRAY 维度声明无效");
                dimensions.Add(new Dimension(lower, upper));
                if (!Take(",")) break;
            }
            if (!Take("]") || !Take("OF")) throw new InvalidDataException("ARRAY 缺少 ] OF");
            var element = ParseType() ?? throw new InvalidDataException("ARRAY 元素类型无效");
            return new ArrayNode(dimensions, element);
        }

        private Token? Current => Index < tokens.Count ? tokens[Index] : null;
        private bool Peek(string text) => Current?.Text.Equals(text, StringComparison.OrdinalIgnoreCase) == true;
        private bool Take(string text) { if (!Peek(text)) return false; Index++; return true; }
        private bool TryTakeInt(out int value)
        {
            if (Current?.Kind == TokenKind.Number && int.TryParse(Current.Text,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            { Index++; return true; }
            value = 0;
            return false;
        }
        private void SkipInitialization()
        {
            var nesting = 0;
            while (Index < tokens.Count)
            {
                if (nesting == 0 && Peek(";")) return;
                if (Peek("[") || Peek("{") || Peek("(")) nesting++;
                else if (Peek("]") || Peek("}") || Peek(")")) nesting = Math.Max(0, nesting - 1);
                Index++;
            }
        }
        private void SkipBraceBlock()
        {
            var depth = 0;
            do
            {
                if (Peek("{")) depth++;
                else if (Peek("}")) depth--;
                Index++;
            } while (Index < tokens.Count && depth > 0);
        }
    }

    private sealed class LayoutCursor
    {
        public int Byte { get; set; }
        public int Bit { get; set; }
        public void FinishBits() { if (Bit != 0) { Byte++; Bit = 0; } }
        public void Even() { FinishBits(); if ((Byte & 1) != 0) Byte++; }
        public void AdvanceBits(long count)
        {
            var total = checked((long)Bit + count);
            Byte = checked(Byte + (int)(total / 8));
            Bit = (int)(total % 8);
        }
    }

    private sealed class LayoutCompiler(
        int dbNumber,
        string dbName,
        bool optimized,
        IReadOnlyDictionary<string, TypeNode> namedTypes,
        List<SiemensDbImportIssue> issues)
    {
        private readonly LayoutCursor _cursor = new();
        private readonly HashSet<StructNode> _activeStructs = new(ReferenceEqualityComparer.Instance);
        // 非优化 DB 按绝对地址访问：某个字段缺类型定义(不知道占多少字节)后，它后面的地址都算不出来
        private string? _brokenAt;
        public int SizeBytes { get; private set; }

        public void EmitRoot(StructNode root, TagGroupDef target)
        {
            foreach (var field in root.Fields) EmitField(field, target, field.Name, _cursor);
            _cursor.Even();
            SizeBytes = _cursor.Byte;
        }

        private void EmitField(FieldNode field, TagGroupDef target, string path, LayoutCursor cursor)
        {
            if (_brokenAt is not null)
            {
                issues.Add(new SiemensDbImportIssue(path, TypeName(field.Type),
                    $"前面的 {_brokenAt} 缺少类型定义，占用字节数未知，之后的地址无法计算"));
                return;
            }
            int before = issues.Count;
            EmitResolvedField(field, target, path, cursor);
            // 这个字段没导入(缺 UDT 等)：非优化 DB 后面的地址全部会错位，停止按地址排布
            if (!optimized && issues.Count > before) _brokenAt = path;
        }

        private void EmitResolvedField(FieldNode field, TagGroupDef target, string path, LayoutCursor cursor)
        {
            var resolved = Resolve(field.Type, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (resolved is null) return;
            if (resolved is ArrayNode array)
            {
                var definition = BuildArray(array, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (definition is null) return;
                Align(cursor, definition);
                var startByte = cursor.Byte;
                var startBit = cursor.Bit;
                if (!TryEmitStructArray(field.Name, field.Description, definition, target, path, startByte))
                    target.Tags.Add(NewTag(field.Name, Address(path, definition.ElementType, startByte, startBit),
                        definition.ElementType, field.Description, definition));
                Advance(cursor, definition);
                return;
            }
            if (resolved is StructNode structure)
            {
                cursor.Even();
                var startByte = cursor.Byte;
                var schema = CompileStruct(structure, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (schema is null) return;
                var group = new TagGroupDef { Name = field.Name, Description = field.Description };
                target.SubGroups.Add(group);
                EmitSchemaMembers(schema, group, path, startByte);
                cursor.Byte = checked(cursor.Byte + schema.SizeBytes);
                cursor.Bit = 0;
                return;
            }
            EmitScalar(field, target, path, cursor, resolved);
        }

        private void EmitScalar(FieldNode field, TagGroupDef target, string path, LayoutCursor cursor, TypeNode resolved)
        {
            Align(cursor, resolved);
            var startByte = cursor.Byte;
            var startBit = cursor.Bit;
            var (dataType, stringLength) = TypeIdentity(resolved);
            target.Tags.Add(NewTag(field.Name, Address(path, dataType, startByte, startBit),
                dataType, field.Description, null, stringLength));
            Advance(cursor, resolved);
        }

        /// <summary>
        /// 结构体数组(Array of "UDT" / Array of Struct，一维)和普通结构体一样展开成标记组：
        /// 每个元素一个组，组名 名字[下标](PLC 下标)，组里按结构继续生成子组和点位；
        /// 地址 = 数组起始字节 + (下标 - 下界) × 元素大小。普通数组、多维数组仍是一个数组点位。
        /// </summary>
        private bool TryEmitStructArray(string name, string? description, ArrayDef definition, TagGroupDef target, string path, int startByte)
        {
            if (definition.Struct is null || definition.Dimensions.Length != 1) return false;
            int lower = definition.LowerBounds.Length > 0 ? definition.LowerBounds[0] : 0;
            for (int offset = 0; offset < definition.Dimensions[0]; offset++)
            {
                int index = lower + offset;
                var group = new TagGroupDef { Name = $"{name}[{index}]", Description = description ?? "" };
                target.SubGroups.Add(group);
                EmitSchemaMembers(definition.Struct, group, $"{path}[{index}]",
                    checked(startByte + offset * definition.ElementStrideBytes));
            }
            return true;
        }

        private void EmitSchemaMembers(StructDef schema, TagGroupDef target, string path, int baseByte)
        {
            foreach (var field in schema.Fields)
            {
                var memberPath = $"{path}.{field.Name}";
                if (field.Array is not null)
                {
                    if (!TryEmitStructArray(field.Name, field.Description, field.Array, target, memberPath, baseByte + field.ByteOffset))
                        target.Tags.Add(NewTag(field.Name,
                            Address(memberPath, field.DataType, baseByte + field.ByteOffset, Math.Max(0, field.BitOffset)),
                            field.DataType, field.Description ?? "", field.Array, field.StringLength));
                }
                else if (field.Struct is not null)
                {
                    var group = new TagGroupDef { Name = field.Name, Description = field.Description ?? "" };
                    target.SubGroups.Add(group);
                    EmitSchemaMembers(field.Struct, group, memberPath, baseByte + field.ByteOffset);
                }
                else
                {
                    target.Tags.Add(NewTag(field.Name,
                        Address(memberPath, field.DataType, baseByte + field.ByteOffset, Math.Max(0, field.BitOffset)),
                        field.DataType, field.Description ?? "", null, field.StringLength));
                }
            }
        }

        private StructDef? CompileStruct(StructNode node, string path, HashSet<string> stack)
        {
            if (!_activeStructs.Add(node))
                throw new InvalidDataException($"UDT 存在递归循环:{path}");
            try
            {
            var cursor = new LayoutCursor();
            var fields = new List<StructFieldDef>();
            foreach (var field in node.Fields)
            {
                var fieldPath = $"{path}.{field.Name}";
                var resolved = Resolve(field.Type, fieldPath, stack);
                // 非优化 DB：成员缺类型时结构体大小和后面成员的偏移都不对，整个结构体不导入
                if (resolved is null) { if (optimized) continue; return null; }
                if (resolved is ArrayNode array)
                {
                    var definition = BuildArray(array, fieldPath, stack);
                    if (definition is null) { if (optimized) continue; return null; }
                    Align(cursor, definition);
                    fields.Add(new StructFieldDef
                    {
                        Name = field.Name, Description = field.Description, DataType = definition.ElementType,
                        ByteOffset = cursor.Byte, BitOffset = definition.ElementStrideBits > 0 ? cursor.Bit : -1,
                        StringLength = definition.StringLength, Array = definition,
                    });
                    Advance(cursor, definition);
                }
                else if (resolved is StructNode nested)
                {
                    cursor.Even();
                    var schema = CompileStruct(nested, fieldPath, stack);
                    if (schema is null) { if (optimized) continue; return null; }
                    fields.Add(new StructFieldDef
                    {
                        Name = field.Name, Description = field.Description, DataType = IgwDataType.Struct,
                        ByteOffset = cursor.Byte, Struct = schema,
                    });
                    cursor.Byte = checked(cursor.Byte + schema.SizeBytes);
                }
                else
                {
                    Align(cursor, resolved);
                    var (dataType, stringLength) = TypeIdentity(resolved);
                    fields.Add(new StructFieldDef
                    {
                        Name = field.Name, Description = field.Description, DataType = dataType,
                        ByteOffset = cursor.Byte, BitOffset = dataType == IgwDataType.Bool ? cursor.Bit : -1,
                        StringLength = stringLength,
                    });
                    Advance(cursor, resolved);
                }
            }
            cursor.Even();
            return new StructDef
            {
                Id = StableSchemaId(path), Name = path.Split('.').Last(), SizeBytes = cursor.Byte, Fields = fields,
            };
            }
            finally
            {
                _activeStructs.Remove(node);
            }
        }

        private ArrayDef? BuildArray(ArrayNode source, string path, HashSet<string> stack)
        {
            var dimensions = new List<int>();
            var lowerBounds = new List<int>();
            TypeNode element = source;
            var sourceParts = new List<string>();
            while (element is ArrayNode array)
            {
                foreach (var dimension in array.Dimensions)
                {
                    dimensions.Add(checked(dimension.Upper - dimension.Lower + 1));
                    lowerBounds.Add(dimension.Lower);
                    sourceParts.Add($"{dimension.Lower}..{dimension.Upper}");
                }
                element = array.Element;
            }
            var resolvedElement = Resolve(element, path, stack);
            if (resolvedElement is null) return null;   // Resolve 已记录缺少的类型
            element = resolvedElement;
            if (element is NamedNode unresolved)
            {
                AddIssue(path, unresolved.Name);
                return null;
            }

            var (dataType, stringLength) = TypeIdentity(element);
            StructDef? schema = null;
            var strideBytes = 0;
            var strideBits = 0;
            if (element is StructNode structure)
            {
                dataType = IgwDataType.Struct;
                schema = CompileStruct(structure, path, stack);
                if (schema is null) return null;
                strideBytes = schema.SizeBytes;
            }
            else if (dataType == IgwDataType.Bool) strideBits = 1;
            else strideBytes = ElementSize(element);

            return new ArrayDef
            {
                ElementType = dataType,
                Dimensions = dimensions.ToArray(), LowerBounds = lowerBounds.ToArray(),
                ElementStrideBytes = strideBytes, ElementStrideBits = strideBits,
                StringLength = stringLength, Struct = schema,
                SourceShape = $"ARRAY[{string.Join(',', sourceParts)}] OF {TypeName(element)}",
            };
        }

        private TypeNode? Resolve(TypeNode source, string path, HashSet<string> stack)
        {
            if (source is not NamedNode named) return source;
            if (!namedTypes.TryGetValue(named.Name, out var resolved))
            {
                AddIssue(path, named.Name);
                return null;
            }
            if (!stack.Add(named.Name))
                throw new InvalidDataException($"UDT 存在递归循环:{named.Name}");
            try { return Resolve(resolved, path, stack); }
            finally { stack.Remove(named.Name); }
        }

        private void AddIssue(string path, string type) => issues.Add(new SiemensDbImportIssue(
            path, type, Unsupported.TryGetValue(type, out var reason) ? reason : "找不到命名 UDT 定义"));

        private string Address(string path, IgwDataType type, int byteOffset, int bitOffset)
        {
            if (optimized) return $"{dbName}.{path}";
            return type switch
            {
                IgwDataType.Bool => $"DB{dbNumber}.DBX{byteOffset}.{bitOffset}",
                IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => $"DB{dbNumber}.DBW{byteOffset}",
                IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => $"DB{dbNumber}.DBD{byteOffset}",
                _ => $"DB{dbNumber}.DBB{byteOffset}",
            };
        }

        private static TagDef NewTag(string name, string address, IgwDataType dataType, string description,
            ArrayDef? array = null, int? stringLength = null) => new()
        {
            Name = name, Address = address, DataType = dataType,
            IsArray = array is not null, Array = array,
            Metadata = array is null ? null : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["array.sourceFormat"] = "Siemens DB",
                ["array.sourceAddress"] = address,
            },
            Access = TagAccess.ReadWrite, ScanRateMs = 100,
            Description = description, StringLength = stringLength,
        };

        private static void Align(LayoutCursor cursor, TypeNode type)
        {
            if (type is ScalarNode { IsBool: true }) return;
            if (type is ScalarNode { Bytes: 1 }) { cursor.FinishBits(); return; }
            cursor.Even();
        }

        private static void Align(LayoutCursor cursor, ArrayDef array)
        {
            if (array.ElementStrideBits > 0) return;
            if (array.ElementStrideBytes == 1) cursor.FinishBits();
            else cursor.Even();
        }

        private static void Advance(LayoutCursor cursor, TypeNode type)
        {
            switch (type)
            {
                case ScalarNode { IsBool: true }: cursor.AdvanceBits(1); break;
                case ScalarNode scalar: cursor.Byte = checked(cursor.Byte + scalar.Bytes); break;
                case StringNode text: cursor.Byte = checked(cursor.Byte + ElementSize(text)); break;
                default: throw new InvalidOperationException($"无法推进类型:{type.GetType().Name}");
            }
        }

        private static void Advance(LayoutCursor cursor, ArrayDef array)
        {
            if (array.ElementStrideBits > 0)
                cursor.AdvanceBits(checked(array.Shape.ElementCount * array.ElementStrideBits));
            else
                cursor.Byte = checked(cursor.Byte + checked((int)(array.Shape.ElementCount * array.ElementStrideBytes)));
        }

        private static int ElementSize(TypeNode type) => type switch
        {
            ScalarNode scalar => scalar.Bytes,
            StringNode text => Even(text.Wide ? checked(4 + 2 * text.Length) : checked(2 + text.Length)),
            _ => throw new InvalidOperationException("复合类型需要先编译布局"),
        };

        private static (IgwDataType DataType, int? StringLength) TypeIdentity(TypeNode type) => type switch
        {
            ScalarNode scalar => (scalar.DataType, scalar.DataType == IgwDataType.ByteArray ? scalar.Bytes : null),
            StringNode text => (text.Wide ? IgwDataType.WString : IgwDataType.String, text.Length),
            StructNode => (IgwDataType.Struct, null),
            _ => throw new InvalidOperationException($"类型尚未解析:{type}"),
        };

        private static int Even(int value) => (value & 1) == 0 ? value : checked(value + 1);
        private static string TypeName(TypeNode type) => type switch
        {
            ScalarNode scalar => scalar.Name,
            StringNode text => $"{(text.Wide ? "WSTRING" : "STRING")}[{text.Length}]",
            StructNode => "STRUCT",
            NamedNode named => named.Name,
            _ => "ARRAY",
        };

        private static string StableSchemaId(string path)
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            ulong hash = 14695981039346656037UL;
            foreach (var value in bytes) { hash ^= value; hash *= 1099511628211UL; }
            return $"s7-{hash:x16}";
        }
    }

    private static IReadOnlyDictionary<string, TypeNode> ParseNamedTypes(IReadOnlyList<Token> tokens)
    {
        var result = new Dictionary<string, TypeNode>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while ((index = FindWord(tokens, "TYPE", index)) >= 0)
        {
            var name = index + 1 < tokens.Count ? tokens[index + 1] : null;
            var structure = FindWord(tokens, "STRUCT", index + 2);
            var db = FindWord(tokens, "DATA_BLOCK", index + 2);
            if (name is null || name.Kind is not (TokenKind.Word or TokenKind.Quoted) ||
                structure < 0 || (db >= 0 && db < structure))
            { index++; continue; }
            var parser = new DeclarationParser(tokens, structure);
            result[name.Text] = parser.ParseStruct();
            index = parser.Index;
        }
        return result;
    }

    private static List<Token> Tokenize(string source)
    {
        var result = new List<Token>();
        for (var i = 0; i < source.Length;)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (source.AsSpan(i).StartsWith("//"))
            {
                var start = i;
                i += 2;
                var end = source.IndexOfAny(['\r', '\n'], i);
                if (end < 0) end = source.Length;
                result.Add(new Token(TokenKind.Comment, source[i..end].Trim(), start));
                i = end;
                continue;
            }
            if (source.AsSpan(i).StartsWith("(*"))
            {
                var start = i;
                var end = source.IndexOf("*)", i + 2, StringComparison.Ordinal);
                if (end < 0) end = source.Length;
                result.Add(new Token(TokenKind.Comment, source[(i + 2)..end].Trim(), start));
                i = end == source.Length ? end : end + 2;
                continue;
            }
            if (source[i] is '"' or '\'')
            {
                var quote = source[i];
                var start = i++;
                var builder = new StringBuilder();
                while (i < source.Length && source[i] != quote) builder.Append(source[i++]);
                if (i < source.Length) i++;
                result.Add(new Token(TokenKind.Quoted, builder.ToString(), start));
                continue;
            }
            if (i + 1 < source.Length && source.AsSpan(i, 2) is var pair &&
                (pair.SequenceEqual(":=") || pair.SequenceEqual("..")))
            {
                result.Add(new Token(TokenKind.Symbol, source.Substring(i, 2), i));
                i += 2;
                continue;
            }
            if ("[]:;,{}()".Contains(source[i]))
            {
                result.Add(new Token(TokenKind.Symbol, source[i].ToString(), i++));
                continue;
            }
            var number = Regex.Match(source[i..], @"^-?\d+");
            if (number.Success)
            {
                result.Add(new Token(TokenKind.Number, number.Value, i));
                i += number.Length;
                continue;
            }
            var word = Regex.Match(source[i..], @"^[\p{L}_][\p{L}\p{N}_]*");
            if (word.Success)
            {
                result.Add(new Token(TokenKind.Word, word.Value, i));
                i += word.Length;
                continue;
            }
            i++;
        }
        return result;
    }

    private static int FindWord(IReadOnlyList<Token> tokens, string text, int start = 0)
    {
        for (var i = start; i < tokens.Count; i++)
            if (tokens[i].Text.Equals(text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// DB 号只认明确写出来的：DATA_BLOCK DB7，或名字以 DB+数字 开头(如 "DB2100_CellData")。
    /// TIA 按符号名导出的源文件不带 DB 号，名字里的其它数字(如 "ST430_数据收集" 的 430)不是 DB 号，
    /// 这时用调用方给的 DB 号并标记为未知，由界面让用户填写。
    /// </summary>
    private static (int Number, string Name, bool Known) FindDb(string source, int defaultDb)
    {
        var match = Regex.Match(source,
            "\\bDATA_BLOCK\\s+(?:\"([^\"]+)\"|(DB\\s*\\d+)|([^\\s\\r\\n]+))", RegexOptions.IgnoreCase);
        var name = match.Success
            ? match.Groups.Cast<Group>().Skip(1).FirstOrDefault(static group => group.Success)?.Value.Trim() ?? ""
            : "";
        var explicitNumber = Regex.Match(source, @"\bDATA_BLOCK\s+DB\s*(\d+)", RegexOptions.IgnoreCase);
        if (explicitNumber.Success) return (int.Parse(explicitNumber.Groups[1].Value), Regex.Replace(name, @"\s+", ""), true);
        var prefixed = Regex.Match(name, @"^DB\s*(\d+)(?:\D|$)", RegexOptions.IgnoreCase);
        if (prefixed.Success) return (int.Parse(prefixed.Groups[1].Value), name, true);
        return (defaultDb, string.IsNullOrWhiteSpace(name) ? $"DB{defaultDb}" : name, false);
    }
}
