using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

/// <summary>
/// Defines the contract for managing a secure tunnel process.
/// </summary>
public interface ITunnelProvider
{
    /// <summary>
    /// The type of tunnel provider (Cloudflare, Ngrok, etc.).
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// Starts the tunnel process and returns the public URL once established.
    /// </summary>
    /// <param name="localPort">The local Kestrel port to expose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The public tunnel URL (e.g., https://xyz.trycloudflare.com).</returns>
    Task<string> StartTunnelAsync(int localPort, string scheme = "https", CancellationToken ct = default);

    /// <summary>
    /// Gracefully shuts down the tunnel process.
    /// </summary>
    Task StopTunnelAsync();

    /// <summary>
    /// Returns whether tunnel is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the current tunnel start tiem if it is running.
    /// </summary>
    DateTime? LastStartTime { get; }

    /// <summary>
    /// Gets the current tunnel URL if the process is running.
    /// </summary>
    string? CurrentUrl { get; }

    /// <summary>
    /// Gets the current path to binary
    /// </summary>
    string CurrentBinaryPath { get; }
    /// <summary>
    /// Gets the current path to binary
    /// </summary>
    string DownloadUrl { get; }
}
