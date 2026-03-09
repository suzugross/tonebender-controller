using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using ToneBenderController.Models;

namespace ToneBenderController.Services;

public class DiskService : IDiskService
{
    public async Task<List<UsbDriveInfo>> GetUsbDrivesAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<UsbDriveInfo>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, Size, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");

            foreach (ManagementObject disk in searcher.Get())
            {
                int diskNumber = Convert.ToInt32(disk["Index"]);

                // System disk protection: skip disk 0
                if (diskNumber == 0) continue;

                string interfaceType = (disk["InterfaceType"]?.ToString() ?? "").ToLowerInvariant();
                string pnpDeviceId = (disk["PNPDeviceID"]?.ToString() ?? "").ToLowerInvariant();

                // USB detection (matches ToneBender C++ DiskManager logic)
                bool isBusUsb = interfaceType == "usb" || interfaceType == "sd";
                bool isPnpUsb = pnpDeviceId.Contains("usbstor") || pnpDeviceId.Contains("usb\\");

                if (!isBusUsb && !isPnpUsb) continue;

                long sizeBytes = 0;
                if (disk["Size"] != null)
                    long.TryParse(disk["Size"].ToString(), out sizeBytes);

                if (sizeBytes <= 0) continue;

                results.Add(new UsbDriveInfo
                {
                    DiskNumber = diskNumber,
                    FriendlyName = disk["Model"]?.ToString() ?? "Unknown USB Drive",
                    SizeBytes = sizeBytes
                });
            }

            return results;
        });
    }

    public async Task<bool> IsUefiFirmwareAsync()
    {
        return await Task.Run(() =>
        {
            // Primary: GetFirmwareType API (Windows 8+)
            try
            {
                GetFirmwareType(out var firmwareType);
                return firmwareType == FirmwareType.Uefi;
            }
            catch
            {
                // Fallback: registry check
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"System\CurrentControlSet\Control\SecureBoot\State");
                    return key != null;
                }
                catch
                {
                    return true; // Default to UEFI on modern hardware
                }
            }
        });
    }

    public async Task<UsbPartitionResult> PartitionDriveAsync(
        int diskNumber, UsbPartitionConfig config, IProgress<string>? progress = null)
    {
        // Safety: refuse disk 0
        if (diskNumber == 0)
            throw new InvalidOperationException("Cannot partition system disk (Disk 0).");

        progress?.Report("Finding available drive letters...");
        var usedLetters = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => d.Name[0]));

        char winPeLetter = FindAvailableLetter(['P', 'S', 'T', 'U'], usedLetters);
        usedLetters.Add(winPeLetter);
        char winInstLetter = FindAvailableLetter(['Q', 'V', 'W', 'X'], usedLetters);
        usedLetters.Add(winInstLetter);
        char dataLetter = FindAvailableLetter(['R', 'Y', 'Z', 'N'], usedLetters);

        // Disable automount to prevent Windows from locking partitions during format
        await RunDiskpartCommandAsync("automount disable");
        try
        {
            progress?.Report("Partitioning USB drive...");

            // Single diskpart script: clean → create partitions → format → assign
            // USB drives always use MBR (auto-initialized by first "create partition").
            // No "convert gpt/mbr" needed — diskpart auto-initializes as MBR.
            // No "offline/online disk" — not supported on removable media.
            string script = BuildPartitionScript(diskNumber, config,
                winPeLetter, winInstLetter, dataLetter);

            var (exitCode, output) = await RunDiskpartScriptAsync(script);
            if (exitCode != 0 || HasDiskpartError(output))
            {
                return new UsbPartitionResult
                {
                    Success = false,
                    ErrorMessage = $"Partitioning failed:\n{output}"
                };
            }

            // Best-effort: set WINPE partition as active for Legacy BIOS/CSM boot.
            // "active" is unsupported on some removable USB media — failure is harmless.
            try
            {
                await RunDiskpartScriptAsync(
                    $"select disk {diskNumber}\r\nselect partition 1\r\nactive\r\nexit\r\n");
            }
            catch { /* swallow — UEFI doesn't need active flag */ }
        }
        finally
        {
            await RunDiskpartCommandAsync("automount enable");
        }

        // ── Create template directories ──
        progress?.Report("Creating directory structure...");
        await Task.Delay(2000); // Let Windows detect the new partitions
        CreateTemplateDirectories(dataLetter);

        progress?.Report("USB drive ready.");

        return new UsbPartitionResult
        {
            Success = true,
            WinPeLetter = winPeLetter,
            WinInstLetter = winInstLetter,
            DataLetter = dataLetter
        };
    }

    // ── Private helpers ──────────────────────────────────────────

    private static bool HasDiskpartError(string output)
    {
        return output.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("The request could not be performed", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("仮想ディスク サービス エラー") ||
               output.Contains("要求を実行できませんでした");
    }

    private static async Task RunDiskpartCommandAsync(string command)
    {
        await RunDiskpartScriptAsync(command + "\r\nexit\r\n");
    }

    private static async Task<(int ExitCode, string Output)> RunDiskpartScriptAsync(string script)
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, script);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart",
                Arguments = $"/s \"{tempFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();

            // Timeout: 120 seconds
            if (await Task.WhenAny(exitTask, Task.Delay(120_000)) != exitTask)
            {
                process.Kill();
                return (-1, "diskpart timed out after 120 seconds.");
            }

            string stdout = await stdoutTask;
            return (process.ExitCode, stdout);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static string BuildPartitionScript(
        int diskNumber, UsbPartitionConfig config,
        char winPeLetter, char winInstLetter, char dataLetter)
    {
        // Single script: clean → create partitions → format → assign.
        // After "clean", diskpart auto-initializes as MBR on first "create partition".
        var lines = new List<string>
        {
            $"select disk {diskNumber}",
            "attributes disk clear readonly noerr",
            "clean",
            $"create partition primary size={config.WinPeSizeMB}",
            "format quick fs=fat32 label=\"WINPE\"",
            $"assign letter={winPeLetter}",
            $"create partition primary size={config.WinInstSizeMB}",
            "format quick fs=fat32 label=\"WININST\"",
            $"assign letter={winInstLetter}",
            "create partition primary",
            "format quick fs=ntfs label=\"DATA\"",
            $"assign letter={dataLetter}",
            "exit",
            ""
        };

        return string.Join("\r\n", lines);
    }

    private static char FindAvailableLetter(char[] candidates, HashSet<char> usedLetters)
    {
        foreach (char c in candidates)
        {
            if (!usedLetters.Contains(c))
                return c;
        }

        // Fallback: search from Z downward
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!usedLetters.Contains(c))
                return c;
        }

        throw new InvalidOperationException("No available drive letters.");
    }

    private static void CreateTemplateDirectories(char dataLetter)
    {
        string root = $"{dataLetter}:\\";

        // Create [IMAGE] directory — ToneBender scans for this
        string imageDir = Path.Combine(root, "[IMAGE]");
        Directory.CreateDirectory(imageDir);

        // Create default capture-config.json
        string configPath = Path.Combine(root, "capture-config.json");
        var defaultConfig = new
        {
            captureFormat = "ffu",
            wimCompression = "fast",
            outputDir = "[IMAGE]",
            powerPlan = "high-performance",
            postCapture = "shutdown"
        };
        string json = JsonSerializer.Serialize(defaultConfig,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    public async Task<bool> DeployWinPeAsync(
        string mediaSourceDir, char winPeDriveLetter, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(mediaSourceDir))
            throw new DirectoryNotFoundException(
                $"WinPE media directory not found: {mediaSourceDir}");

        progress?.Report($"Deploying WinPE to {winPeDriveLetter}:\\...");

        // Note: destination must NOT be wrapped in quotes — "P:\" causes \"
        // to be parsed as an escaped quote, breaking the entire argument list.
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy",
            Arguments = $"\"{mediaSourceDir}\" {winPeDriveLetter}:\\ /MIR /NP /R:3 /W:2",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start robocopy.");

        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // robocopy exit codes: 0-7 = success, >=8 = error
        if (process.ExitCode >= 8)
        {
            progress?.Report($"Deploy failed (robocopy exit code {process.ExitCode}).");
            throw new InvalidOperationException(
                $"robocopy failed (exit code {process.ExitCode}):\n{stdout}");
        }

        progress?.Report("WinPE deployed to USB.");
        return true;
    }

    // ── P/Invoke ─────────────────────────────────────────────────

    private enum FirmwareType
    {
        Unknown = 0,
        Bios = 1,
        Uefi = 2,
        Max = 3
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out FirmwareType firmwareType);
}
