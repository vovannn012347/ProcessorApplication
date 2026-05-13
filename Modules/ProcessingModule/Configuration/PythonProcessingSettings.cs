using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;


namespace ProcessingModule.Configuration;

public class PythonProcessingSettings
{
    public const string SectionName = "PythonProcessorSettings";

    // relative or absolute path to venv/bin/python/executor
    [Required]
    [Display(Name = "Python Executable Path")]
    public string PythonExecutablePath { get; set; } = ""; 

    [Display(Name = "Log to Standard Output")]
    public bool LogStdout { get; set; } = true;
}