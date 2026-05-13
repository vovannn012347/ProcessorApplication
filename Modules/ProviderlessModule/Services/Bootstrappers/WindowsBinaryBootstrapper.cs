using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Registry;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Bootstrappers;

public class WindowsBinaryBootstrapper : IBinaryBootstrapper
{
    private readonly ITunnelSelector _selector;

    public WindowsBinaryBootstrapper(
        ITunnelSelector selector
        )
    {
        _selector = selector;
    }

    public async Task EnsureBinariesAsync(CancellationToken ct = default)
    {
        var provider = _selector.GetActiveProvider();
        string finalBinaryPath = provider.CurrentBinaryPath; // e.g., .../bin/ngrok.exe
        string downloadUrl = provider.DownloadUrl;
        string destinationDir = Path.GetDirectoryName(finalBinaryPath)!;

        if (File.Exists(finalBinaryPath)) return;

        using var client = new HttpClient();
        string tempZipFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tmp");
        string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            // 1. Download to Temp
            using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using (var fs = new FileStream(tempZipFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }
            }

            Directory.CreateDirectory(destinationDir);

            // Identify if it's a ZIP (Magic Bytes: PK)
            if (IsZipSignature(tempZipFile))
            {
                Directory.CreateDirectory(extractPath);
                ZipFile.ExtractToDirectory(tempZipFile, extractPath);

                // 3. Find the "Hero" Executable
                // Strategy: Find .exe files, prioritize the one that matches our expected filename
                var allFiles = Directory.GetFiles(extractPath, "*.*", SearchOption.AllDirectories);
                var exeFiles = allFiles.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();

                string? bestExe = exeFiles.FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(Path.GetFileName(finalBinaryPath), StringComparison.OrdinalIgnoreCase))
                    ?? exeFiles.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

                if (string.IsNullOrEmpty(bestExe))
                    throw new FileNotFoundException("No executable found in the archive.");

                // 4. Move the Hero Executable to its specific path
                if (File.Exists(finalBinaryPath)) File.Delete(finalBinaryPath);
                File.Move(bestExe, finalBinaryPath);

                // 5. Move everything else to the destination directory
                // This includes subdirectories and supporting files (DLLs, etc.)
                MoveDirectoryContents(extractPath, destinationDir, finalBinaryPath);
            }
            else
            {
                // Direct EXE download
                if (File.Exists(finalBinaryPath)) File.Delete(finalBinaryPath);
                File.Move(tempZipFile, finalBinaryPath);
            }
        }
        finally
        {
            // Cleanup temp artifacts
            if (File.Exists(tempZipFile)) File.Delete(tempZipFile);
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
        }
    }

    private void MoveDirectoryContents(string sourceDir, string destDir, string excludedFile)
    {
        // Get all top-level entries in the extracted folder
        foreach (var entry in Directory.GetFileSystemEntries(sourceDir))
        {
            string fileName = Path.GetFileName(entry);
            string destPath = Path.Combine(destDir, fileName);

            // Skip the file we already moved to finalBinaryPath
            if (string.Equals(entry, excludedFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(destPath, excludedFile, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(entry))
            {
                // Move subdirectories (Note: Directory.Move fails if destination exists)
                if (Directory.Exists(destPath)) Directory.Delete(destPath, true);
                Directory.Move(entry, destPath);
            }
            else
            {
                // Move files
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(entry, destPath);
            }
        }
    }

    private bool IsZipSignature(string filePath)
    {
        byte[] buffer = new byte[2];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            if (fs.Length < 2) return false;
            fs.Read(buffer, 0, 2);
        }
        return buffer[0] == 0x50 && buffer[1] == 0x4B; // PK
    }
}