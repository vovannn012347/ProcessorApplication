namespace ProcessingModule.Infrastructure;

public static class InOutType
{
    public const string ScriptFile = "script_file";
    public const string SourceFile = "source_file";
    public const string SourceDir = "source_directory";
    public const string InOutFile = "file";
    public const string InOutFileMultiple = "multi_file";
    public const string InOutDir = "folder";
    public const string Boolean = "boolean";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string String = "string";

    public static string[] SourceInput = new[] { ScriptFile, SourceFile, SourceDir };
    public static string[] ScalarInput = new[] { Boolean, Integer, Decimal, String };
    public static string[] FileInput = new[] { InOutFile, InOutFileMultiple, InOutDir };
}

public static class TaskStatusKeyword
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string ServerError = "ServerError";
    public const string Paused = "Paused";
    public const string Stopped = "Stopped";
}

public static class FileExtensions
{
    public const string Journal = "orchestration";
    public static readonly string[] ImageFormats = { ".jpg", ".jpeg", ".png" };
}

public static class MicsConstants
{
    public const string FileInputsDirectory = "inputs";
    public const string JournalsDirectory = "journals";
    public const string LocalizationDirectory = "journals";

    public const string ScriptManifestFile = "script_manifest.json";
    public const string DirectInputFile = "direct_input.json";
    public const string DirectOutputFile = "direct_output.json";
    public const string FileOutputFile = "file_output.json";
    public const string ScriptSummaryFile = "script_summary.json";

    public const string OrchestrationDirectory = "processing_manifests";
    public const string OrchestrationManifestFile = "orchestration_manifest.json";
}