using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StrmAudio
{
    /// <summary>
    /// STRM Audio plugin: makes .strm files in music libraries work like in Emby,
    /// by using the target URL from the .strm file as the playback source.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "STRM Audio";

        public override Guid Id => Guid.Parse("a0e614d9-608e-4d7a-94de-8f1e3328a1ca");

        public override string Description =>
            "Makes .strm files playable for audio (music/radio), like in Emby. The server fetches the stream and forwards it to clients.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "StrmAudio",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }

    /// <summary>
    /// Plugin configuration. Editable on the plugin's settings page in the
    /// dashboard; changes apply immediately to new playback sessions.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether the resilient stream proxy with
        /// automatic reconnect is used. When false, the core fetches the target
        /// URL directly without reconnect support.
        /// </summary>
        public bool EnableReconnect { get; set; } = true;

        /// <summary>
        /// Gets or sets how long (in seconds) the proxy keeps trying to reconnect
        /// to a dropped upstream stream before giving up and closing the client
        /// connection.
        /// </summary>
        public int ReconnectWindowSeconds { get; set; } = 300;

        /// <summary>
        /// Gets or sets the timeout (in seconds) for connecting to the upstream
        /// stream and receiving response headers.
        /// </summary>
        public int UpstreamConnectTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Gets or sets the maximum time (in seconds) to wait for new data on an
        /// open upstream connection before treating it as stalled and reconnecting.
        /// </summary>
        public int ReadTimeoutSeconds { get; set; } = 20;
    }
}
