using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using ProcessorModule.Configuration;

namespace ProcessorModule.Models.Views;

public class ProcessorSettingsViewModel
{
    public ProcessorSettings General { get; set; }
    public PythonProcessingSettings Python { get; set; }
    //public NoneSandboxSettings None { get; set; }
    public OsSandboxSettings OsSandbox { get; set; }
    public DockerSandboxSettings DockerSandbox { get; set; }
}
