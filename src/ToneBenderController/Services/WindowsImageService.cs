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
        int lastReported = -1;
        while (!process.StandardOutput.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            string? line = await process.StandardOutput.ReadLineAsync();
            if (line == null) break;

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
