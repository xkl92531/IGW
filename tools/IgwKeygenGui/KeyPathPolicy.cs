using System.IO;

namespace IgwKeygenGui;

public static class KeyPathPolicy
{
    public static string ResolvePrivateKey(string appDirectory, string? environmentPath)
    {
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
            return Path.GetFullPath(environmentPath);

        string appDirectoryFull = Path.GetFullPath(appDirectory);
        string besideExecutable = Path.Combine(appDirectoryFull, "private.key");
        if (File.Exists(besideExecutable))
            return besideExecutable;

        for (var directory = new DirectoryInfo(appDirectoryFull); directory != null; directory = directory.Parent)
        {
            string sourceTreeKey = Path.Combine(directory.FullName, "tools", "IgwKeygen", "private.key");
            if (File.Exists(sourceTreeKey))
                return sourceTreeKey;

            string ancestorKey = Path.Combine(directory.FullName, "private.key");
            if (File.Exists(ancestorKey))
                return ancestorKey;
        }

        return besideExecutable;
    }

    public static string GetDialogDirectory(string? keyPath, string appDirectory)
    {
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            try
            {
                string? keyDirectory = Path.GetDirectoryName(Path.GetFullPath(keyPath));
                if (!string.IsNullOrWhiteSpace(keyDirectory) && Directory.Exists(keyDirectory))
                    return keyDirectory;
            }
            catch
            {
                // Invalid or stale paths fall through to a portable existing directory.
            }
        }

        try
        {
            string appDirectoryFull = Path.GetFullPath(appDirectory);
            if (Directory.Exists(appDirectoryFull))
                return appDirectoryFull;
        }
        catch
        {
            // Fall through to the user's documents directory.
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
    }
}
