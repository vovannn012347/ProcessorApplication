using Common.Interfaces.Menu;

using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services;
public interface IModuleService
{
    IEnumerable<ModuleInfoViewModel> GetModuleInfo();
    List<MenuItemViewModel> GetMenuItems(string moduleId);
}

public class ModuleService : IModuleService
{
    // We will inject all IModule instances here
    private readonly IEnumerable<IModule> _modules;

    public ModuleService(IEnumerable<IModule> modules)
    {
        _modules = modules;
    }

    // Gets the list of modules for the TOP NAVBAR
    public IEnumerable<ModuleInfoViewModel> GetModuleInfo()
    {
        return _modules.Select(m => new ModuleInfoViewModel
        {
            ModuleId = m.ModuleId,
            Name = m.Name
        }).ToList();
    }

    // Gets the sidebar menu for a specific module
    public List<MenuItemViewModel> GetMenuItems(string moduleId)
    {
        var module = _modules.FirstOrDefault(m => m.ModuleId == moduleId);
        return module?.GetMenuItems() ?? new List<MenuItemViewModel>();
    }
}