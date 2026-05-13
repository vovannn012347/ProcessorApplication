using System.Diagnostics;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Bootstrappers;

public class LinuxBinaryBootstrapper : IBinaryBootstrapper
{
    private readonly ITunnelSelector _selector;

    public LinuxBinaryBootstrapper(ITunnelSelector selector)
    {
        _selector = selector;
    }

    public async Task EnsureBinariesAsync(CancellationToken ct = default)
    {
        var provider = _selector.GetActiveProvider();
        string binaryPath = provider.CurrentBinaryPath;
        string downloadUrl = provider.DownloadUrl;

        if (File.Exists(binaryPath)) return;

        using var client = new HttpClient();

        // 1. Reachability Check
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using var responseHeaders = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!responseHeaders.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Binary source unreachable. HTTP {(int)responseHeaders.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Connectivity Error: Cannot reach {downloadUrl}.", ex);
        }

        // 2. Directory Preparation
        var directory = Path.GetDirectoryName(binaryPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 3. Streaming Download
        using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        using (var streamToReadFrom = await response.Content.ReadAsStreamAsync(ct))
        using (var streamToWriteTo = File.Open(binaryPath, FileMode.Create))
        {
            await streamToReadFrom.CopyToAsync(streamToWriteTo, ct);
        }

        // 4. Linux-Specific: Set Executable Permissions
        try
        {
            // We use the 'chmod' command which is standard on all Linux distros
            var processInfo = new ProcessStartInfo("chmod", $"+x {binaryPath}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var chmod = Process.Start(processInfo);
            if (chmod != null)
            {
                await chmod.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex)
        {
            throw new PlatformNotSupportedException($"Failed to grant execution rights to {binaryPath}. Manual 'chmod +x' may be required.", ex);
        }
    }
}