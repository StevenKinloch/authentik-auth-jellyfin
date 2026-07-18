using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AuthentikAuth.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AuthentikAuth.Api
{
    /// <summary>
    /// Handles the Authentik OIDC authorization-code + PKCE flow.
    /// Endpoints:
    ///   GET /Authentik/Start     - begins login, redirects to Authentik
    ///   GET /Authentik/Callback  - receives the code, exchanges it, signs the user in
    /// </summary>
    [ApiController]
    [Route("Authentik")]
    public class AuthentikController : ControllerBase
    {
        // In-memory store for pending PKCE flows, keyed by the OAuth "state" value.
        // Entries expire after 10 minutes; fine for a single-server homelab deployment.
        private static readonly ConcurrentDictionary<string, PendingLogin> PendingLogins = new();

        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<AuthentikController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public AuthentikController(
            IUserManager userManager,
            ISessionManager sessionManager,
            ILogger<AuthentikController> logger,
            IHttpClientFactory httpClientFactory)
        {
            _userManager = userManager;
            _sessionManager = sessionManager;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        private PluginConfiguration Config => Plugin.Instance!.Configuration;

        [HttpGet("AutoRedirectStatus")]
        public IActionResult AutoRedirectStatus()
        {
            return Content(
                JsonSerializer.Serialize(new { enabled = Config.AutoRedirectToSso }),
                "application/json");
        }

        [HttpGet("Logout")]
        public IActionResult Logout()
        {
            var config = Config;
            if (string.IsNullOrWhiteSpace(config.AuthentikBaseUrl) || string.IsNullOrWhiteSpace(config.JellyfinPublicUrl))
            {
                // Not configured - just send them to a plain, non-looping login page.
                return Redirect("/web/index.html?manual=1");
            }

            // RP-initiated logout (OpenID Connect). ?manual=1 stops our own auto-redirect
            // script from immediately bouncing back to Authentik once we land here -
            // otherwise, if Authentik's session is still alive for any reason, the user
            // would get silently logged straight back in, defeating the point of logging out.
            // Lowercased defensively - Authentik slugs are case-sensitive in the URL, and
            // it's an easy typo to make in the settings field (e.g. "Jellyfin" instead of
            // "jellyfin"), so this avoids a silent 404 on logout regardless of how it was entered.
            var postLogoutRedirect = $"{config.JellyfinPublicUrl}/web/index.html?manual=1";
            var logoutUrl =
                $"{config.AuthentikBaseUrl}/application/o/{Uri.EscapeDataString(config.ApplicationSlug.ToLowerInvariant())}/end-session/" +
                $"?post_logout_redirect_uri={Uri.EscapeDataString(postLogoutRedirect)}";

            return Redirect(logoutUrl);
        }

        [HttpGet("Start")]
        public IActionResult Start()
        {
            var config = Config;
            if (string.IsNullOrWhiteSpace(config.AuthentikBaseUrl) ||
                string.IsNullOrWhiteSpace(config.ClientId) ||
                string.IsNullOrWhiteSpace(config.JellyfinPublicUrl))
            {
                return BadRequest("Authentik Auth plugin is not fully configured yet. " +
                    "Set the base URL, client ID, and Jellyfin public URL in the plugin settings.");
            }

            var state = RandomToken(24);
            var codeVerifier = RandomToken(64);
            var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

            PendingLogins[state] = new PendingLogin
            {
                CodeVerifier = codeVerifier,
                CreatedUtc = DateTime.UtcNow
            };
            CleanupExpired();

            var redirectUri = $"{config.JellyfinPublicUrl}/Authentik/Callback";
            var authorizeUrl =
                $"{config.AuthentikBaseUrl}/application/o/authorize/" +
                $"?client_id={Uri.EscapeDataString(config.ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString("openid profile email groups")}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                $"&code_challenge_method=S256";

            return Redirect(authorizeUrl);
        }

        [HttpGet("Callback")]
        public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
        {
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Authentik returned an error: {Error}", error);
                return Content($"Authentik sign-in failed: {error}", "text/plain");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !PendingLogins.TryRemove(state, out var pending))
            {
                return BadRequest("Missing or expired login state. Please try signing in again.");
            }

            var config = Config;
            var redirectUri = $"{config.JellyfinPublicUrl}/Authentik/Callback";

            using var httpClient = _httpClientFactory.CreateClient();
            if (config.DoNotValidateEndpoints)
            {
                _logger.LogWarning("DoNotValidateEndpoints is enabled - TLS/discovery validation is relaxed.");
            }

            // Exchange the authorization code for tokens.
            var tokenResponse = await httpClient.PostAsync(
                $"{config.AuthentikBaseUrl}/application/o/token/",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = config.ClientId,
                    ["client_secret"] = config.ClientSecret,
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["code_verifier"] = pending.CodeVerifier
                })).ConfigureAwait(false);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                var body = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("Token exchange with Authentik failed: {Status} {Body}", tokenResponse.StatusCode, body);
                return Content("Token exchange with Authentik failed. Check the Jellyfin log for details.", "text/plain");
            }

            using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();

            if (string.IsNullOrEmpty(accessToken))
            {
                return Content("Authentik did not return an access token.", "text/plain");
            }

            // Use the userinfo endpoint rather than parsing the ID token JWT ourselves -
            // simpler, and Authentik signs userinfo responses with the same trusted claims.
            var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, $"{config.AuthentikBaseUrl}/application/o/userinfo/");
            userInfoRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            var userInfoResponse = await httpClient.SendAsync(userInfoRequest).ConfigureAwait(false);

            if (!userInfoResponse.IsSuccessStatusCode)
            {
                var body = await userInfoResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("Userinfo request failed: {Status} {Body}", userInfoResponse.StatusCode, body);
                return Content("Could not retrieve user info from Authentik.", "text/plain");
            }

            using var userInfoDoc = JsonDocument.Parse(await userInfoResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = userInfoDoc.RootElement;

            var username = root.TryGetProperty("preferred_username", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(username))
            {
                return Content("Authentik did not return a preferred_username claim.", "text/plain");
            }

            var groups = new List<string>();
            if (root.TryGetProperty("groups", out var groupsElement) && groupsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in groupsElement.EnumerateArray())
                {
                    var name = g.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        groups.Add(name);
                    }
                }
            }

            var allowedGroups = SplitList(config.AllowedGroups);
            var adminGroups = SplitList(config.AdminGroups);

            var isAllowed = allowedGroups.Count == 0 || groups.Any(g => allowedGroups.Contains(g, StringComparer.OrdinalIgnoreCase));
            if (!isAllowed)
            {
                _logger.LogWarning(
                    "User {Username} is not in any allowed group. Their groups: [{Groups}]. Allowed: [{Allowed}]",
                    username, string.Join(", ", groups), string.Join(", ", allowedGroups));
                return Content("You are signed in to Authentik, but you are not in a group permitted to access Jellyfin.", "text/plain");
            }

            var isAdmin = groups.Any(g => adminGroups.Contains(g, StringComparer.OrdinalIgnoreCase));

            var user = _userManager.GetUserByName(username);
            if (user is null)
            {
                if (!config.CreateUsersAutomatically)
                {
                    return Content(
                        $"No local Jellyfin account named '{username}' exists, and automatic user creation is disabled.",
                        "text/plain");
                }

                _logger.LogInformation("Creating new Jellyfin user '{Username}' from Authentik login.", username);
                user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            }

            // Keep admin status in sync with current Authentik group membership.
            if (user.HasPermission(PermissionKind.IsAdministrator) != isAdmin)
            {
                user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }

            // Establish a real Jellyfin session for this already-verified identity.
            var authRequest = new AuthenticationRequest
            {
                UserId = user.Id,
                Username = username,
                App = "Authentik SSO",
                AppVersion = "1.0.0",
                DeviceId = $"authentik-{state}",
                DeviceName = "Authentik SSO",
                RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            };

            var authResult = await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);

            // jellyfin-web matches a stored credential entry against the server it just
            // loaded using the server's unique Id - without it, the client silently
            // ignores whatever we put in localStorage and falls back to the login page.
            // UserDto.ServerId gives us that same Id.
            var serverId = authResult.User.ServerId;
            var userJson = JsonSerializer.Serialize(new
            {
                authResult.User.Id,
                authResult.User.Name,
                authResult.User.ServerId,
                EnableAutoLogin = true
            });

            var credentialsJson = JsonSerializer.Serialize(new
            {
                Servers = new[]
                {
                    new
                    {
                        Id = serverId,
                        ManualAddress = config.JellyfinPublicUrl,
                        LastConnectionMode = 2,
                        AccessToken = authResult.AccessToken,
                        UserId = authResult.User.Id,
                        DateLastAccessed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }
                }
            });

            var localStorageUserKey = $"user-{authResult.User.Id}-{serverId}";

            var html = $@"
<!DOCTYPE html>
<html><body>
<script>
  localStorage.setItem('jellyfin_credentials', {JsonSerializer.Serialize(credentialsJson)});
  localStorage.setItem({JsonSerializer.Serialize(localStorageUserKey)}, {JsonSerializer.Serialize(userJson)});
  localStorage.setItem('enableAutoLogin', 'true');
  window.location.replace('/web/index.html');
</script>
Signed in - redirecting...
</body></html>";

            return Content(html, "text/html");
        }

        private static List<string> SplitList(string value)
        {
            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        private static string RandomToken(int bytes)
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static void CleanupExpired()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var key in PendingLogins.Where(kv => kv.Value.CreatedUtc < cutoff).Select(kv => kv.Key).ToList())
            {
                PendingLogins.TryRemove(key, out _);
            }
        }

        private class PendingLogin
        {
            public required string CodeVerifier { get; set; }
            public DateTime CreatedUtc { get; set; }
        }
    }
}
