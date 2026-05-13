using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using ProcessingModule.Configuration;

namespace ProcessingModule.Models.Views;

public class ProcessorSettingsViewModel
{
    public ProcessorSettings General { get; set; }
    public PythonProcessingSettings Python { get; set; }
    //public NoneSandboxSettings None { get; set; }
    public OsSandboxSettings OsSandbox { get; set; }
    public DockerSandboxSettings DockerSandbox { get; set; }
}
