using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AuthentikAuth.Configuration
{
    /// <summary>
    /// Plugin configuration. All fields are plain strings/bools to avoid the
    /// array-serialization issues seen with other SSO plugins.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Base URL of your Authentik instance, e.g. https://authentik.kinloch.org.uk
        /// No trailing slash.
        /// </summary>
        public string AuthentikBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// The application "slug" configured in Authentik for this provider,
        /// e.g. "jellyfin". Used to build the /application/o/{slug}/ discovery path.
        /// </summary>
        public string ApplicationSlug { get; set; } = "jellyfin";

        /// <summary>
        /// OAuth2 client ID from the Authentik provider.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// OAuth2 client secret from the Authentik provider.
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>
        /// The public base URL Jellyfin is reachable at, e.g. https://jellyfin.kinloch.org.uk
        /// Used to build the redirect_uri explicitly rather than inferring scheme from the
        /// incoming request (avoids http/https mismatches behind Cloudflare Tunnel).
        /// </summary>
        public string JellyfinPublicUrl { get; set; } = string.Empty;

        /// <summary>
        /// Comma-separated list of Authentik group names whose members are allowed to log in.
        /// Leave blank to allow any authenticated Authentik user.
        /// </summary>
        public string AllowedGroups { get; set; } = string.Empty;

        /// <summary>
        /// Comma-separated list of Authentik group names whose members should be made
        /// Jellyfin administrators.
        /// </summary>
        public string AdminGroups { get; set; } = string.Empty;

        /// <summary>
        /// If true, a Jellyfin user account will be created automatically the first time
        /// someone in an allowed group signs in. If false, only users who already exist
        /// locally (matched by username) can sign in via SSO.
        /// </summary>
        public bool CreateUsersAutomatically { get; set; } = true;

        /// <summary>
        /// Skip TLS certificate / discovery-document validation. Only enable temporarily
        /// for debugging internal networking issues.
        /// </summary>
        public bool DoNotValidateEndpoints { get; set; } = false;

        /// <summary>
        /// If true, visiting the normal Jellyfin web login page automatically redirects
        /// to /Authentik/Start instead of showing the password form. Add ?manual=1 to
        /// the URL to bypass this and reach the password form (e.g. for a local-only
        /// admin account, or if Authentik itself is unreachable).
        /// </summary>
        public bool AutoRedirectToSso { get; set; } = false;
    }
}
