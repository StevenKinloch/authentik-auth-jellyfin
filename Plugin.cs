using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AuthentikAuth.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AuthentikAuth
{
    /// <summary>
    /// Authentik SSO plugin entry point.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Fixed GUID for this plugin. Do not change once installed anywhere,
        /// as it's how Jellyfin identifies the plugin's config file on disk.
        /// </summary>
        public static readonly Guid PluginGuid = new Guid("a4e1c8b2-6f3d-4b8a-9c2e-2f1d7e5a9b31");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Authentik Auth";

        public override Guid Id => PluginGuid;

        public override string Description => "Sign in to Jellyfin using Authentik as an OIDC identity provider.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
            };
        }
    }
}
