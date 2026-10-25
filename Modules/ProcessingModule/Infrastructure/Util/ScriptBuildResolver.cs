using Microsoft.EntityFrameworkCore;

using ProcessingModule.Database;
using ProcessingModule.Database.Models;

namespace ProcessingModule.Infrastructure.Util;

/// <summary>
/// Finds the script build (library entry) a job step runs.
/// Steps store the build ID; steps created before builds were tracked only have script_id
/// (and maybe a version), so they fall back to the best match: same version if known,
/// available builds first, newest install first.
/// </summary>
public static class ScriptBuildResolver
{
    public static async Task<ScriptIndex?> ResolveAsync(ProcessorDbContext db, OrchestratedTaskSubJob subJob, CancellationToken ct = default)
    {
        if (subJob.ScriptIndexId.HasValue)
        {
            return await db.Scripts.FirstOrDefaultAsync(s => s.Id == subJob.ScriptIndexId.Value, ct);
        }

        var query = db.Scripts.Where(s => s.ScriptIdentifier == subJob.ScriptId);
        if (!string.IsNullOrEmpty(subJob.ScriptVersion))
        {
            query = query.Where(s => s.ScriptVersion == subJob.ScriptVersion);
        }

        return await query
            .OrderByDescending(s => s.IsAvailable)
            .ThenByDescending(s => s.CreatedTime)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>"label v1.2.0" for messages.</summary>
    public static string Describe(ScriptIndex build) =>
        $"{build.ScriptLabel ?? build.ScriptIdentifier} v{build.ScriptVersion} (build {build.Id})";

    /// <summary>Parses a comma-separated list of build IDs, keeping order and dropping duplicates and junk.</summary>
    public static List<int> ParseBuildIds(string? csv)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return result;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var id) && id > 0 && !result.Contains(id)) result.Add(id);
        }
        return result;
    }
}
