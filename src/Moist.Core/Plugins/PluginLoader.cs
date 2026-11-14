using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Encoders;
using Moist.Core.Jobs;
using Moist.Core.Settings;

namespace Moist.Core.Plugins;

/// <summary>
/// Discovers and loads IPlugin implementations from .dll files in the plugins directory.
/// Plugins can register new encoders, processors, and other extensions.
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly List<IPlugin> _loadedPlugins = [];

    public IReadOnlyList<IPlugin> LoadedPlugins => _loadedPlugins;

    public static string GetPluginsDirectory()
    {
        return MoistPaths.Sub("plugins");
    }

    public PluginLoader(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PluginLoader>() ?? NullLogger<PluginLoader>.Instance;
    }

    /// <summary>
    /// Scan the plugins directory and load all assemblies containing IPlugin implementations.
    /// </summary>
    public void LoadPlugins(EncoderRegistry encoders, JobQueue jobQueue)
    {
        var pluginsDir = GetPluginsDirectory();
        if (!Directory.Exists(pluginsDir)) return;

        var dlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
        _logger.LogInformation("Scanning {Count} DLLs in plugins directory: {Dir}", dlls.Length, pluginsDir);

        foreach (var dllPath in dlls)
        {
            try
            {
                LoadPluginAssembly(dllPath, encoders, jobQueue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load plugin from {Path}", dllPath);
            }
        }

        _logger.LogInformation("Loaded {Count} plugin(s)", _loadedPlugins.Count);
    }

    private void LoadPluginAssembly(string dllPath, EncoderRegistry encoders, JobQueue jobQueue)
    {
        var assembly = Assembly.LoadFrom(dllPath);
        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract
                        && t.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in pluginTypes)
        {
            try
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                _logger.LogInformation("Loading plugin: {Name} v{Version} ({Id})",
                    plugin.Name, plugin.Version, plugin.Id);

                plugin.Register(encoders, jobQueue);
                _loadedPlugins.Add(plugin);
                _logger.LogInformation("Plugin registered successfully: {Name}", plugin.Name);
            }
            catch (Exception ex)
            {
                // Isolate a single plugin's construction/registration failure so
                // sibling plugins in the same assembly still load. Use type.FullName
                // since plugin.Name may be unreachable when the ctor itself threw.
                _logger.LogError(ex, "Plugin type {Type} failed to load", type.FullName);
            }
        }
    }
}
