namespace IGW.Abstractions;

/// <summary>
/// 可写数据根目录:默认 <c>%ProgramData%\IGW</c>(如 C:\ProgramData\IGW)。
/// 为什么不写在安装目录:Program Files 普通用户没有写权限(Runtime 起不来),
/// 且卸载/重装会连同安装目录一起删掉(配置丢失)。ProgramData 普通用户可写、安装器不碰。
/// 可用环境变量 <c>IGW_DATA_DIR</c> 覆盖(开发/多实例隔离测试用;生产用默认即可)。
/// Runtime、数据库配置、数据迁移等进程都用这里,保证找同一份工程文件。
/// </summary>
public static class IgwPaths
{
    /// <summary>数据根目录(进程内只算一次;已确保存在)。</summary>
    public static string DataDir { get; } = Resolve();

    /// <summary>默认工程文件路径 %ProgramData%\IGW\project.igwproj。</summary>
    public static string ProjectFile => Path.Combine(DataDir, "project.igwproj");

    /// <summary>日志目录。</summary>
    public static string LogsDir => Path.Combine(DataDir, "logs");

    private static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("IGW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try { Directory.CreateDirectory(env); } catch { }
            return env;
        }
        if (OperatingSystem.IsWindows())
        {
            string win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IGW");   // C:\ProgramData\IGW
            try { Directory.CreateDirectory(win); } catch { }
            return win;
        }
        // Linux:服务(root)标准位 /var/lib/igw;非 root(开发/手跑)写不进则退 ~/.local/share/igw
        const string sys = "/var/lib/igw";
        if (IsWritable(sys)) return sys;
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "igw");   // ~/.local/share/igw
        try { Directory.CreateDirectory(home); } catch { }
        return home;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".wprobe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 首次运行迁移:把老版本写在安装目录(appDir)里的用户数据搬到 DataDir(仅当 DataDir 里还没有)。
    /// 只复制不删除(安全,可回退);让"从旧版原地升级"的用户工程/授权/历史无缝续上。
    /// </summary>
    public static void MigrateFromLegacy(string appDir)
    {
        try
        {
            // 早期版本把正式工程也命名为 demo.igwproj。升级时复制到正式文件名，绝不删除或覆盖旧文件。
            CopyIfMissing(Path.Combine(DataDir, "demo.igwproj"), ProjectFile);

            if (string.Equals(Path.GetFullPath(appDir).TrimEnd('\\'),
                              Path.GetFullPath(DataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;   // appDir 就是 DataDir(如用 IGW_DATA_DIR 指到安装目录),无需迁移

            // 单文件:工程 / 授权 / 防回拨 / 各 SQLite 库(含 -wal/-shm)
            CopyIfMissing(Path.Combine(appDir, "project.igwproj"), ProjectFile);
            CopyIfMissing(Path.Combine(appDir, "demo.igwproj"), ProjectFile);
            foreach (var name in new[] { "license.key", ".igwlic" })
                CopyIfMissing(Path.Combine(appDir, name), Path.Combine(DataDir, name));
            foreach (var db in SafeGlob(appDir, "*.db"))
            {
                string baseName = Path.GetFileName(db);
                foreach (var suf in new[] { "", "-wal", "-shm" })
                    CopyIfMissing(db + suf, Path.Combine(DataDir, baseName + suf));
            }
            // 目录:OPC UA 证书(整目录搬,保留已被客户端信任的自签证书)
            CopyDirIfMissing(Path.Combine(appDir, "pki"), Path.Combine(DataDir, "pki"));
        }
        catch { /* 迁移失败不致命:后续按空工程/新证书继续 */ }
    }

    private static IEnumerable<string> SafeGlob(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void CopyIfMissing(string src, string dst)
    {
        try { if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst); } catch { }
    }

    private static void CopyDirIfMissing(string srcDir, string dstDir)
    {
        try
        {
            if (!Directory.Exists(srcDir) || Directory.Exists(dstDir)) return;
            foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, f);
                string target = Path.Combine(dstDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: false);
            }
        }
        catch { }
    }
}
