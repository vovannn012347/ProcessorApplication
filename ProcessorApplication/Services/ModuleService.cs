using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Identity;

using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services;

public class ModuleService : IModuleService
{

    private readonly IEnumerable<IModule> _modules;

    public ModuleService(
        IEnumerable<IModule> modules)
    {
        _modules = modules;
    }

    // Gets the list of modules for the TOP NAVBAR
    public IEnumerable<ModuleInfoViewModel> GetModuleInfo()
    {
        return _modules.Select(m => new ModuleInfoViewModel
        {
            ModuleId = m.ModuleId,
            Name = m.Name,
            Roles = m.GetRequiredRoles().ToArray()
        });
    }

    // Gets the sidebar menu for a specific module
    public List<MenuItemViewModel> GetMenuItems(string moduleId, IServiceProvider services)
    {
        var module = _modules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
        // Pass the container down to the module
        return module?.GetMenuItems(services) ?? new List<MenuItemViewModel>();
    }
}