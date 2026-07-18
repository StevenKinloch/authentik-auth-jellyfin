using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AuthentikAuth
{
    /// <summary>
    /// On server startup, injects a small script into the web client's index.html that
    /// auto-redirects to /Authentik/Start when AutoRedirectToSso is enabled in the plugin
    /// config. Add ?manual=1 to the URL to reach the normal password form instead.
    ///
    /// This has to touch the static web files directly because Jellyfin doesn't expose a
    /// supported hook for altering the login page - same approach used by plugins like
    /// Jellyscrub and jellyfin-plugin-custom-javascript. It re-checks and re-patches on
    /// every server start; a Jellyfin web-client update overwrites index.html and removes
    /// the injected script until the server restarts again.
    ///
    /// If the Jellyfin process user doesn't own the web files (common in some Docker/
    /// package setups), this will fail with UnauthorizedAccessException - see the log
    /// warning below and the README for the permission fix.
    /// </summary>
    public class WebIndexPatcher : IHostedService
    {
        private const string MarkerStart = "<!-- AuthentikAuth:start -->";
        private const string MarkerEnd = "<!-- AuthentikAuth:end -->";

        private readonly IApplicationPaths _paths;
        private readonly ILogger<WebIndexPatcher> _logger;

        public WebIndexPatcher(IApplicationPaths paths, ILogger<WebIndexPatcher> logger)
        {
            _paths = paths;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var indexPath = Path.Combine(_paths.WebPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("AuthentikAuth: could not find index.html at {Path} to patch.", indexPath);
                    return Task.CompletedTask;
                }

                var content = File.ReadAllText(indexPath);

                // Strip any previous injection first so re-running (e.g. after a plugin
                // update) doesn't stack up duplicate copies.
                var startIdx = content.IndexOf(MarkerStart, StringComparison.Ordinal);
                var endIdx = content.IndexOf(MarkerEnd, StringComparison.Ordinal);
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    content = content.Remove(startIdx, (endIdx + MarkerEnd.Length) - startIdx);
                }

                var script = $@"{MarkerStart}
<script data-cfasync=""false"">
(function () {{
  var alreadyRedirecting = false;

  function checkAndMaybeRedirect() {{
    if (alreadyRedirecting) return;
    if (window.location.search.indexOf('manual=1') !== -1) {{
      return;
    }}
    try {{
      var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || 'null');
      var hasToken = creds && creds.Servers && creds.Servers.some(function (s) {{ return s && s.AccessToken; }});
      if (hasToken) {{
        return;
      }}
    }} catch (e) {{ /* malformed/missing credentials - proceed to redirect check */ }}
    fetch('/Authentik/AutoRedirectStatus').then(function (r) {{ return r.json(); }}).then(function (s) {{
      if (s && s.enabled) {{
        alreadyRedirecting = true;
        window.location.replace('/Authentik/Start');
      }}
    }}).catch(function () {{ /* Authentik unreachable - fall through to normal login */ }});
  }}

  // Detect an explicit Sign Out (Jellyfin's own /Sessions/Logout call) and route it
  // through our own logout, which also ends the Authentik session - otherwise, if
  // Authentik's session is still alive, checkAndMaybeRedirect() below would silently
  // log the user straight back in, making Sign Out appear to do nothing.
  var origFetch = window.fetch;
  window.fetch = function (input, init) {{
    var url = typeof input === 'string' ? input : (input && input.url) || '';
    if (url.indexOf('/Sessions/Logout') !== -1) {{
      alreadyRedirecting = true;
      return origFetch.apply(this, arguments).then(function (resp) {{
        window.location.replace('/Authentik/Logout');
        return resp;
      }});
    }}
    return origFetch.apply(this, arguments);
  }};

  checkAndMaybeRedirect();

  // Jellyfin's web client is a single-page app - landing back on the login screen (e.g.
  // after a session expires) happens via client-side routing, not a full page load, so
  // the one-shot check above won't see it. Re-run the check whenever the URL path
  // changes to the login view. (Explicit Sign Out is handled separately above and
  // bypasses this, via alreadyRedirecting.)
  var lastPath = window.location.pathname + window.location.hash;
  setInterval(function () {{
    var currentPath = window.location.pathname + window.location.hash;
    if (currentPath !== lastPath) {{
      lastPath = currentPath;
      if (currentPath.toLowerCase().indexOf('login') !== -1) {{
        checkAndMaybeRedirect();
      }}
    }}
  }}, 500);
}})();
</script>
{MarkerEnd}";

                var bodyCloseIdx = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyCloseIdx < 0)
                {
                    _logger.LogWarning("AuthentikAuth: could not find </body> in index.html, skipping patch.");
                    return Task.CompletedTask;
                }

                content = content.Insert(bodyCloseIdx, script + "\n");
                File.WriteAllText(indexPath, content);
                _logger.LogInformation("AuthentikAuth: patched index.html for auto-redirect support.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AuthentikAuth: no permission to write to index.html. If Jellyfin's web files are " +
                    "owned by a different user than the Jellyfin service account, auto-redirect won't " +
                    "work until that's fixed (see README).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuthentikAuth: failed to patch index.html.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
