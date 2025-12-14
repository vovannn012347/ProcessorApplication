using System.Collections.Concurrent;

using Common.Interfaces.EventBus;

namespace Common.Models;
public class ModuleDependency 
{
    public string ModuleId { get; set; }
    public Version? MinVersion { get; set; }
    public Version? MaxVersion { get; set; }

}