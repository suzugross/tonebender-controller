using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ToneBenderController.Models;

namespace ToneBenderController.Services;

public class PowerShellService : IPowerShellService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ScriptDir { get; }

    public PowerShellService()
    {
        ScriptDir = FindScriptDir();
    }

    public async Task RunBuildAsync(
        string profilePath,
        string? driverPath = null,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scriptPath = Path.Combine(ScriptDir, "tonebender.ps1");

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ProfilePath \"{profilePath}\"";
        if (!string.IsNullOrEmpty(driverPath))
            args += $" -DriverPath \"{driverPath}\"";

        await RunPsScriptAsync(args, progress, ct);
    }

    public async Task RunRegenerateIsoAsync(
        string workspaceDir,
        string isoPath,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scriptPath = Path.Combine(ScriptDir, "regenerate-pe-iso.ps1");

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                   $"-WorkDir \"{workspaceDir}\" -IsoPath \"{isoPath}\"";

        await RunPsScriptAsync(args, progress, ct);
    }

    private static async Task RunPsScriptAsync(
        string arguments,
        IProgress<BuildProgress>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell process.");

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try
            {
                var bp = JsonSerializer.Deserialize<BuildProgress>(e.Data, JsonOptions);
                if (bp is { Type: "progress" })
                    progress?.Report(bp);
            }
            catch
            {
                // Non-JSON lines (verbose output, etc.) — ignore
            }
        };
        process.BeginOutputReadLine();

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(ct);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException(
                $"PowerShell script exited with code {process.ExitCode}.\n{stderr}");
        }
    }

    private static string FindScriptDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tonebender.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "tonebender.ps1 not found. Run the app from within the repository.");
    }
}
