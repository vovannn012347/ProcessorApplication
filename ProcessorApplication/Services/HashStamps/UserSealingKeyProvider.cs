using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services.HashStamps;

public sealed class SealingKey
{
    /// <summary>32-byte key for sealing chain entries.</summary>
    public byte[] Key { get; init; } = Array.Empty<byte>();

    /// <summary>Server stamp (key period) the key belongs to; stored with the entry so it can be recomputed.</summary>
    public int StampId { get; init; }

    /// <summary>True if the owner is a registered user (key bound to their personal key); false for system/unknown owners.</summary>
    public bool IsUserBound { get; init; }
}

/// <summary>
/// Keys for sealing per-user hash chains (e.g. processing results). Nothing is stored:
/// key = HMAC(server period key of the entry's time, purpose | user personal key).
/// Needs the node root key (for the period key) and the user's personal key (unlocked with the server key,
/// so the user doesn't have to be logged in). Anyone without both cannot produce or check a seal.
/// </summary>
public interface IUserSealingKeyProvider
{
    /// <param name="owner">User id or user name the chain belongs to.</param>
    /// <param name="purpose">Separates different chains (e.g. "processing-results").</param>
    /// <param name="at">Time of the entry: selects the server key period.</param>
    Task<SealingKey> GetSealingKeyAsync(string owner, string purpose, DateTime at, CancellationToken ct = default);

    /// <summary>Same key again for verification, using the stamp recorded with the entry.</summary>
    Task<SealingKey> GetSealingKeyForStampAsync(string owner, string purpose, int stampId, CancellationToken ct = default);
}

public sealed class UserSealingKeyProvider : IUserSealingKeyProvider
{
    private readonly AppDbContext _db;
    private readonly IHashStampService _stamps;

    public UserSealingKeyProvider(AppDbContext db, IHashStampService stamps)
    {
        _db = db;
        _stamps = stamps;
    }

    public async Task<SealingKey> GetSealingKeyAsync(string owner, string purpose, DateTime at, CancellationToken ct = default)
    {
        var stamp = await _stamps.GetHashByTimeAsync(at);
        return await BuildAsync(owner, purpose, stamp.Id, stamp.MasterKey, ct);
    }

    public async Task<SealingKey> GetSealingKeyForStampAsync(string owner, string purpose, int stampId, CancellationToken ct = default)
    {
        var stamp = await _stamps.GetByIdAsync(stampId)
                    ?? throw new CryptographicException($"Server key stamp {stampId} not found.");
        return await BuildAsync(owner, purpose, stamp.Id, stamp.MasterKey, ct);
    }

    private async Task<SealingKey> BuildAsync(string owner, string purpose, int stampId, string periodKey, CancellationToken ct)
    {
        string? personal = await UnlockPersonalKeyAsync(owner, ct);
        string material = personal != null
            ? $"chain|{purpose}|user|{personal}"
            : $"chain|{purpose}|owner|{owner}";

        byte[] key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(periodKey), Encoding.UTF8.GetBytes(material));
        return new SealingKey { Key = key, StampId = stampId, IsUserBound = personal != null };
    }

    /// <summary>The user's personal key, unlocked with the server key of the user's creation time.</summary>
    private async Task<string?> UnlockPersonalKeyAsync(string owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner)) return null;

        string normalized = owner.ToUpperInvariant();
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == owner || u.NormalizedUserName == normalized || u.UserName == owner, ct);
        if (user == null || string.IsNullOrEmpty(user.ServerEncryptedHashKey)) return null;

        var creationStamp = await _stamps.GetHashByTimeAsync(user.CreateDateTime);
        string personal = SecurityHelperUtil.DecryptData(user.ServerEncryptedHashKey, creationStamp.MasterKey);
        return string.IsNullOrEmpty(personal) ? null : personal;
    }
}
