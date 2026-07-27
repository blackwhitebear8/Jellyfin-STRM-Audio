using System.Linq;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmAudio
{
    /// <summary>
    /// Registers the <see cref="StrmAudioMediaSourceManager"/> as a decorator over
    /// Jellyfin's default IMediaSourceManager.
    ///
    /// Jellyfin invokes plugin registrations AFTER its own core registrations
    /// (ApplicationHost: RegisterServices first, then _pluginManager.RegisterServices).
    /// With Microsoft.Extensions.DependencyInjection the last registration "wins" when
    /// resolving a single IMediaSourceManager - so the entire server (including
    /// BaseItem.MediaSourceManager) receives our decorator.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Look up the existing core registration of IMediaSourceManager so we can
            // register the concrete implementation type (Emby.Server.Implementations.
            // Library.MediaSourceManager) without a compile-time reference.
            var descriptor = serviceCollection.LastOrDefault(d =>
                d.ServiceType == typeof(IMediaSourceManager)
                && d.ImplementationType is not null);

            if (descriptor?.ImplementationType is null)
            {
                // Unexpected server version: do nothing, the server keeps working.
                return;
            }

            var innerType = descriptor.ImplementationType;

            // Remove the original interface registration to avoid duplicate instances.
            serviceCollection.Remove(descriptor);

            // Register the concrete core implementation as a singleton on its own type
            // (DI can construct it normally, all dependencies are available).
            serviceCollection.AddSingleton(innerType);

            // Register our decorator as THE IMediaSourceManager.
            serviceCollection.AddSingleton<IMediaSourceManager>(sp =>
                new StrmAudioMediaSourceManager(
                    (IMediaSourceManager)sp.GetRequiredService(innerType),
                    sp.GetRequiredService<MediaBrowser.Model.IO.IFileSystem>(),
                    sp.GetRequiredService<ILogger<StrmAudioMediaSourceManager>>()));

            // Install the reconnect handler on Jellyfin's default named HttpClient.
            // Named client configurations are additive, so this appends to the
            // core's own registration and intercepts the plugin's sentinel URLs
            // in-process before any network activity happens.
            serviceCollection.AddTransient<StrmAudioReconnectHandler>();
            serviceCollection
                .AddHttpClient(NamedClient.Default)
                .AddHttpMessageHandler<StrmAudioReconnectHandler>();
        }
    }
}
