namespace ScriptProvisioning.Configuration;

public class ScriptProvisioningSettings
{
    /// <summary>
    /// Module data folder: published packages (published/) and the publish/install records (*.json).
    /// Relative paths are resolved against the application folder.
    /// Default: Module_Data/ScriptProvisioning
    /// </summary>
    public string DataPath { get; set; } = "Module_Data/ScriptProvisioning";

    /// <summary>
    /// Publish new packages as hidden: they are seeded to nodes that already know the hash,
    /// but not listed in the catalogue or announced to trackers. Default: false (listed).
    /// </summary>
    public bool PublishHidden { get; set; } = false;

    /// <summary>
    /// Give up on a package download after this many minutes (paused downloads count too). Default: 240.
    /// </summary>
    public int DownloadTimeoutMinutes { get; set; } = 240;

    /// <summary>How many finished install tasks are kept for the page. Default: 50.</summary>
    public int KeepFinishedTasks { get; set; } = 50;
}
