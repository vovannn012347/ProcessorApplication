using Newtonsoft.Json;

namespace ProcessorModule.Models;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

public class ScriptManifest
{
    [JsonProperty("script_id")]
    public string ScriptId { get; set; }

    [JsonProperty("script_label")] 
    public string ScriptLabel { get; set; }

    [JsonProperty("entry_point")] 
    public string EntryPoint { get; set; }

    [JsonProperty("version")] 
    public string ScriptVersion { get; set; }

    [JsonProperty("processor_version")]
    public string MinProcessorVersion { get; set; }

    [JsonProperty("artifact_hash")]
    public string ScriptHash { get; set; }


    [JsonProperty("inputs_mapping")] 
    public List<InputMapping> Inputs { get; set; } = new();
    [JsonProperty("outputs_mapping")] 
    public List<OutputMapping> Outputs { get; set; } = new();

    //inputs/outputs info used in ui labeling or presenting 
    [JsonProperty("orchestrator_input_mapping")]
    public List<OrchestrationInputMapping> OrchestrationInputs { get; set; } = new();

    [JsonProperty("orchestrator_output_mapping")]
    public List<OrchestrationOutputMapping> OrchestrationOutputs { get; set; } = new();

    //localization files
    [JsonProperty("localization")]
    public Dictionary<string, string> Localization { get; set; } = new();
}

public class InputMapping
{
    [JsonProperty("label")] 
    public string Label { get; set; }
    // "script_file", "source_file", "folder", "file"
    // "string", "integer", "decimal", "boolean"
    [JsonProperty("type")] 
    public string Type { get; set; } 
    [JsonProperty("disk_path")] 
    public string DiskPath { get; set; }
    [JsonProperty("optional")]
    public bool Optional { get; set; }
}

public class OutputMapping
{
    [JsonProperty("label")] 
    public string Label { get; set; }
    [JsonProperty("type")] 
    public string Type { get; set; }
    [JsonProperty("disk_path")] 
    public string DiskPath { get; set; }
}

public class OrchestrationInputMapping
{
    // "script_file", "source_file", "folder", "file"
    // "string", "integer", "decimal", "boolean"
    [JsonProperty("type")]
    public string Type { get; set; }
    //label param name
    [JsonProperty("label_param")]
    public string LabelParam { get; set; }
    [JsonProperty("text_label")]
    public string LocalizationLabel { get; set; }
}

public class OrchestrationOutputMapping
{
    [JsonProperty("type")]
    public string Type { get; set; }
    [JsonProperty("label_param")]
    public string LabelParam { get; set; }
    [JsonProperty("text_label")]
    public string LocalizationLabel { get; set; }
}


//script manifest
public class ProcessingManifest
{
    [JsonProperty("run-script")]
    public string RunScript { get; set; } // ScriptId

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("result_hash")]
    public string ResultHash { get; set; }


    [JsonProperty("folder_base")] //path for direct outputs
    public string FolderBase { get; set; }


    [JsonProperty("inputs_base")] // absolute path for folder variable
    public Dictionary<string, string> InputsFoldersBase { get; set; } = new();

    [JsonProperty("outputs_base")] // absolute path for folder variable
    public Dictionary<string, string> OutputFoldersBase { get; set; } = new();
}

public class OrchestrationManifest
{
    [JsonProperty("job_id")]
    public Guid JobId { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("steps")]
    public List<OrchestrationStep> Steps { get; set; } = new();
}

public class OrchestrationStep
{
    [JsonProperty("sequence")]
    public int Sequence { get; set; }

    [JsonProperty("processing_id")]
    public Guid ProcessingId { get; set; }

    [JsonProperty("script_id")]
    public string ScriptId { get; set; }

    [JsonProperty("inputs")]
    public Dictionary<string, string> Inputs { get; set; } = new();

    [JsonProperty("outputs")]
    public Dictionary<string, string> Outputs { get; set; } = new();

    // Optional: for dependency logic
    [JsonProperty("previous")]
    public List<Guid> PreviousProcessingIds { get; set; } = new();
}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
