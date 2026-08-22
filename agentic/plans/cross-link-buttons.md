# Cross-Link Buttons — Implementation Plan

| | |
| --- | --- |
| **Status** | Approved — scope decisions recorded 2026-08-21 ([§12](#12-decisions-taken)); not started |
| **Source** | Moved out of [`IDEAS.md`](../IDEAS.md) (high-priority item), expanded and re-verified |
| **Target Jellyfin** | `12.0.0-rc4` (the pinned package version — see HANDOFF "Known gotchas") |
| **Research verified** | 2026-08-21, against the pinned NuGet assemblies, `jellyfin` master, `jellyfin-web` master, and `n00bcodr/Jellyfin-Enhanced` main |
| **Effort** | Phase 1 ~2–3 h · Phase 2 ~half day · Phase 3 ~1 h · `PairStore` indexes ~1–2 h |
| **Release shape** | Minor bump (new feature + new config options) — `1.1.0.0` per the version convention |

---

## 1. Goal

A button in the external-links row of an item's detail page (alongside IMDb / TMDB / TVDB) that jumps
to the linked counterpart:

- a Season 0 special that the plugin has paired gets a **Movie Version** button;
- the paired movie gets a **TV Special** button.

Secondary goal: the button should sit comfortably next to the buttons injected by
[Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) (JE). **JE is not a dependency at
any layer** — see [§6.4](#64-does-this-require-jellyfin-enhanced).

Success looks like: on a paired episode's page a user clicks one button and lands on the movie, and
vice versa, with no extra tab, no configuration beyond a master toggle, and no visible change for
unpaired items.

In-app navigation comes from the Phase 2 client script, which is **on by default**
([D5](#12-decisions-taken)) so the goal above is met on a fresh install. A user who does not want the
plugin rewriting the served `index.html` turns `InjectClientScript` off and keeps a working button
that opens a new tab instead.

---

## 2. Phases

| Phase | Delivers | Standalone? |
| --- | --- | --- |
| **1 — Server-side links** | Two `IExternalUrlProvider` implementations + config fields. Renders as a plain text link exactly like the stock IMDb/TMDB links, opens in a new tab. | Yes — ships value on its own |
| **2 — Client-side polish** | Icon button + in-app SPA navigation (no new tab), via an injected client script. | Pure progressive enhancement over Phase 1 |
| **3 — Surface and docs** | Config page section, README, AUDIT entry. | Required before release |

**All three phases ship in one release** ([D1](#12-decisions-taken)). Phase 2 still gets its own
`InjectClientScript` flag — defaulting **on** ([D5](#12-decisions-taken)) — so that a user who
objects to request-time rewriting of `index.html` can switch it off and keep working buttons.

Build them in phase order regardless: Phase 1 is independently testable, and if the JE-coexistence
test in [§10](#10-testing-checklist) fails late, Phase 1 can still be released on its own with
`InjectClientScript` removed from the config page.

A fourth work item ships alongside them: the `PairStore` indexes in
[§5.4](#54-pairstore-indexes-in-scope-for-this-change) ([D4](#12-decisions-taken)).

---

## 3. Verified research

Everything in this section was checked on 2026-08-21 rather than assumed. "Reflection probe" means a
throwaway console project referencing the same pinned `Jellyfin.Controller` / `Jellyfin.Model`
packages the plugin uses, dumping the members of the type in question.

### 3.1 The extension point is `IExternalUrlProvider`

`MediaBrowser.Controller.Providers.IExternalUrlProvider` exists in the pinned package with exactly
two members (reflection probe):

```
P String Name
M IEnumerable`1 GetExternalUrls(BaseItem item)   // IEnumerable<string>
```

The alternative — `IExternalId` with a `UrlFormatString` — is the wrong tool: it is keyed to a
provider ID stored on the item and also surfaces in the metadata editor. `IExternalUrlProvider` is
what the official TVDB plugin uses to place its links on detail pages.

### 3.2 Discovery is automatic; do not register in `PluginServiceRegistrator`

`ApplicationHost.FindParts()` (jellyfin master, `Emby.Server.Implementations/ApplicationHost.cs`)
runs **after** `_pluginManager.CreatePlugins()` and calls:

```csharp
Resolve<IProviderManager>().AddParts(
    GetExports<IImageProvider>(), GetExports<IMetadataService>(),
    GetExports<IMetadataProvider>(), GetExports<IMetadataSaver>(),
    GetExports<IExternalId>(), GetExports<IExternalUrlProvider>());
```

`GetExports<T>()` walks every concrete type in the composable assemblies (plugin assemblies
included) and instantiates each through `CreateInstanceSafe`, which is
`ActivatorUtilities.CreateInstance(ServiceProvider, type)` against the **root** provider. Three
consequences that shape the design:

1. A public class implementing the interface is picked up with **no DI registration**. Registering it
   in `PluginServiceRegistrator` as well would risk a second, unrelated instance — don't.
2. Constructor arguments resolve from the root container, so `IPairStore` (registered in
   [`PluginServiceRegistrator.cs`](../../PluginServiceRegistrator.cs)), `ILibraryManager` and
   `IServerApplicationHost` are all injectable.
3. **A throwing constructor disables the entire plugin.** `CreateInstanceSafe` catches, logs
   `Error creating {Type}`, and calls `_pluginManager.FailPlugin(type.Assembly)`. The constructors
   must therefore do nothing but assign fields — no config reads, no I/O, no `Plugin.Instance`
   dereference.

Because `FindParts` runs after `CreatePlugins`, `Plugin.Instance` is already set by the time the
providers are constructed — but the null guard stays anyway, matching the codebase convention
(`Plugin.Instance?.Configuration` appears in every service).

The instances are constructed once at startup and held by `ProviderManager` for the process
lifetime, so injecting the `IPairStore` singleton is correct and cheap.

### 3.3 One caption per provider class ⇒ two classes

`ProviderManager` (jellyfin master, `MediaBrowser.Providers/Manager/ProviderManager.cs`):

```csharp
_externalUrlProviders = externalUrlProviders.OrderBy(i => i.Name).ToArray();
...
public IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item)
    => _externalUrlProviders.SelectMany(p => p.GetExternalUrls(item)
        .Select(externalUrl => new ExternalUrl { Name = p.Name, Url = externalUrl }));
```

The caption is the **provider's** `Name`, not a per-URL value, so two different captions require two
provider classes. `Name` is a property read at call time, which is what makes a config-driven label
possible — but note the `OrderBy(i => i.Name)` above: `Name` is also read **at startup**, inside
`AddParts`, before any request exists. So the getter must be as defensive as the constructor: no
config dereference that can throw, no `Plugin.Instance!`. The `Label(configured, fallback)` helper in
[§5.1](#51-new-files) exists precisely so both call sites are safe. `ExternalUrl` lives in `MediaBrowser.Model.Providers` (reflection probe) — not
`MediaBrowser.Model.Dto`, as the earlier draft assumed.

### 3.4 DTO gating keeps this off the hot paths

`DtoService.AttachBasicFields` (jellyfin master):

```csharp
if (options.ContainsField(ItemFields.ExternalUrls))
{
    dto.ExternalUrls = _providerManager.GetExternalUrls(item).ToArray();
}
```

`ItemFields.ExternalUrls` exists in the pinned model assembly (reflection probe), and the detail-page
request does ask for it — verified, not assumed: `UserLibraryController.GetItem` builds
`var dtoOptions = new DtoOptions();`, and `DtoOptions()`'s parameterless constructor sets
`Fields = AllItemFields`, which is every `ItemFields` value except `SeasonUserData` and
`RefreshState`. List and grid queries pass an explicit narrower field set, so the provider is called
about once per detail-page view, not once per poster in a library grid.

Note the `.ToArray()`: the sequence returned by `GetExternalUrls` is **enumerated inside the DTO
pipeline**, which drives the exception-safety design in [§5.2](#52-exception-safety-non-obvious).

### 3.5 How the web client renders it — verified verbatim

`jellyfin-web` master, `src/apps/legacy/controllers/itemDetails/index.js`:

```js
function renderLinks(page, item) {
    const externalLinksElem = page.querySelector('.itemExternalLinks');
    const links = [];

    if (!layoutManager.tv && item.HomePageUrl) {
        links.push(`<a is="emby-linkbutton" class="button-link" href="${item.HomePageUrl}" target="_blank">${globalize.translate('ButtonWebsite')}</a>`);
    }

    if (item.ExternalUrls) {
        for (const url of item.ExternalUrls) {
            links.push(`<a is="emby-linkbutton" class="button-link" href="${url.Url}" target="_blank">${escapeHtml(url.Name)}</a>`);
        }
    }
    ...
    externalLinksElem.innerHTML = html.join(', ');
```

What matters:

- The container is `.itemExternalLinks` inside `#itemDetailPage`. This is still the live code path on
  master; there is no React replacement for the item detail page yet.
- **`ExternalUrls` are not gated by `layoutManager.tv`** — only `HomePageUrl` is. The button appears
  in the TV layout too. (A summarising fetch of this same file claimed the opposite; the verbatim
  source above is the authority.)
- `url.Name` is HTML-escaped; **`url.Url` is interpolated raw into `href`**. Our URLs are built from
  a GUID and a system ID only, so there is no injection vector — but this is a **standing constraint
  to record in `AUDIT.md`: never place user-controlled text in the emitted URL**, the configurable
  labels included (labels go through `Name`, which is escaped).
- `target="_blank"` is hardcoded ⇒ a purely server-side link opens a new browser tab. Removing that
  requires client-side code, which is the whole justification for Phase 2.
- `innerHTML` is reassigned on every render, so any client-side upgrade of these anchors must be
  re-applied on each detail-page render (hence a `MutationObserver`, not a one-shot pass).

### 3.6 The in-app route

`#/details?id={itemId}&serverId={serverId}` — the same shape for `Movie` and `Episode`
(`appRouter.getRouteUrl`). `serverId` is `IApplicationHost.SystemId`; `IServerApplicationHost`
inherits from `MediaBrowser.Common.IApplicationHost` (reflection probe confirms `SystemId` is
declared on the base interface, not on `IServerApplicationHost` itself), so injecting
`IServerApplicationHost` and reading `.SystemId` compiles and is correct.

A **hash-only relative URL** — `#/details?id=…&serverId=…` — resolves against the current document,
which on a detail page is already `…/web/#/details?id=…`. That works unchanged behind a reverse
proxy, under a custom base URL, and over remote access, with no knowledge of the external hostname.

### 3.7 Absolute URLs must come from the request, not from config

The earlier design proposed a `CrossLinkUrlStyle = Absolute` mode "built from the published server
URL". In `12.0.0-rc4` that is not reachable from a plugin with the current package references:

- `MediaBrowser.Model.Configuration.ServerConfiguration` has **no** `BaseUrl` / `PublishedServerUri`
  property (full property dump taken; network settings live in a `NetworkConfiguration` type in
  `Jellyfin.Networking`, an assembly this plugin does not reference).
- `IServerApplicationHost` does expose `GetSmartApiUrl(HttpRequest)`, `GetSmartApiUrl(IPAddress)` and
  `GetSmartApiUrl(string hostname)` — all of which need request context that
  `IExternalUrlProvider.GetExternalUrls(BaseItem)` does not receive.

So absolute mode means injecting `IHttpContextAccessor` and calling `GetSmartApiUrl(HttpRequest)` on
the in-flight request. That route is confirmed available:

- `Jellyfin.Server/Startup.cs` (master) calls `services.AddHttpContextAccessor()` in
  `ConfigureServices` — the same `IServiceCollection` that `ApplicationHost.Init` receives, so
  `IHttpContextAccessor` resolves from the root provider our providers are constructed against
  ([§3.2](#32-discovery-is-automatic-do-not-register-in-pluginserviceregistrator)).
- The DTO is built on the *calling client's* request, so a non-web client (Android/iOS/TV) asking for
  the item gets a URL derived from its own connection — which is exactly the case absolute mode
  exists for.

What `GetSmartApiUrl(HttpRequest)` actually returns (jellyfin master, `ApplicationHost.cs`), because
it decides how trustworthy the result is:

```csharp
public string GetSmartApiUrl(HttpRequest request)
{
    if (ConfigurationManager.GetNetworkConfiguration().EnablePublishedServerUriByRequest)
    {
        int? requestPort = request.Host.Port;
        if (requestPort is null
            || (requestPort == 80 && string.Equals(request.Scheme, "http", …))
            || (requestPort == 443 && string.Equals(request.Scheme, "https", …)))
        {
            requestPort = -1;
        }

        return GetLocalApiUrl(request.Host.Host, request.Scheme, requestPort);
    }

    return GetSmartApiUrl(request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback);
}
```

Consequences that shape [§5.5](#55-absolute-url-mode):

1. The returned string is a **base** URL — scheme, host, port, and the configured `BaseUrl` path —
   with the trailing slash trimmed (`GetLocalApiUrl` builds it through `UriBuilder`). The details
   route must therefore be appended as `{base}/web/#/details?…`, and the `BaseUrl` path must **not**
   be added a second time.
2. A configured `PublishedServerUrl` takes precedence over everything else (in the `IPAddress` /
   `hostname` overloads), which is the correct outcome for remote users.
3. With `EnablePublishedServerUriByRequest` **off** and no published URL, the result is derived from
   `Connection.RemoteIpAddress` — behind a reverse proxy that is the proxy's address unless
   forwarded headers are honoured, so the URL can come back as a LAN address. This is a real failure
   mode, not a theoretical one, and it is why `Relative` stays the default.
4. With `EnablePublishedServerUriByRequest` **on**, the host comes from the request's `Host` header —
   i.e. from the client. It only ever affects that client's own response, but it does mean the
   emitted URL is no longer built purely from server-side values; see the validation requirement in
   [§5.5](#55-absolute-url-mode).
5. There is no ambient `HttpContext` when a DTO is built outside a request, so `HttpContext` may be
   `null` and the code must fall back to the relative form.

### 3.8 There is no plugin script hook; `IStartupFilter` is the only route

The pinned `MediaBrowser.Controller.Plugins` namespace contains exactly two types (reflection probe):
`IHasEmbeddedImage` and `IPluginServiceRegistrator`. `IHasWebPages` (which the plugin already
implements) is config-page-only. There is no supported way to add a script to the served web app.

`Microsoft.AspNetCore.Hosting.IStartupFilter` **is** resolvable from the plugin's existing package
references (verified by compiling against it with only `Jellyfin.Controller` + `Jellyfin.Model`
referenced), which makes JE's approach available to us: middleware registered outermost that rewrites
the `index.html` response in memory.

### 3.9 The Jellyfin Enhanced patterns worth copying — read verbatim

**`Services/ScriptInjectionStartupFilter.cs`** (JE main). Its ordering and its failure handling are
the parts that matter, and several of them were missing from the earlier draft:

- `Configure` does `app.Use(InvokeAsync); next(app);` — registering **before** the rest of the
  pipeline so it runs outermost.
- Non-index paths return immediately. `IsIndexRequest` matches `EndsWith("/web/index.html")`,
  `EndsWith("/web/")` and `== "/web"` — the `EndsWith` form is what keeps it correct under a base-URL
  prefix.
- **Non-`GET` requests pass straight through.** Buffering a `HEAD` would compute a bogus
  `Content-Length` against an empty downstream body.
- Strips `Accept-Encoding` (so the downstream response is uncompressed and readable) plus `Range` /
  `If-Range` (a `206` would otherwise pass through un-injected with a wrong total length).
- Swaps `Response.Body` for a `MemoryStream`; **on a downstream exception it restores the original
  body and rethrows** rather than flushing a truncated 200.
- Only rewrites when the status is `200` and `Content-Type` contains `text/html`; everything else
  (`304`, redirects, non-HTML) is copied through untouched.
- Idempotency keyed on the controller endpoint substring; insertion at the **last** `</body>`.
- Any error inside the rewrite is caught and the original HTML is served.
- Afterwards it sets `Content-Type` and `Content-Length`, and removes `ETag`, `Last-Modified` and
  `Accept-Ranges`, because the body no longer matches the static file's validators.
- Logs the injection exactly once via `Interlocked.Exchange` on a flag.

**`js/others/letterboxd-links.js`** (JE main) — the button conventions:

- anchor: `is="emby-linkbutton"`, `target="_blank"`, `rel="noopener noreferrer"`;
- classes: `button-link emby-button letterboxd-link`, plus `letterboxd-link-icon` in icon mode
  (text vs icon is config-driven);
- icon: an injected `<style id="letterboxd-links-styles">` defining `.letterboxd-link-icon::before`
  with `content:""`, `display:inline-block`, a 25 px square, `background-image:url(...)`,
  `background-size:contain`, `background-repeat:no-repeat`, `vertical-align:middle`,
  `margin-right:5px`;
- a `MutationObserver` on `document.body` with `{childList:true, subtree:true,
  attributeFilter:['class']}`, work deferred through `requestIdleCallback(..., {timeout:500})` behind
  an in-flight boolean, with a `setTimeout` fallback;
- a `processedItemIds` set plus a `lastVisibleItemId`, cleared when the visible item changes;
- stale links removed from `#itemDetailPage.hide` on every pass;
- the current item id read from the hash:
  `new URLSearchParams(window.location.hash.split('?')[1]).get('id')`;
- `arr-tag-links.js` additionally hangs `data-id` / `data-tag` on the anchor, and JE's
  `docs/advanced/css-customization.md` documents `.itemExternalLinks a.arr-tag-link[data-id="…"]`
  recipes — i.e. `data-*` attributes are the convention JE users' own CSS depends on.

JE's script has to *create* its anchors, which costs it an `ApiClient.getItem` round trip per detail
page. **Ours do not** — Phase 1 already put the anchor in the DOM, so our script only restyles an
existing element and intercepts its click. That is strictly less work and strictly less fragile.

### 3.10 A permission-aware lookup exists (relevant to the visibility leak)

`ILibraryManager` exposes `GetItemById(Guid)`, `GetItemById<T>(Guid)`, `GetItemById<T>(Guid, Guid userId)`
and `GetItemById<T>(Guid, User)` (reflection probe), and `BaseItem` exposes `IsVisible(User)` /
`IsVisibleStandalone(User)`. `IExternalUrlProvider` has no user context of its own, but if the
`IHttpContextAccessor` route in [§3.7](#37-absolute-urls-must-come-from-the-request-not-from-config)
proves reliable, the same accessor yields the authenticated user and makes a server-side visibility
check possible. Otherwise the check belongs client-side (Phase 2), or is accepted and documented.

### 3.11 A script-free styling escape hatch exists

`MediaBrowser.Model.Branding.BrandingOptions` has a `CustomCss` property, and
`IServerConfigurationManager.GetConfiguration("branding")` / `SaveConfiguration("branding", …)` can
read and write it. So the icon-button look is achievable with **no script injection at all**.

This plan deliberately does **not** have the plugin write to `CustomCss` — that is a shared,
user-owned server setting, and silently editing it is exactly the kind of surprise the audit rules
exist to prevent. Instead, [Phase 3](#8-phase-3--surface-and-docs) documents a copy-paste CSS snippet
in the README for users who want the icon without enabling script injection.

### Corrections this research made to the earlier `IDEAS.md` design

| Earlier claim | Corrected |
| --- | --- |
| `ExternalUrl` is in `MediaBrowser.Model.Dto` | It is in `MediaBrowser.Model.Providers` |
| `CrossLinkUrlStyle=Absolute` built "from the published server URL" | No such property exists in `ServerConfiguration` on 12.0.0-rc4. Absolute mode is built from the live request via `IHttpContextAccessor` + `GetSmartApiUrl(HttpRequest)` — confirmed registered, with the caveats in [§3.7](#37-absolute-urls-must-come-from-the-request-not-from-config) |
| "wrap the body defensively" in `GetExternalUrls` | A `try/catch` inside a `yield return` iterator never runs at call time — the exception surfaces inside `DtoService`. It needs a non-iterator wrapper ([§5.2](#52-exception-safety-non-obvious)) |
| (not mentioned) | A throwing provider **constructor** makes `PluginManager.FailPlugin` disable the whole plugin |
| (not mentioned) | JE's filter also passes non-`GET` through, rethrows on downstream failure, and removes `Accept-Ranges` |
| Uncertain whether `ExternalUrls` render in the TV layout | Confirmed: not gated by `layoutManager.tv`; they do render |

---

## 4. Design decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Extension point | `IExternalUrlProvider` ×2 | The only way to get two distinct captions ([§3.3](#33-one-caption-per-provider-class--two-classes)) |
| Registration | None — automatic discovery | `GetExports` finds it; explicit registration risks a duplicate instance |
| URL form (default) | Hash-relative `#/details?id=…&serverId=…&stm=m` | Proxy-safe, base-URL-safe, needs no hostname knowledge |
| URL form (opt-in) | Absolute, built from the live request ([§5.5](#55-absolute-url-mode)) | The only form a non-web client can follow |
| Marker | `&stm=m` (movie target) / `&stm=s` (special target) | Lets the Phase 2 script recognise our anchors and their direction with zero API calls, without depending on the user-configurable caption |
| Which pairs link | `Status == Active` **and** `MovieItemId` non-empty; `IsExistingMovie` included | The button is about navigation, not lifecycle ownership. `DryRun` / `Pending` / `Error` pairs have nothing to navigate to |
| Stale-target check | `ILibraryManager.GetItemById` on the target before yielding | Prevents a dead button after an out-of-band deletion; the library manager caches, so it is one cheap in-memory hit |
| Labels | Config fields with non-empty fallbacks | `Name` is read per call, so labels change without a restart |
| Phase 2 toggle | Separate `InjectClientScript` flag, default **on** | The feature's point is in-app navigation, and JE sets the precedent for injecting by default; the flag exists so users who distrust `index.html` rewriting can drop to Phase 1 ([D5](#12-decisions-taken)) |
| CSS-only alternative | Documented snippet, not written by the plugin | Never silently mutate a shared server setting ([§3.11](#311-a-script-free-styling-escape-hatch-exists)) |
| Visibility leak | Accepted and documented, no per-user check | The disclosure is an opaque GUID plus a fixed caption; a per-user check costs a request per detail page (Phase 2) or ties the provider to request context it should not need ([D3](#12-decisions-taken)) |
| `PairStore` indexes | In scope for this change | The lookups move onto a request path that shares a lock with file I/O ([§5.4](#54-pairstore-indexes-in-scope-for-this-change)) |

---

## 5. Phase 1 — server-side links

### 5.1 New files

```
Providers/
├── CrossLinkUrlBuilder.cs       ← pure string helpers (route shape, label fallback)
├── CrossLinkUrlResolver.cs      ← relative-vs-absolute decision; holds the host + accessor
├── LinkedMovieUrlProvider.cs    ← on an Episode → link to the paired Movie
└── LinkedSpecialUrlProvider.cs  ← on a Movie   → link to the paired Episode
```

**Registration asymmetry, easy to get wrong:** the two providers must **not** be registered
([§3.2](#32-discovery-is-automatic-do-not-register-in-pluginserviceregistrator)), but
`CrossLinkUrlResolver` **must** be — `ActivatorUtilities.CreateInstance` resolves a part's
constructor parameters from the container and does not construct unregistered concrete types on the
fly. So `PluginServiceRegistrator` gains
`serviceCollection.AddSingleton<CrossLinkUrlResolver>();` and nothing else for Phase 1.

**`Providers/CrossLinkUrlBuilder.cs`** — the one place that builds a URL, so the "no user-controlled
text in the URL" rule has a single enforcement point:

```csharp
internal static class CrossLinkUrlBuilder
{
    /// <summary>
    /// Builds the details route for an item. Only a GUID, the server's system ID,
    /// and a fixed marker ever reach this string — never user-supplied text,
    /// because jellyfin-web interpolates ExternalUrl.Url into href without
    /// escaping. In absolute mode the request-derived base is validated first
    /// (see BuildAbsolute).
    /// </summary>
    public static string Details(Guid itemId, string systemId, char marker)
        => $"#/details?id={itemId:N}&serverId={systemId}&stm={marker}";

    public static string Label(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured;
}
```

`marker` is `'m'` when the target is the movie and `'s'` when the target is the special. The Phase 2
script reads it to tag the anchor without depending on the user-configurable caption.

**`Providers/LinkedMovieUrlProvider.cs`** (the `LinkedSpecialUrlProvider` is the mirror image —
`item is Movie`, `GetByMovieId`, target `EpisodeItemId`, label `SpecialLinkLabel`):

```csharp
public class LinkedMovieUrlProvider : IExternalUrlProvider
{
    private readonly IPairStore _pairStore;
    private readonly ILibraryManager _libraryManager;
    private readonly CrossLinkUrlResolver _urls;      // wraps IServerApplicationHost + IHttpContextAccessor
    private readonly ILogger<LinkedMovieUrlProvider> _logger;

    // Field assignment only — a throw here fails the whole plugin (see §3.2).
    public LinkedMovieUrlProvider(
        IPairStore pairStore,
        ILibraryManager libraryManager,
        CrossLinkUrlResolver urls,
        ILogger<LinkedMovieUrlProvider> logger)
    { /* assign */ }

    public string Name =>
        CrossLinkUrlBuilder.Label(Plugin.Instance?.Configuration.MovieLinkLabel, "Movie Version");

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        try
        {
            return Build(item);          // materialised array, never a lazy iterator
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cross-link lookup failed for {ItemId}", item?.Id);
            return Array.Empty<string>();
        }
    }

    private string[] Build(BaseItem item)
    {
        if (item is not Episode) return Array.Empty<string>();      // cheapest rejection first

        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.ShowCrossLinks) return Array.Empty<string>();

        var pair = _pairStore.GetByEpisodeId(item.Id);
        if (pair is null
            || pair.Status != PairStatus.Active
            || pair.MovieItemId is null
            || pair.MovieItemId == Guid.Empty) return Array.Empty<string>();

        if (_libraryManager.GetItemById(pair.MovieItemId.Value) is null) return Array.Empty<string>();

        return [_urls.Resolve(pair.MovieItemId.Value, 'm')];
    }
}
```

The guard order is deliberate: type check (a field comparison) → config flag → store lookup →
library lookup. On a library where nothing is paired, the cost per detail-page render is two type
checks and one bool.

### 5.2 Exception safety (non-obvious)

`GetExternalUrls` must **not** be a `yield return` iterator with a `try/catch` around its body. An
iterator's body does not run when the method is called — it runs when `DtoService` enumerates it
inside `_providerManager.GetExternalUrls(item).ToArray()`, at which point our `catch` is long out of
scope and the exception escapes into the DTO pipeline, breaking the entire item-detail response. The
wrapper above (non-iterator method, materialised array) is what makes the `try/catch` meaningful.

### 5.3 Config additions

In [`Configuration/PluginConfiguration.cs`](../../Configuration/PluginConfiguration.cs):

| Field | Type | Default | Purpose |
| --- | --- | --- | --- |
| `ShowCrossLinks` | `bool` | `true` | Master on/off; read per call, so it takes effect without a restart |
| `MovieLinkLabel` | `string` | `"Movie Version"` | Caption shown on the episode's page |
| `SpecialLinkLabel` | `string` | `"TV Special"` | Caption shown on the movie's page |
| `InjectClientScript` | `bool` | `true` | Phase 2 — enables `index.html` rewriting; off means plain links that open a new tab |
| `CrossLinkUrlStyle` | `CrossLinkUrlStyle` enum | `Relative` | `Relative` = in-app hash route (web clients); `Absolute` = request-derived full URL (non-web clients) — [§5.5](#55-absolute-url-mode) |

The new enum follows the existing `MetadataProviderType` precedent exactly — declared in the same
file, `[JsonConverter(typeof(JsonStringEnumConverter))]`, explicit member values (`Relative = 0`,
`Absolute = 1`) — and the config-page loader tolerates both the numeric and the string form, as the
`PrimaryProvider` loader already does:

```js
var us = config.CrossLinkUrlStyle;
page.querySelector('#selectCrossLinkUrlStyle').value =
    (us === 1 || us === 'Absolute') ? 'Absolute' : 'Relative';
```

New fields are absent from an existing `PluginConfiguration.xml`, so on upgrade they deserialise to
the C# initialiser default — which is the intended value in every case above.

### 5.4 `PairStore` indexes (in scope for this change)

`GetByEpisodeId` / `GetByMovieId` are `List.Find` linear scans under a lock
([`Data/PairStore.cs`](../../Data/PairStore.cs)). Today they are called from scans and event
handlers; this feature calls them from a request path, so they get proper indexes ([D4](#12-decisions-taken)).

The scan cost itself is minor. The reason it matters is the lock: `Save()` performs a backup copy, a
serialise, a temp write and a rename **while holding it**, so a detail-page render can block behind a
full `pairs.json` write. Indexing does not fix that — but it removes any argument about the lookup's
own cost and keeps the critical section short.

- Add `Dictionary<Guid, LinkedPair>` for `EpisodeItemId` and `Dictionary<Guid, LinkedPair>` for
  `MovieItemId`.
- Rebuild both in `Load` (including the backup-restore path) and clear both in `Clear`.
- Maintain both in `Upsert`, `UpsertMany`, `Remove`, `RemoveMany`.
- Keep the existing lock and its semantics — the win is O(1) lookups, not lock removal.

**The correctness trap, and why the obvious fix does not work.** `MovieItemId` genuinely changes
across `Upsert` calls — four paths in the current code do it:

| Path | Transition |
| --- | --- |
| `LibraryEventHandler` — movie scanned at the hard link path | `null` → id, `Pending` → `Active` |
| `CleanupTask` — pending pair promoted | `null` → id |
| `CleanupTask` — hard link missing, recreated | id → `null`, back to `Pending` |
| `SpecialToMovieController.RemoveAllLinks` | id → `null`, back to `DryRun` |

Index the new key without evicting the old one and `GetByMovieId(oldId)` keeps returning a pair that
no longer claims that movie — which would corrupt deletion handling, not merely this feature.

The natural fix — "remove the replaced entry's old keys, read from the object being replaced" —
**does not work here**, and this is the part worth knowing before writing the code. Callers mutate
the object *in place* and then hand the same reference back:

```csharp
var pair = _pairStore.GetByHardLinkPath(movie.Path);   // live reference into _pairs
pair.MovieItemId = movie.Id;                            // mutated before Upsert sees it
pair.Status = PairStatus.Active;
_pairStore.Upsert(pair);
```

`GetAll` copies the *list*, not its elements, and the other getters return the stored reference
directly, so inside `Upsert` the entry at `_pairs[index]` **is** the incoming `pair`. The previous
key is already overwritten and cannot be recovered from either object.

So the store must remember what it indexed, independently of the pair objects:

```csharp
// pair Id → the keys currently present in the indexes for that pair
private readonly Dictionary<Guid, (Guid Episode, Guid? Movie)> _indexedKeys = new();
```

Every mutation path evicts using `_indexedKeys[pair.Id]`, re-inserts, and updates the record;
`Remove` / `RemoveMany` evict and drop the record; `Load` / `Clear` rebuild all three structures
together. This is the single design point that makes the change safe — everything else about it is
routine.

Two further constraints:

- The `MovieItemId` index must skip `null` and `Guid.Empty` (unlinked and dry-run pairs) rather than
  keying on them — otherwise every unlinked pair collides on one key.
- Neither key is guaranteed unique by any invariant in the store, and `List.Find` today returns the
  **first** match. Use `TryAdd`-style semantics (first writer wins) so the indexed lookups return
  what the scans returned, and keep `GetAll` as the only order-sensitive accessor.

`EpisodeItemId`, by contrast, is only ever assigned when a pair is constructed (verified across the
detection service, cleanup task, event handler, and controller) — but index it through the same
`_indexedKeys` mechanism anyway rather than relying on that remaining true.

A cheap safety net: keep the `List.Find` implementations as private fallbacks and add a debug-only
assertion that the index result matches, so a maintenance bug surfaces in testing rather than as a
silently wrong button.

Out of scope, but noted: because callers mutate live references before calling `Upsert`, a concurrent
reader can already observe a half-updated pair. That predates this change and the indexes neither
cause nor worsen it — it belongs in `IDEAS.md`, not here.

### 5.5 Absolute URL mode

`CrossLinkUrlStyle = Absolute` ([D2](#12-decisions-taken)) exists for non-web clients, which hand
`ExternalUrls` to a browser intent where a relative hash URL is meaningless.

`Providers/CrossLinkUrlResolver.cs` is the only place that knows about the difference:

```csharp
public class CrossLinkUrlResolver
{
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpContextAccessor _http;

    public CrossLinkUrlResolver(IServerApplicationHost appHost, IHttpContextAccessor http) { /* assign */ }

    public string Resolve(Guid targetId, char marker)
    {
        var route = CrossLinkUrlBuilder.Details(targetId, _appHost.SystemId, marker);

        if (Plugin.Instance?.Configuration.CrossLinkUrlStyle != CrossLinkUrlStyle.Absolute)
        {
            return route;
        }

        var request = _http.HttpContext?.Request;
        if (request is null)
        {
            return route;                       // DTO built outside a request — §3.7 point 5
        }

        var basis = _appHost.GetSmartApiUrl(request);

        // The emitted URL lands in an href that jellyfin-web does not escape (§3.5),
        // and with EnablePublishedServerUriByRequest the host comes from the request
        // (§3.7 point 4). Emit it only if it parses as an absolute http(s) URI.
        if (!Uri.TryCreate(basis, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return route;
        }

        return $"{basis.TrimEnd('/')}/web/{route}";
    }
}
```

Points that are easy to get wrong:

- `GetSmartApiUrl` already includes the configured `BaseUrl` path — do **not** prepend or append it
  again ([§3.7](#37-absolute-urls-must-come-from-the-request-not-from-config) point 1).
- `route` already starts with `#`, so the concatenation yields `…/web/#/details?…`.
- Every failure path degrades to the relative form rather than emitting nothing, so a web client is
  never left without a button.
- The `Uri.TryCreate` validation is the mitigation for the unescaped-`href` constraint in
  [§3.5](#35-how-the-web-client-renders-it--verified-verbatim): under `Absolute` the URL is no longer
  built purely from a GUID and the system ID, so it is validated before it is emitted.
- The resolver reads config per call, so switching styles needs no restart.
- Absolute mode changes the emitted URL per requesting client. Nothing caches these DTOs, so that is
  correct rather than a hazard — but it does mean the Phase 2 script must not assume the href is
  relative ([§6.1](#61-webspecialtomoviejs-embedded-resource)).

---

## 6. Phase 2 — client-side polish

Pure progressive enhancement. If the script fails, is blocked, or is disabled, Phase 1 still works —
a plain text link that opens a new tab.

### 6.1 `Web/specialtomovie.js` (embedded resource)

Added to the `.csproj` alongside the existing `configPage.html` embedded resource.

Behaviour, mirroring JE's structure so the two coexist predictably:

1. Inject `<style id="specialtomovie-links-styles">` once, defining
   `.specialtomovie-link-icon::before` with JE's exact geometry (`content:""`, `inline-block`, 25 px
   square, `background-size:contain`, `background-repeat:no-repeat`, `vertical-align:middle`,
   `margin-right:5px`). The icon is an **inline `data:` URI SVG** — deliberately not a CDN URL, so
   the feature works on an air-gapped server and adds no third-party request.
2. `MutationObserver` on `document.body` with `{childList:true, subtree:true,
   attributeFilter:['class']}`, work deferred via `requestIdleCallback(..., {timeout:500})` behind an
   in-flight boolean, with a `setTimeout` fallback where `requestIdleCallback` is missing.
3. Each pass:
   `document.querySelectorAll('#itemDetailPage:not(.hide) .itemExternalLinks a[href*="stm="]:not([data-stm-upgraded])')`.
   For each match:
   - add `class="button-link emby-button specialtomovie-link specialtomovie-link-icon"`;
   - read the marker and the target GUID out of the href — parse the hash portion
     (`href.split('#')[1]`) with `URLSearchParams` so the same code works for both the relative and
     the absolute form — and set `data-specialtomovie="movie"` (marker `m`) or `"special"`
     (marker `s`) plus `data-linked-id`. The `data-*` habit is what makes JE users'
     CSS-customisation snippets transfer to our button;
   - if the href starts with `#`, `removeAttribute('target')` so it navigates in place; if it is
     absolute (the user chose `Absolute` for their non-web clients), leave `target` alone;
   - set `data-stm-upgraded="1"`.
4. Re-application is expected, not exceptional: `renderLinks` reassigns
   `externalLinksElem.innerHTML` on every render, destroying the upgrade; the observer sees the
   `childList` mutation and upgrades the fresh anchor.
5. A clean-up pass over `#itemDetailPage.hide .specialtomovie-link`, as JE does, so stale detail
   pages do not accumulate upgraded anchors.
6. Guard the whole module in a `try/catch` and log through a single `console.warn` prefix; never let
   a failure interfere with the rest of the page.

The direction is taken from the `stm=m` / `stm=s` marker rather than from the caption, because the
caption is user-configurable and may be translated or renamed at any time.

### 6.2 `Api/ClientScriptController.cs`

```csharp
[ApiController]
[Route("SpecialToMovie")]
[AllowAnonymous]
public class ClientScriptController : ControllerBase
{
    [HttpGet("ClientScript")]
    [Produces("application/javascript")]
    public ActionResult GetClientScript() { /* embedded resource + version-stamped ETag */ }
}
```

This is the plugin's **first** anonymous route — every existing route is
`Policies.RequiresElevation`
([`Api/SpecialToMovieController.cs`](../../Api/SpecialToMovieController.cs)). It must stay a fixed
embedded asset: no route or query input, no reflection of request data, no configuration values
interpolated into the body. It has to be anonymous because the browser fetches it from the login page
onwards, before any user is authenticated.

The `ETag` is stamped from the assembly version so a plugin upgrade invalidates cached copies.

### 6.3 `Services/ScriptInjectionStartupFilter.cs`

Implement the JE pattern from [§3.9](#39-the-jellyfin-enhanced-patterns-worth-copying--read-verbatim)
point for point, including the parts the earlier draft omitted (non-`GET` pass-through, rethrow on
downstream exception, `Accept-Ranges` removal, one-shot logging).

- Register in `PluginServiceRegistrator`:
  `serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();`
  (This one **is** an explicit DI registration — unlike the URL providers, nothing discovers it.)
  This depends on plugin registrators running before the host builds its pipeline, which they do:
  `ApplicationHost.Init(IServiceCollection)` is called from `Startup.ConfigureServices`, and JE ships
  exactly this registration today. It is the one structural assumption in Phase 2 that is load-bearing
  — if a future Jellyfin moves plugin registration after `Build()`, the filter silently stops running,
  so the "with injection on, the script tag is present in index.html" test is not optional.
- Gate on `ShowCrossLinks && InjectClientScript`, read per request so toggling needs no restart.
- Idempotency marker: the substring `/SpecialToMovie/ClientScript`.
- Insert before the **last** `</body>`.

### 6.4 Does this require Jellyfin Enhanced?

**No.** JE is a *pattern source*, not a runtime dependency, at any layer.

| Installed | Result |
| --- | --- |
| Phase 1 only | Plain text link, styled like the stock IMDb/TMDB links, opens a new tab |
| Phase 2, no JE | Icon button, in-app SPA navigation |
| Phase 2 + JE | Same icon button, visually consistent with JE's Letterboxd / arr buttons |

JE does not restyle third-party links — it injects *its own* buttons into the same
`.itemExternalLinks` container. "Look good alongside JE" therefore means adopting JE's conventions
(the `button-link emby-button` class shape, the `::before` icon technique, the `data-*` habit) so
that both sets of buttons read as one design language. Nothing is contributed to JE and nothing is
consumed from it.

The one genuinely JE-dependent item is the two-`IStartupFilter` nesting question in
[§11](#11-risks).

---

## 7. Phase 2 alternative considered and rejected

Writing the icon CSS into `BrandingOptions.CustomCss` via `IServerConfigurationManager` would give a
styled button with no script injection and no anonymous endpoint. Rejected as a default because it
mutates a shared, user-owned server setting the plugin does not own, and any managed-block marker
scheme has to survive the user editing around it. It is offered instead as a documented snippet users
paste themselves ([§8](#8-phase-3--surface-and-docs)) — same visual result, zero plugin-side risk.
Note that it only styles: it cannot remove `target="_blank"`, so in-app navigation remains
script-only.

---

## 8. Phase 3 — surface and docs

**[`Configuration/configPage.html`](../../Configuration/configPage.html)** — a new
`<div class="verticalSection">` titled **Detail page links**, placed after *General*, following the
existing markup conventions exactly (`checkboxContainer checkboxContainer-withDescription`,
`emby-checkbox-label`, `fieldDescription`, `inputContainer` + `emby-input` for the label fields):

- `#chkShowCrossLinks` → `ShowCrossLinks`
- `#txtMovieLinkLabel` → `MovieLinkLabel` (placeholder `Movie Version`)
- `#txtSpecialLinkLabel` → `SpecialLinkLabel` (placeholder `TV Special`)
- `#chkInjectClientScript` → `InjectClientScript`, with a description stating explicitly that it is
  on by default, that it rewrites the served `index.html` in memory, and that turning it off leaves
  working links that open in a new tab
- `#selectCrossLinkUrlStyle` → `CrossLinkUrlStyle`, a two-option dropdown whose description
  states plainly that `Relative` is right for browsers and `Absolute` is for phone / tablet / TV apps

Wire each into both `loadConfig` (read, with `!= null` fallbacks as `MetadataCacheDays` uses) and
`saveConfig` (`.trim()` the label inputs, as the API-key fields do). Grey out the label inputs and
the script toggle when the master toggle is off, following the existing
`updateTwoWayState` / `twoWayDeletionContainer` pattern.

**[`README.md`](../../README.md)** — a feature section describing the buttons, the new config
options, the new-tab caveat when script injection is off, the non-web-client caveat, and the
copy-paste CSS snippet from [§7](#7-phase-2-alternative-considered-and-rejected).

**[`AUDIT.md`](../AUDIT.md)** — see [§9](#9-audit-notes-to-record-before-release).

---

## 9. Audit notes to record before release

The pre-release audit is mandatory; these are the items this feature adds to it.

1. **New unauthenticated endpoint** `/SpecialToMovie/ClientScript` — the plugin's first
   `[AllowAnonymous]` route. Confirm it serves a fixed embedded asset with no request input reflected
   into the response.
2. **Unescaped `href` in jellyfin-web** — record as a standing constraint that `ExternalUrl.Url` is
   interpolated raw, so only GUIDs and the system ID may appear in it. Configurable labels flow
   through `ExternalUrl.Name`, which **is** escaped.
3. **Request-derived host under `Absolute`** — with `EnablePublishedServerUriByRequest` enabled
   server-side, `GetSmartApiUrl(HttpRequest)` takes the host from the request's `Host` header, so the
   emitted URL is no longer built purely from server-side values. The impact is confined to the
   requesting client's own response (a client can only mislead itself), and the `Uri.TryCreate`
   http/https validation in [§5.5](#55-absolute-url-mode) is the mitigation. Record it as an
   understood, bounded risk of the non-default mode.
4. **Response-body rewriting middleware** — runs on every `GET` matching the index paths. Confirm the
   non-`GET` short-circuit, the rethrow-on-downstream-failure path, the header fixes
   (`Content-Length` set; `ETag` / `Last-Modified` / `Accept-Ranges` removed), and that it is inert
   when either flag is off. **This ships on by default** ([D5](#12-decisions-taken)), so it is the
   highest-exposure item in this release: every web client of every user gets the rewritten
   `index.html`. Re-read the failure paths specifically for "does this serve the original HTML", not
   just "does this work".
5. **Visibility leak — accepted risk** ([D3](#12-decisions-taken)). The provider has no user context,
   so the button renders even for a user whose library permissions exclude the target; clicking lands
   on an error page. The disclosure is an opaque item GUID plus a fixed caption, to an already
   authenticated user. Record it in `AUDIT.md` as accepted, with the reasoning, and note it in the
   README rather than silently carrying it.
6. **Provider construction failure fails the plugin** — confirm all three constructors
   (`CrossLinkUrlResolver` included) only assign fields.
7. **DTO-path exception safety** — confirm `GetExternalUrls` is not a lazy iterator
   ([§5.2](#52-exception-safety-non-obvious)).
8. **`PairStore` index maintenance** — confirm eviction goes through the `_indexedKeys` record and
   **not** through the incoming pair's current field values, which are already mutated by the time
   `Upsert` runs ([§5.4](#54-pairstore-indexes-in-scope-for-this-change)). A stale index entry is a
   correctness bug in deletion handling, not just in this feature.
9. **PII sweep** — the usual full sweep; pay attention to the new JS file (console log prefixes) and
   the new config descriptions.

---

## 10. Testing checklist

- [ ] A paired episode page shows the movie button; the paired movie page shows the special button;
      each navigates to the correct item.
- [ ] Nothing renders for unpaired items, `DryRun` / `Pending` / `Error` pairs, or pairs whose
      counterpart has been deleted out of band.
- [ ] `IsExistingMovie` pairs **do** show the button.
- [ ] Works behind a reverse proxy and with a configured base URL.
- [ ] Renders in the TV layout (expected, per
      [§3.5](#35-how-the-web-client-renders-it--verified-verbatim)).
- [ ] Toggling `ShowCrossLinks` takes effect without a server restart.
- [ ] Renaming a label takes effect without a restart.
- [ ] Phase 2: no new tab; the icon renders; the upgrade survives navigating away and back; no
      duplicate anchors after ten navigations; the observer does not thrash (profile one detail page).
- [ ] Phase 2: with `InjectClientScript` off, `index.html` is byte-identical to the unmodified file
      (the escape hatch for an on-by-default feature — verify it before release, not after).
- [ ] Phase 2: on a fresh install with no configuration touched at all, the icon button appears and
      navigates in-app — i.e. the default path actually delivers the goal in [§1](#1-goal).
- [ ] Coexists with Jellyfin Enhanced — both scripts land, buttons look consistent, no duplicate
      injection — **tested in both install orders**.
- [ ] Detail-page DTO latency unchanged with a large `pairs.json`.
- [ ] Non-web client (Android / TV): confirm what actually happens to the relative URL, and that
      `Absolute` produces a URL that client can follow, before any claim is made in the README.
- [ ] `Absolute` mode: URL is correct with a configured base URL (the base path appears exactly
      once), correct behind a reverse proxy with a published server URL set, and falls back to the
      relative form when the mode is on but no request context exists.
- [ ] Switching `CrossLinkUrlStyle` takes effect without a server restart.
- [ ] `PairStore` indexes: after a pair is upserted with a changed `MovieItemId`,
      `GetByMovieId(oldId)` returns null and `GetByMovieId(newId)` returns the pair; after `Remove`
      both keys are gone; after a backup-restore `Load` both indexes are populated; unlinked
      (`null` / `Guid.Empty`) pairs do not collide.
- [ ] Existing deletion and cleanup flows still behave identically with the indexes in place —
      `CleanupTask.ValidatePair` and `LibraryEventHandler.OnItemRemoved` are the ones that lean on
      these lookups.
- [ ] `dotnet build -c Release` clean, per the standing build-before-handoff rule.

---

## 11. Risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| Non-web clients hand `ExternalUrls` to a browser intent; a relative URL is meaningless there — dead button | Medium | `CrossLinkUrlStyle = Absolute` ([§5.5](#55-absolute-url-mode)) is the escape hatch; document the default's limitation |
| `Absolute` mode returns a LAN address for a remote user, because with `EnablePublishedServerUriByRequest` off the URL derives from `Connection.RemoteIpAddress`, which behind a proxy is the proxy | Medium | Relative stays the default; the config description tells users absolute mode depends on their published-server-URL / forwarded-header setup |
| Stale `PairStore` index entry after an `Upsert` that changes `MovieItemId` | High if hit | Remove the replaced entry's old keys on every mutation path; debug assertion against the `List.Find` fallback ([§5.4](#54-pairstore-indexes-in-scope-for-this-change)) |
| Two `IStartupFilter`s buffering the same `index.html` (ours + JE) — each is idempotent by its own marker, but the nesting is untested | Medium | Must be verified on a server with JE installed, in both install orders, before release |
| A `MutationObserver` on `document.body` with `subtree:true` is a heavy hook | Low | Same shape and throttling as JE, which ships it widely; profile a detail page |
| An unhandled provider exception breaks the whole item-detail DTO | High if hit | Non-iterator `try/catch` wrapper ([§5.2](#52-exception-safety-non-obvious)) |
| A throwing provider constructor disables the plugin entirely | High if hit | Field-assignment-only constructors ([§3.2](#32-discovery-is-automatic-do-not-register-in-pluginserviceregistrator)) |
| `jellyfin-web` replaces the legacy item-detail controller with a React page | Low, future | Phase 1 is unaffected (server-side); Phase 2's selectors would need revisiting |
| Jellyfin 12.0.0 GA changes `IExternalUrlProvider` | Low | Already an accepted project-wide assumption for the rc4 pin; re-verify at the GA re-pin |

---

## 12. Decisions taken

Recorded 2026-08-21. These were the plan's open questions; each is now settled and the sections above
are written to match.

**D1 — Scope: all phases in one release.** Phases 1, 2 and 3 ship together, plus the `PairStore`
indexes. Phase 2 keeps its own `InjectClientScript` flag ([D5](#12-decisions-taken)), and the phases are still
built in order so Phase 1 can be released alone if the JE-coexistence test fails late.

**D2 — `CrossLinkUrlStyle` stays, implemented via `IHttpContextAccessor`.** `Relative` remains the
default; `Absolute` builds the URL from the live request with `GetSmartApiUrl(HttpRequest)`. The
accessor's availability is confirmed ([§3.7](#37-absolute-urls-must-come-from-the-request-not-from-config)),
the implementation and its failure paths are in [§5.5](#55-absolute-url-mode), and the remaining
verification is behavioural — what a real Android/TV client does with each form
([§10](#10-testing-checklist)).

**D3 — Visibility leak: accepted and documented.** No per-user check. The button may render for a
user who cannot see the target; clicking lands on an error page. Recorded in `AUDIT.md` as an
accepted risk with its reasoning, and noted in the README. Revisit only if a user reports it as a
real problem — [§3.10](#310-a-permission-aware-lookup-exists-relevant-to-the-visibility-leak)
records how it would be closed.

**D4 — `PairStore` indexes: in scope.** Delivered with this feature, with the index-maintenance
correctness requirements in [§5.4](#54-pairstore-indexes-in-scope-for-this-change).

**D5 — `InjectClientScript` defaults to on.** Raised by the plan audit: with Phase 2 shipping in the
same release ([D1](#12-decisions-taken)), an off-by-default flag would have meant the feature arrived
as a plain new-tab link, with the goal in [§1](#1-goal) unmet until the user found the toggle.
Resolved in favour of the on-by-default position:

| | Default on (chosen) | Default off (rejected) |
| --- | --- | --- |
| Out-of-box experience | Icon button, in-app navigation | Plain text link, new tab |
| `index.html` rewriting | Happens by default, with a toggle to stop it | Only if the user asks for it |
| Precedent | JE's flag is `DisableScriptInjectionMiddleware` — JE injects by default | — |
| Risk if the middleware misbehaves | Every user's web client | Confined to opt-in users |

The accepted cost is the bottom-right cell: a middleware bug reaches every web client rather than
only volunteers. Three things carry that weight, and all three are therefore **non-negotiable** in
review rather than nice-to-have:

1. every failure path in [§6.3](#63-servicesscriptinjectionstartupfiltercs) serves the original HTML
   untouched — the filter can only ever fail *open*;
2. non-`GET`, non-200 and non-HTML responses are never buffered at all;
3. the "with injection off, `index.html` is byte-identical" test in [§10](#10-testing-checklist)
   proves the escape hatch actually works before release.

### Still to verify during implementation

These are not decisions — they are checks that can only be done against a running server, and each is
already in [§10](#10-testing-checklist):

- what a non-web client actually does with each URL form;
- two `IStartupFilter`s (ours + JE) buffering the same `index.html`, in both install orders;
- `Absolute` mode's output behind a reverse proxy and under a configured base URL.
