// zig 包装器：交叉编译 libigw_s7plus.so 时当作 C 编译器/链接器/归档器。
//
// rustc 以 CC 风格调用链接器时不传目标三元组，zig 会按宿主(Windows)去链接，找不到 Linux 的运行时；
// 所以这里统一注入 zig 认的 `-target x86_64-linux-gnu.2.17`，并丢掉 LLVM 风格的 `--target=...`。
// glibc 钉在 2.17：容器基础镜像(noble)和现场的老发行版都能装载。
//
// 按可执行文件名分工：名字以 ar 结尾 → `zig ar`，否则 → `zig cc`。
// zig 本体的位置：环境变量 IGW_ZIG > 与本程序同目录的 zig.exe > PATH 里的 zig.exe。
//
// 本文件必须留在版本库里：它是交叉编译链的一环，之前只存在于被 .gitignore 排除的 rundata\ 下，
// 换一台机器就重建不出来(现场那次 libigw_s7plus.so 迟迟没跟着升级，就绊在这里)。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class ZigWrap
{
    private const string ZigTarget = "x86_64-linux-gnu.2.17";

    private static int Main(string[] args)
    {
        string zig = FindZig();
        if (zig == null)
        {
            Console.Error.WriteLine("zigwrap: 找不到 zig.exe（设置 IGW_ZIG，或放到本程序同目录/PATH）");
            return 127;
        }

        string self = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
        bool archiver = self.EndsWith("ar", StringComparison.OrdinalIgnoreCase);

        var passed = new List<string> { archiver ? "ar" : "cc" };
        if (!archiver)
        {
            passed.Add("-target");
            passed.Add(ZigTarget);
        }
        foreach (string argument in args)
        {
            // rustc/cc-rs 传的 LLVM 风格目标由上面的 -target 取代，重复传会让 zig 报错
            if (!archiver && argument.StartsWith("--target=", StringComparison.OrdinalIgnoreCase)) continue;
            passed.Add(argument);
        }

        var start = new ProcessStartInfo(zig, Join(passed))
        {
            UseShellExecute = false,
        };
        using (var child = Process.Start(start))
        {
            child.WaitForExit();
            return child.ExitCode;
        }
    }

    private static string FindZig()
    {
        string configured = Environment.GetEnvironmentVariable("IGW_ZIG");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        string beside = Path.Combine(
            Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? ".", "zig.exe");
        if (File.Exists(beside)) return beside;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(directory, "zig.exe"); }
            catch (ArgumentException) { continue; }   // PATH 里可能有非法字符的条目
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>把参数拼成 Windows 命令行（.NET Framework 没有 ArgumentList，只能自己加引号）。</summary>
    private static string Join(List<string> arguments)
    {
        var line = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append(Quote(argument));
        }
        return line.ToString();
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
