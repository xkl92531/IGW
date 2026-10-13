using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeStartupContractTests
{
    [Fact]
    public async Task RestPortConflict_ExitsCleanlyWithoutUnhandledRuntimeCrash()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string root = Path.Combine(Path.GetTempPath(), "IGW-PortConflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "port-conflict.igwproj");
        string projectJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            name = "port-conflict-test",
            settings = new { restPort = port, opcUaPort = 4840, managementPort = 0 },
            opcUa = new { enabled = false, port = 4840 },
            channels = Array.Empty<object>(),
        });
        await File.WriteAllTextAsync(projectPath, projectJson);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string runtimeDll = RepoFile("src", "IGW.Runtime", "bin", configuration, "net10.0", "IGW.Runtime.dll");
        Assert.True(File.Exists(runtimeDll), $"Runtime test binary not found: {runtimeDll}");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet", $"\"{runtimeDll}\" \"{projectPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = root,
                },
            };
            process.StartInfo.Environment["IGW_DATA_DIR"] = Path.Combine(root, "data");
            Assert.True(process.Start());
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            string output = (await stdoutTask) + (await stderrTask);

            Assert.True(process.ExitCode == 98,
                $"Expected clean port-conflict exit code 98, actual {process.ExitCode}. Output:{Environment.NewLine}{output}");
            Assert.Contains($"REST 端口 {port} 已被其他程序占用", output);
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IGW.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }
}
