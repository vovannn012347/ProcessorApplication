using ProcessingModule.Database.Models;

namespace ProcessingModule.Utils;

public static class ProcessingPathUtility
{
    /// <summary>
    /// Initializes the physical directory structure for a job and its sub-jobs.
    /// Format: [ResultsOutputPath] / [ParentJobId] / [SubJobId]
    /// </summary>
    public static void InitializeJobDirectories(OrchestratedTask task, string rootOutputPath)
    {
        // Set the root path for the parent task
        task.PhysicalPathRoot = Path.Combine(rootOutputPath, task.Id.ToString());

        foreach (var subJob in task.SubJobs)
        {
            // Set the subdirectory name to the sub-job's unique ID
            subJob.StepDirectoryName = subJob.Id.ToString();

            var fullSubJobPath = Path.Combine(task.PhysicalPathRoot, subJob.StepDirectoryName);
            if (!Directory.Exists(fullSubJobPath))
            {
                Directory.CreateDirectory(fullSubJobPath);
            }
        }
    }

    /// <summary>
    /// Converts a physical file path into a secure web token.
    /// Replaces directory separators with colons for URL compatibility.
    /// </summary>
    public static string CreateFileToken(Guid parentId, Guid subId, string relativeFilePath)
    {
        // Sanitize path by replacing separators with colons
        var sanitizedPath = relativeFilePath
            .Replace('\\', ':')
            .Replace('/', ':');

        return $"{parentId}:{subId}:{sanitizedPath}";
    }
}