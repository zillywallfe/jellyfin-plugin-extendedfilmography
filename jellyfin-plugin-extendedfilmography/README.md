# Publish this repository

Read [GITHUB-SETUP.md](GITHUB-SETUP.md) first. This package contains the same plugin runtime source as 1.0.1.0; only publishing files and owner metadata were updated. It does not fix or diagnose the disappearance after restarting with the feature unchecked.

# Extended Filmography

A Jellyfin server plugin that extends the person page beyond your library. Jellyfin's "Known For"
row is a query against your own items; this adds an actor's best-known films and series from TMDb
alongside them, so the page shows what the actor is actually famous for rather than only what you
happen to own.

It works in **every client**, native apps included, because the extra titles are added to the
standard item API response rather than drawn by a web-only overlay.

**It writes nothing to `library.db`, stores no images, and has no effect on library scans.**
Uninstalling removes every trace.

---

## How it works

Jellyfin has no hook for adding sections to a person page, and native clients such as Neptune
(Apple TV) have no plugin API at all. What every client does have in common is the HTTP API.

So the plugin registers an ASP.NET Core middleware — via `IPluginServiceRegistrator` plus an
`IStartupFilter`, the supported route in — at the front of Jellyfin's pipeline, and:

| Request | What happens |
|---|---|
| `GET /Items?personIds=…` | The response JSON is parsed and up to the configured number of TMDb titles are appended to `Items`, with `TotalRecordCount` adjusted to match |
| `GET /Items/{id}` for an appended title | Answered from the plugin's cache with a full item object: synopsis, year, rating, genres, request links |
| `GET /Items/{id}/Images/Primary` | Redirected (302) to `image.tmdb.org`, so no image is ever stored |
| `Seasons`, `Episodes`, `Similar`, `SpecialFeatures`, `parentId=…` | Answered with an empty result, so clients that go looking don't get an error |
| `PlaybackInfo`, `/Videos/{id}/…` | Answered `404` immediately, so nothing hangs trying to play a title you don't have |

Appended items are marked `LocationType: Virtual` — the same marker Jellyfin already uses for
missing episodes — so clients that grey those out grey these out too, and don't offer a play
button.

Item ids are derived from the TMDb id by a reversible encoding, so the plugin holds no id table
and needs no database of its own. The only thing on disk is a small JSON file per person you have
actually browsed, in the server's **cache** directory, roughly 4 KB each.

### What this cannot do

**It cannot create a separate, labelled section.** A client renders the rows it was built to
render; the API has no way to say "and here is a second row called Also Known For". The extra
titles therefore appear inside the existing Known For row, after your own items. A genuinely
separate section is only possible in the web client, by injecting JavaScript into `jellyfin-web`
— which by definition would not work in Neptune.

If a separate row matters more than Neptune support, that's a different plugin, and it's worth
saying so up front rather than discovering it after installing this one.

---

## Requirements

- Jellyfin **10.11.x** (see [Targeting another Jellyfin version](#targeting-another-jellyfin-version))
- Docker, or a .NET **9** SDK — 10.11 builds against .NET 9
- A free TMDb API key (v3), from your TMDb account under **Settings → API**
- Your people need TMDb ids, which Jellyfin's built-in TMDb metadata provider already sets. People
  without one are skipped silently.

---

## Install / upgrade

Use [UPGRADE.md](UPGRADE.md) for the complete ThinkPad instructions, Windows SCP command, automated backup and installation, verification, and old-source cleanup. The installer targets the known host bind mount at `~/plex_config/jellyfin/data/plugins`, uses .NET 9, and runs the regression harness before stopping Jellyfin. Build outside all Jellyfin plugins directories.

## Version support

This update pins Jellyfin.Controller and Jellyfin.Model to 10.11.11, targets .NET 9, and declares minimum ABI 10.11.11.0. It is intended for your Jellyfin 10.11.11 server. Other server lines need a separate compatibility review.

---

## Verification

The repo ships an offline harness that needs no packages, no network and no Jellyfin server:

```bash
cd verify
dotnet run -c Release
```

It runs regression checks covering id encoding and collision-resistance, the ranking and filtering
rules, route matching, JSON injection (paging, limits, type filters, idempotency), gzip/brotli/
deflate round-tripping, the on-disk cache, and — over a real HTTP pipeline with response
compression enabled — the middleware end to end. CI runs it before every build.

Once installed, check the wire format directly before looking at any UI. Find an actor's id from
their page URL, then:

```bash
JF=http://localhost:8096
KEY=<your jellyfin api key>
PID=<the person's item id>

# Extra titles present, marked Virtual?
curl -s "$JF/Items?personIds=$PID&recursive=true&api_key=$KEY" \
  | jq '.Items[] | {Name, Type, LocationType}'

# Detail page for one of them
SID=$(curl -s "$JF/Items?personIds=$PID&recursive=true&api_key=$KEY" \
  | jq -r '.Items[] | select(.LocationType=="Virtual") | .Id' | head -1)
curl -s "$JF/Items/$SID?api_key=$KEY" | jq '{Name, ProductionYear, Overview, ExternalUrls}'

# Poster redirects to TMDb
curl -sI "$JF/Items/$SID/Images/Primary?api_key=$KEY" | head -3
```

Then confirm nothing else moved: `library.db` unchanged in size, search unchanged, Recently Added
unchanged, scan time unchanged.

---

## Settings

| Setting | Default | Notes |
|---|---|---|
| Enable | on | Kill switch; restores stock behaviour without uninstalling |
| TMDb API key | — | Required |
| Metadata language | `en-US` | e.g. `sv-SE`, `nl-NL` |
| Titles per person | 50 | |
| Popularity weight / Rating weight | 0.6 / 0.4 | Set 0 / 1 for a strictly highest-rated list |
| Minimum TMDb votes | 10 | Keeps a 10.0 with three votes out |
| Billing cutoff | 0 | No billing limit; a positive value restricts billed cast |
| Minimum episodes for unbilled TV credits | 1 | TMDb often omits billing on television |
| Include films / series | on / on | |
| Acting credits only | on | Off also includes directing, writing, producing |
| Drop "as Self" appearances | on | |
| Drop talk shows, news, reality | on | |
| Hide titles already in the library | on | Avoids repeating the native row |
| Hide unreleased titles | on | |
| Append a marker to external titles | off | For clients that don't grey virtual items |
| Jellyseerr / Overseerr URL | — | Adds a request link to every external title |
| Poster delivery | Redirect | Switch to Proxy only if a client won't follow a 302 |
| Cache lifetime | 30 days | |
| Lookup budget | 4000 ms | See below |

Changing anything that affects *selection* invalidates cached results automatically. Changing
presentation-only settings does not, so you won't lose weeks of cache to a cosmetic tweak.

---

## Notes and troubleshooting

**First visit to a person may show nothing.** A cold TMDb lookup is given a 4-second budget. If it
runs over, the page renders normally and the lookup finishes in the background — reload and the
titles are there. Raise the budget if your server's connection is slow.

**Nothing appears at all.** Check, in order: the API key is saved; the person has a TMDb id
(`curl "$JF/Items/$PID?api_key=$KEY" | jq .ProviderIds`); the filters aren't excluding everything
(try dropping the billing cutoff to 0 and the vote floor to 0). Turn on Verbose logging to see how
many credits survived ranking.

**Posters are missing in one client.** That client isn't following the 302. Switch **Poster
delivery** to Proxy.

**A client shows your own items but no extras, and sends a `limit`.** If the client's page came
back exactly full, the plugin steps aside rather than shifting that client's paging window. This
only bites when an actor has as many library items as the client's page size.

**It stopped working after a server upgrade.** The plugin rewrites API responses, so a change to
the `/Items` response shape upstream can break it. Every rewrite is wrapped so that a failure
falls back to the unmodified response — the feature stops, the server doesn't. Check the log for
`Failed to extend person`.

**Uninstalling.** Delete the plugin folder and restart. Optionally delete
`config/cache/extended-filmography/`. There is nothing else to clean up.

---

## Licence

MIT. See `LICENSE`. Swap it for GPL-3.0 if you'd rather match the official Jellyfin plugins.

Film and television metadata comes from [TMDb](https://www.themoviedb.org/). This plugin is not
endorsed or certified by TMDb.
