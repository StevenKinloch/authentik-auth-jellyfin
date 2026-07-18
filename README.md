# Authentik Auth — a minimal Jellyfin SSO plugin

A small, single-purpose Jellyfin plugin that does OIDC authorization-code +
PKCE login against **Authentik only**, and nothing else. It exists as an
alternative to `9p4/jellyfin-plugin-sso` (now archived) which was hitting
config-persistence, case-sensitivity, and scope-array bugs.

## What it does

- `GET /Authentik/Start` — redirects the browser to Authentik's authorize
  endpoint with PKCE.
- `GET /Authentik/Callback` — exchanges the code for tokens, calls
  Authentik's `/application/o/userinfo/` endpoint to get claims (no JWT
  signature verification needed this way), checks group membership, and
  either links to an existing local Jellyfin user (matched by username) or
  creates a new one, then signs the browser in.
- Admin status is set based on `AdminGroups` membership and kept in sync on
  every login.

## Important: I could not compile or test this

I don't have the .NET SDK or NuGet access in the environment I wrote this
in, so **this has not been built**. Treat it as a strong starting point,
not a finished artifact. The specific things most likely to need a fix
once you try to build it:

1. **`ISessionManager.AuthenticateDirect(AuthenticationRequest)`** — I'm
   fairly confident this method exists in roughly this shape in Jellyfin
   10.9/10.10's `MediaBrowser.Controller.Session` namespace (it's the
   pattern other external-auth plugins use), but the exact method name or
   signature may differ slightly in 10.10.3 specifically. If the build
   fails here, open the Jellyfin server SDK reference or decompile
   `MediaBrowser.Controller.dll` from your install to find the current
   equivalent.
2. **`IUserManager.CreateUserAsync` / `GetUserByName` / `UpdateUserAsync`**
   — signatures (async vs sync, return types) can shift between minor
   versions. Same advice: check against your installed version's
   `MediaBrowser.Controller.dll`.
3. **`PermissionKind` namespace** — I've guessed `Jellyfin.Data.Enums`.
   If the build can't find it, search your Jellyfin install's DLLs for
   `PermissionKind` to find the right `using`.
4. **NuGet package versions** in the `.csproj` — `Jellyfin.Controller` /
   `Jellyfin.Model` version `10.10.3` should match your server version
   exactly; if that exact version isn't on NuGet, use the closest
   published one for the 10.10.x line.

None of these are large fixes — they're the kind of thing that shows up
as a handful of compiler errors pointing at the exact problem, not a
design issue.

## Building

You'll need the .NET 8 SDK. On the Jellyfin host or any machine with
network access to nuget.org:

```bash
cd AuthentikAuth
dotnet restore
dotnet build -c Release
```

The compiled `AuthentikAuth.dll` (plus dependency DLLs) will be under
`bin/Release/net8.0/`.

## Installing

```bash
sudo mkdir -p "/var/lib/jellyfin/plugins/Authentik Auth_1.0.0.0"
sudo cp bin/Release/net8.0/*.dll "/var/lib/jellyfin/plugins/Authentik Auth_1.0.0.0/"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/Authentik Auth_1.0.0.0"
sudo systemctl restart jellyfin
```

## Configuring

1. **In Authentik**: create an OAuth2/OpenID provider with redirect URI
   `https://jellyfin.kinloch.org.uk/Authentik/Callback`, and a scope
   mapping for `groups` (same custom Property Mapping you already set up
   for the other plugin — reuse it).
2. **In Jellyfin**: Dashboard → Plugins → Authentik Auth → Settings. Fill
   in the Authentik base URL, application slug, client ID/secret, your
   Jellyfin public URL, and `Home` in both Allowed Groups and Admin
   Groups (matching what you're already using).
3. Point a login link at `https://jellyfin.kinloch.org.uk/Authentik/Start`.

## Cloudflare

Same guidance as before: this needs a hostname-level WAF exemption for
the Jellyfin subdomain (no Turnstile/Managed Challenge), and should sit
behind the tunnel without a Cloudflare Access application in front of
it, since Access would intercept the redirect round-trip the same way
it would have broken the other plugin.

## Plugin icon

Turns out this isn't possible the way I first tried it. `IHasThumbImage`
was an Emby-only interface that Jellyfin removed entirely - it doesn't
exist in current Jellyfin at all, which is why the build failed with
"type not found" rather than a namespace error. Custom icons only show up
for plugins installed **through a plugin repository** (a hosted
`manifest.json` with an `imageUrl`, added as a repo in Jellyfin's Plugin
Catalog) - not for manually-copied DLLs like you're doing. `thumb.png` is
still in the `Configuration` folder if you want to set up repository
hosting later (you already run a domain, so this is realistic - just
more setup than it's worth for now), but it's no longer wired into the
code.

## Making Authentik the default login

New `AutoRedirectToSso` setting (checkbox on the plugin's config page).
When on, `WebIndexPatcher` - now a standard ASP.NET Core `IHostedService`,
registered via `PluginServiceRegistrator` - injects a small script into
Jellyfin's `index.html` on every server start: if there's no existing
`jellyfin_credentials` in localStorage, it calls `/Authentik/AutoRedirectStatus`
and, if enabled, redirects straight to `/Authentik/Start`.

I originally wrote this against `IServerEntryPoint`, which - like
`IHasThumbImage` - turned out to no longer exist in current Jellyfin.
Current Jellyfin's own plugin template recommends `IHostedService` plus
an `IPluginServiceRegistrator` to register it, which is what this now
uses. Same technique (patching `index.html` directly) is used by real
plugins like Jellyscrub and jellyfin-plugin-custom-javascript, so the
overall approach is sound even though my first guess at the specific
interface was wrong.

**Escape hatch, always available:** `https://jellyfin.kinloch.org.uk/web/index.html?manual=1`
bypasses the redirect and shows the normal password form. Bookmark that
somewhere safe - you'll want it if Authentik is ever down, or for any
local admin account.

**Known permission gotcha** (seen with other injection-based plugins):
if the Jellyfin *process* runs as a different user than whoever owns
`/usr/share/jellyfin/web/index.html`, the patch will silently fail to
write and log a warning rather than crash. You've already confirmed
`jellyfin` owns everything under `/var/lib/jellyfin`, but the *web*
files live under `/usr/share/jellyfin/web` (a different path, likely
owned by root since it comes from the distro package) - worth checking:

```bash
ls -la /usr/share/jellyfin/web/index.html
```

If that's not owned by (or at least writable by) the `jellyfin` user,
the auto-redirect won't apply and you'll see a warning like
`AuthentikAuth: no permission to write to index.html` in the Jellyfin
log after a restart.

## Android

Same limitation as any Jellyfin SSO plugin: this only covers the web
login. Native apps still need Quick Connect for pairing.
