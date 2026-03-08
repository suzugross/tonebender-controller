using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ToneBenderController.Models;

namespace ToneBenderController.Services;

public class WindowsImageService : IWindowsImageService
{
    public async Task<char> MountIsoAsync(string isoPath)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException("ISO file not found.", isoPath);

        // Mount ISO and get drive letter via PowerShell
        string psCommand = $"(Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru | Get-Volume).DriveLetter";

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -Command \"{psCommand}\"",
            timeoutMs: 30_000);

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Failed to mount ISO (exit code {exitCode}):\n{stderr}");

        string letter = stdout.Trim();
        if (letter.Length == 1 && char.IsLetter(letter[0]))
            return letter[0];

        throw new InvalidOperationException(
            $"Unexpected drive letter from Mount-DiskImage: \"{stdout.Trim()}\"");
    }

    public async Task UnmountIsoAsync(string isoPath)
    {
        // Best-effort unmount — swallow all exceptions
        try
        {
            string psCommand = $"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'";
            await RunProcessAsync(
                "powershell.exe",
                $"-NoProfile -Command \"{psCommand}\"",
                timeoutMs: 15_000);
        }
        catch
        {
            // Intentionally swallowed
        }
    }

    public async Task<List<WimEdition>> GetWimEditionsAsync(string wimFilePath)
    {
        if (!File.Exists(wimFilePath))
            throw new FileNotFoundException("WIM/ESD file not found.", wimFilePath);

        var (exitCode, stdout, _) = await RunProcessAsync(
            "dism.exe",
            $"/Get-WimInfo /WimFile:\"{wimFilePath}\"",
            timeoutMs: 60_000);

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"DISM /Get-WimInfo failed (exit code {exitCode}):\n{stdout}");

        return ParseWimInfo(stdout);
    }

    public async Task ExportEditionAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Ensure destination directory exists
        string? destDir = Path.GetDirectoryName(destinationWimPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        var psi = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = $"/Export-Image /SourceImageFile:\"{sourceWimPath}\" " +
                        $"/SourceIndex:{sourceIndex} " +
                        $"/DestinationImageFile:\"{destinationWimPath}\" " +
                        $"/Compress:max",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dism.exe.");

        // Read stdout line-by-line for progress parsing
        // NOTE: Do NOT use EndOfStream — it is a synchronous blocking property
        // that freezes the UI thread when DISM output is infrequent (e.g. USB writes).
        int lastReported = -1;
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();

            // Parse progress: "[ 58.3% ]" or "[==== 58.3% ====]"
            var match = Regex.Match(line, @"\[\s*=*\s*(\d+[\.,]\d+)\s*%\s*=*\s*\]");
            if (match.Success)
            {
                string numStr = match.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    int percent = (int)pct;
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        progress?.Report(percent);
                    }
                }
            }
        }

        // Wait for exit with cancellation support
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"DISM /Export-Image failed (exit code {process.ExitCode}).");

        progress?.Report(100);
    }

    public async Task InjectDriversIntoWimAsync(
        string wimPath, string driverPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(wimPath))
            throw new FileNotFoundException("WIM file not found.", wimPath);
        if (!Directory.Exists(driverPath))
            throw new DirectoryNotFoundException($"Driver directory not found: {driverPath}");

        // Create temporary mount directory next to the WIM
        string mountDir = Path.Combine(
            Path.GetDirectoryName(wimPath)!,
            "_drvmount_" + Path.GetRandomFileName());
        Directory.CreateDirectory(mountDir);

        try
        {
            // Mount WIM (index 1 — exported WIM always has a single index)
            progress?.Report("Mounting WIM for driver injection...");
            ct.ThrowIfCancellationRequested();

            var (mountExit, _, mountErr) = await RunProcessAsync(
                "dism.exe",
                $"/Mount-Wim /WimFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\"",
                timeoutMs: 120_000);

            if (mountExit != 0)
                throw new InvalidOperationException(
                    $"DISM /Mount-Wim failed (exit code {mountExit}):\n{mountErr}");

            // Inject drivers
            progress?.Report("Injecting OEM drivers...");
            ct.ThrowIfCancellationRequested();

            var (drvExit, drvOut, drvErr) = await RunProcessAsync(
                "dism.exe",
                $"/Image:\"{mountDir}\" /Add-Driver /Driver:\"{driverPath}\" /Recurse",
                timeoutMs: 300_000);

            if (drvExit != 0)
                throw new InvalidOperationException(
                    $"DISM /Add-Driver failed (exit code {drvExit}):\n{drvOut}\n{drvErr}");

            // Unmount with commit
            progress?.Report("Unmounting WIM (commit)...");
            ct.ThrowIfCancellationRequested();

            var (unmountExit, _, unmountErr) = await RunProcessAsync(
                "dism.exe",
                $"/Unmount-Wim /MountDir:\"{mountDir}\" /Commit",
                timeoutMs: 120_000);

            if (unmountExit != 0)
                throw new InvalidOperationException(
                    $"DISM /Unmount-Wim failed (exit code {unmountExit}):\n{unmountErr}");

            progress?.Report("Driver injection complete.");
        }
        catch
        {
            // Best-effort discard unmount on failure
            try
            {
                await RunProcessAsync(
                    "dism.exe",
                    $"/Unmount-Wim /MountDir:\"{mountDir}\" /Discard",
                    timeoutMs: 60_000);
            }
            catch { /* swallow */ }

            throw;
        }
        finally
        {
            // Clean up temp mount directory
            try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, true); }
            catch { /* swallow */ }
        }
    }

    // ── Private helpers ──────────────────────────────────────────

    private static List<WimEdition> ParseWimInfo(string output)
    {
        var editions = new List<WimEdition>();
        WimEdition? current = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();

            // Match both English and Japanese DISM output
            if (TryMatchField(line, "Index", "インデックス", out string? indexVal))
            {
                if (int.TryParse(indexVal, out int idx))
                {
                    current = new WimEdition { Index = idx };
                    editions.Add(current);
                }
            }
            else if (current != null && TryMatchField(line, "Name", "名前", out string? nameVal))
            {
                current.Name = nameVal!;
            }
            else if (current != null && TryMatchField(line, "Description", "説明", out string? descVal))
            {
                current.Description = descVal!;
            }
            else if (current != null && TryMatchField(line, "Size", "サイズ", out string? sizeVal))
            {
                current.SizeBytes = ParseSizeString(sizeVal!);
            }
        }

        return editions;
    }

    private static bool TryMatchField(string line, string enKey, string jaKey, out string? value)
    {
        // Format: "Key : Value" or "Key：Value"
        foreach (string key in new[] { enKey, jaKey })
        {
            string prefix1 = key + " : ";
            string prefix2 = key + "：";
            string prefix3 = key + ": ";

            if (line.StartsWith(prefix1, StringComparison.OrdinalIgnoreCase))
            {
                value = line[prefix1.Length..].Trim();
                return true;
            }
            if (line.StartsWith(prefix2, StringComparison.OrdinalIgnoreCase))
            {
                value = line[prefix2.Length..].Trim();
                return true;
            }
            if (line.StartsWith(prefix3, StringComparison.OrdinalIgnoreCase))
            {
                value = line[prefix3.Length..].Trim();
                return true;
            }
        }

        value = null;
        return false;
    }

    private static long ParseSizeString(string sizeStr)
    {
        // DISM outputs size like "4,123,456,789 bytes" or "4.123.456.789 バイト"
        // Remove all non-digit characters except for the number
        string digits = Regex.Replace(sizeStr, @"[^\d]", "");
        return long.TryParse(digits, out long bytes) ? bytes : 0;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();

        if (await Task.WhenAny(exitTask, Task.Delay(timeoutMs)) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "", $"{fileName} timed out after {timeoutMs / 1000} seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }
}
