

using Common.Interfaces.Menu;

namespace ProcessorApplication.Infrastructure;
public interface IModuleService
{
    IEnumerable<ModuleInfoViewModel> GetModuleInfo();
    List<MenuItemViewModel> GetMenuItems(string moduleId, IServiceProvider services);
}

