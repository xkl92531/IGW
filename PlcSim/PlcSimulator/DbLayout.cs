namespace PlcSimulator
{
    /// <summary>S7 基础数据类型</summary>
    public enum S7DataType
    {
        Bool,
        Int,
        Real,
        S7String
    }

    /// <summary>一个数据块定义（对应西门子 DB）</summary>
    public class DbBlockDef
    {
        public int DbNumber { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public int Size { get; set; }
    }

    /// <summary>数据块内的一个字段（含绝对字节偏移，S7_Optimized_Access := FALSE）</summary>
    public class FieldDef
    {
        public int DbNumber { get; set; }
        public string Name { get; set; }
        public S7DataType Type { get; set; }
        public int ByteOffset { get; set; }
        public int BitOffset { get; set; }
        /// <summary>仅 S7String 有效：字符串最大长度（不含 2 字节头）</summary>
        public int StringMaxLen { get; set; }
        public string Comment { get; set; }

        /// <summary> Kepware/TIA 风格绝对地址显示 </summary>
        public string AddressText
        {
            get
            {
                switch (Type)
                {
                    case S7DataType.Bool: return $"DB{DbNumber}.DBX{ByteOffset}.{BitOffset}";
                    case S7DataType.Int: return $"DB{DbNumber}.DBW{ByteOffset}";
                    case S7DataType.Real: return $"DB{DbNumber}.DBD{ByteOffset}";
                    default: return $"DB{DbNumber}.DBB{ByteOffset} (String[{StringMaxLen}])";
                }
            }
        }

        /// <summary>HslCommunication 服务器内部地址</summary>
        public string HslAddress => $"DB{DbNumber}.{ByteOffset}";

        public string TypeText
        {
            get
            {
                switch (Type)
                {
                    case S7DataType.Bool: return "Bool";
                    case S7DataType.Int: return "Int";
                    case S7DataType.Real: return "Real";
                    default: return $"String[{StringMaxLen}]";
                }
            }
        }
    }

    /// <summary>
    /// DB2100~2103 点表布局（来源：仓库根目录 DB2100~2103 四个 SCL DATA_BLOCK 定义文件）。
    /// 非优化访问，字节偏移按 S7 对齐规则手工核算。
    /// </summary>
    public static class DbLayout
    {
        public const int CellCount = 11;           // Array[0..10]
        public const int CellStride = 166;         // 52(String50) + 4 + 4 + 102(String100) + 2 + 2
        public const int CellDataBase = 6;         // DB2100 中 CellData 起始字节

        public static readonly List<DbBlockDef> Blocks = new List<DbBlockDef>
        {
            new DbBlockDef { DbNumber = 2100, Name = "DB2100", Title = "DB2100 电芯OCV_DataDB", Size = 1836 },
            new DbBlockDef { DbNumber = 2101, Name = "DB2101", Title = "DB2101 设备异常",       Size = 106  },
            new DbBlockDef { DbNumber = 2102, Name = "DB2102", Title = "DB2102 在线离线登录",    Size = 6    },
            new DbBlockDef { DbNumber = 2103, Name = "DB2103", Title = "DB2103 刷卡登录",        Size = 8    },
        };

        public static readonly List<FieldDef> Fields = BuildFields();

        private static List<FieldDef> BuildFields()
        {
            var list = new List<FieldDef>
            {
                // ---------- DB2100 ----------
                new FieldDef { DbNumber = 2100, Name = "Request",     Type = S7DataType.Bool, ByteOffset = 0, BitOffset = 0, Comment = "请求（1:K值请求 2:出站请求）" },
                new FieldDef { DbNumber = 2100, Name = "PLCFlag",     Type = S7DataType.Int,  ByteOffset = 2, Comment = "PLC标志位 1=等待MES响应，3=电气操作完成" },
                new FieldDef { DbNumber = 2100, Name = "MESFlag",     Type = S7DataType.Int,  ByteOffset = 4, Comment = "MES标志位 2=MES响应完成，4=流程结束" },
            };

            for (int i = 0; i < CellCount; i++)
            {
                int b = CellDataBase + i * CellStride;
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].DMCCode",      Type = S7DataType.S7String, ByteOffset = b + 0,   StringMaxLen = 50,  Comment = "条码" });
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].KValue",       Type = S7DataType.Real,     ByteOffset = b + 52,  Comment = "K值" });
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].Voltage",      Type = S7DataType.Real,     ByteOffset = b + 56,  Comment = "电压值" });
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].ErrorMessage", Type = S7DataType.S7String, ByteOffset = b + 60,  StringMaxLen = 100, Comment = "MES返回NG原因(中文描述)" });
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].IsNG",         Type = S7DataType.Int,      ByteOffset = b + 162, Comment = "电芯状态(0:初始 1:OK 2:NG 3:清线)" });
                list.Add(new FieldDef { DbNumber = 2100, Name = $"CellData[{i}].Errorinfo",    Type = S7DataType.Int,      ByteOffset = b + 164, Comment = "异常信息" });
            }

            list.Add(new FieldDef { DbNumber = 2100, Name = "Temperature", Type = S7DataType.Real, ByteOffset = CellDataBase + CellCount * CellStride, Comment = "环境温度" });

            // ---------- DB2101 ----------
            list.Add(new FieldDef { DbNumber = 2101, Name = "PLCFlag",      Type = S7DataType.Int,      ByteOffset = 0, Comment = "PLC标志位 1=等待MES响应，3=电气操作完成" });
            list.Add(new FieldDef { DbNumber = 2101, Name = "MESFlag",      Type = S7DataType.Int,      ByteOffset = 2, Comment = "MES标志位 2=MES响应完成，4=流程结束" });
            list.Add(new FieldDef { DbNumber = 2101, Name = "ErrorMessage", Type = S7DataType.S7String, ByteOffset = 4, StringMaxLen = 100, Comment = "NG原因(中文描述)" });

            // ---------- DB2102 ----------
            list.Add(new FieldDef { DbNumber = 2102, Name = "PLCFlag", Type = S7DataType.Int, ByteOffset = 0, Comment = "PLC标志位 1=等待MES响应，3=电气操作完成" });
            list.Add(new FieldDef { DbNumber = 2102, Name = "MESFlag", Type = S7DataType.Int, ByteOffset = 2, Comment = "MES标志位 2=MES响应完成，4=流程结束" });
            list.Add(new FieldDef { DbNumber = 2102, Name = "Offline", Type = S7DataType.Int, ByteOffset = 4, Comment = "是否在线" });

            // ---------- DB2103（注意：源定义中 Account/Password/LoginType 类型为 Bool，与注释语义不符，疑似现场导出有误，此处按 Bool 保留） ----------
            list.Add(new FieldDef { DbNumber = 2103, Name = "PLCFlag",   Type = S7DataType.Int,  ByteOffset = 0, Comment = "PLC标志位 1=等待MES响应，3=电气操作完成" });
            list.Add(new FieldDef { DbNumber = 2103, Name = "MESFlag",   Type = S7DataType.Int,  ByteOffset = 2, Comment = "MES标志位 2=MES响应完成，4=流程结束" });
            list.Add(new FieldDef { DbNumber = 2103, Name = "Account",   Type = S7DataType.Bool, ByteOffset = 4, BitOffset = 0, Comment = "账号/卡号（源文件类型Bool疑有误，应为String）" });
            list.Add(new FieldDef { DbNumber = 2103, Name = "Password",  Type = S7DataType.Bool, ByteOffset = 5, BitOffset = 0, Comment = "密码（源文件类型Bool疑有误，应为String）" });
            list.Add(new FieldDef { DbNumber = 2103, Name = "LoginType", Type = S7DataType.Bool, ByteOffset = 6, BitOffset = 0, Comment = "类型：1-账号密码校验 2-刷卡校验 3-注销（源文件类型Bool疑有误，应为Int）" });

            return list;
        }

        public static FieldDef Find(int dbNumber, string name) =>
            Fields.First(f => f.DbNumber == dbNumber && f.Name == name);
    }
}
