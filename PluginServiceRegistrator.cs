using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AuthentikAuth
{
    /// <summary>
    /// Registers WebIndexPatcher as a hosted background service. Jellyfin locates this
    /// class automatically at server startup by scanning the plugin assembly for
    /// IPluginServiceRegistrator implementations.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<WebIndexPatcher>();
        }
    }
}
