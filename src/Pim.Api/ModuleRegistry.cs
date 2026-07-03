using System.Reflection;
using Pim.Core.Modules;
using Serilog;

namespace Pim.Api;

public class ModuleRegistry
{
    private readonly List<IModule> _modules = new();
    private readonly HashSet<string> _loadedTypeNames = new();
    private readonly HashSet<string> _loadedModuleNames = new();

    public IReadOnlyList<IModule> Modules => _modules;

    public void DiscoverModules(IServiceCollection services, IConfiguration configuration)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var moduleFiles = Directory.GetFiles(baseDir, "Pim.Module.*.dll");

        foreach (var assemblyPath in moduleFiles)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load module assembly: {Path}", assemblyPath);
                continue;
            }

            List<Type> moduleTypes;
            try
            {
                moduleTypes = assembly.GetTypes()
                    .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Log.Warning(ex, "Failed to load types from module assembly: {Path}", assemblyPath);
                continue;
            }

            foreach (var type in moduleTypes)
            {
                // Dedup by type full name (same DLL loaded twice)
                if (!_loadedTypeNames.Add(type.FullName!))
                    continue;

                IModule module;
                try
                {
                    module = (IModule)Activator.CreateInstance(type)!;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to create module instance for {Type}", type.FullName);
                    continue;
                }

                // Dedup by module name (different versions, same name)
                if (!_loadedModuleNames.Add(module.Name))
                {
                    Log.Warning("Duplicate module name '{Name}' skipped; type={Type}", module.Name, type.FullName);
                    continue;
                }

                _modules.Add(module);

                try
                {
                    module.RegisterServices(services, configuration);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Module '{Name}' service registration failed", module.Name);
                }
            }
        }
    }

    public void MapAllEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var module in _modules)
        {
            module.MapEndpoints(endpoints);
        }
    }

    public async Task InitializeAllAsync(IServiceProvider serviceProvider)
    {
        foreach (var module in _modules)
        {
            await module.InitializeAsync(serviceProvider);
        }
    }
}
