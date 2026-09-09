using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Abstractions;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Middleware;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Jellyfin.Plugin.ExtendedFilmography.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Verify;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        UpgradeRegressionTests();

        Section("SyntheticId");
        SyntheticIdTests();

        Section("CreditRanker");
        CreditRankerTests();

        Section("DtoBuilder");
        DtoBuilderTests();

        Section("TitleConverter");
        TitleConverterTests();

        Section("RouteMatcher");
        RouteMatcherTests();

        Section("ItemsResponseInjector");
        InjectorTests();

        Section("ResponseCodec");
        await ResponseCodecTests().ConfigureAwait(false);

        Section("Image sizing");
        ImageSizeTests();

        Section("Cache");
        await CacheTests().ConfigureAwait(false);

        Section("Settings stamp");
        SettingsStampTests();

        Section("End-to-end pipeline");
        await EndToEndTests().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"passed {_passed}, failed {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- SyntheticId

    private static void SyntheticIdTests()
    {
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            foreach (var tmdbId in new[] { 1, 2, 603, 1396, 99861, 2147483647 })
            {
                var id = SyntheticId.Create(kind, tmdbId);
                Check(
                    SyntheticId.TryDecode(id, out var backKind, out var backId) && backKind == kind && backId == tmdbId,
                    $"round-trips {kind}/{tmdbId}");
            }
        }

        Check(
            SyntheticId.Create(MediaKind.Movie, 603) == SyntheticId.Create(MediaKind.Movie, 603),
            "is deterministic across calls");

        Check(
            SyntheticId.Create(MediaKind.Movie, 603) != SyntheticId.Create(MediaKind.Series, 603),
            "separates movie and series with the same TMDb id");

        var seen = new HashSet<Guid>();
        var collisions = 0;
        for (var i = 1; i <= 50_000; i++)
        {
            if (!seen.Add(SyntheticId.Create(MediaKind.Movie, i)))
            {
                collisions++;
            }
        }

        Check(collisions == 0, "produces no collisions across 50k ids");

        var falsePositives = 0;
        for (var i = 0; i < 20_000; i++)
        {
            if (SyntheticId.IsSynthetic(Guid.NewGuid()))
            {
                falsePositives++;
            }
        }

        Check(falsePositives == 0, "never mistakes a random GUID for one of ours");

        Check(!SyntheticId.IsSynthetic(Guid.Empty), "rejects the empty GUID");

        var api = SyntheticId.ToApiString(SyntheticId.Create(MediaKind.Series, 1396));
        Check(api.Length == 32 && !api.Contains('-'), "renders ids the way the Jellyfin API does");
        Check(
            SyntheticId.TryParseAndDecode(api, out var pk, out var pid) && pk == MediaKind.Series && pid == 1396,
            "parses its own dashless form");
        Check(
            SyntheticId.TryParseAndDecode(SyntheticId.Create(MediaKind.Series, 1396).ToString("D"), out _, out _),
            "parses the dashed form too");
        Check(!SyntheticId.TryParseAndDecode("not-a-guid", out _, out _), "rejects junk");
    }

    private sealed class TestLibrary : MediaBrowser.Controller.Library.ILibraryManager
    {
        public readonly List<MediaBrowser.Controller.Entities.BaseItem> Items = new();
        public IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> GetItemList(
            MediaBrowser.Controller.Entities.InternalItemsQuery query)
        {
            // Model the missing provider metadata produced by minimal DTO options.
            return query.DtoOptions?.AllFields == true ? Items : Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
        }
        public MediaBrowser.Controller.Entities.BaseItem? GetItemById(Guid id) => null;
    }

    private static void UpgradeRegressionTests()
    {
        var config = new PluginConfiguration
        {
            MaxItems = 20, MinVoteCount = 100, MaxBillingOrder = 10,
            MinEpisodeCountWithoutOrder = 5, TmdbApiKey = "keep-key", Enabled = false,
        };
        Check(config.MigrateDefaults() && config.MaxItems == 50 && config.MinVoteCount == 10
              && config.MaxBillingOrder == 0 && config.MinEpisodeCountWithoutOrder == 1,
            "old defaults migrate to broader discovery");
        Check(!config.Enabled && config.TmdbApiKey == "keep-key", "migration preserves toggle and credentials");
        config.MaxItems = 20;
        Check(!config.MigrateDefaults() && config.MaxItems == 20, "migration runs only once");
        var custom = new PluginConfiguration { MaxItems = 37, MinVoteCount = 42 };
        custom.MigrateDefaults();
        Check(custom.MaxItems == 37 && custom.MinVoteCount == 42, "custom limits survive migration");
        var library = new TestLibrary();
        library.Items.Add(new MediaBrowser.Controller.Entities.Movies.Movie
        {
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
        });
        var index = new LibraryTmdbIndex(library,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryTmdbIndex>.Instance);
        Check(index.GetKeys().Contains(FilmographyEntry.BuildKey(MediaKind.Movie, 603)),
            "library query loads TMDb metadata for duplicate filtering");
        library.Items.Clear();
        index.Invalidate();
        Check(index.GetKeys().Count == 0, "library index refreshes after invalidation");
        var credits = new TmdbCombinedCredits();
        credits.Cast.Add(Credit(id: 603));
        credits.Cast.Add(Credit(id: 604));
        var broader = new PluginConfiguration { MaxItems = 1 };
        var candidates = CreditRanker.Rank(credits, broader, null, DateTime.UtcNow, limitResults: false);
        Check(candidates.Count == 2, "cache candidates are not truncated before library exclusion");
        var owned = new HashSet<string> { candidates[0].Key };
        var selected = CreditRanker.SelectForLibrary(candidates, owned, 1);
        Check(selected.Count == 1 && selected[0].Key == candidates[1].Key,
            "cached candidates exclude owned titles and backfill the display limit");
        owned.Add(candidates[1].Key);
        Check(CreditRanker.SelectForLibrary(candidates, owned, 1).Count == 0,
            "same cached credits respect an updated library snapshot");
        Check(CreditRanker.SelectForLibrary(candidates, null, 1).Count == 1,
            "turning off library exclusion restores cached candidates");
    }

    // ---------------------------------------------------------------- CreditRanker

    private static void CreditRankerTests()
    {
        var config = new PluginConfiguration
        {
            MaxItems = 20, MinVoteCount = 100, MaxBillingOrder = 10, MinEpisodeCountWithoutOrder = 5,
        };
        var now = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);

        Check(
            CreditRanker.Convert(Credit(id: 1, votes: 50), config, null, now) is null,
            "drops credits below the vote floor");

        Check(
            CreditRanker.Convert(Credit(id: 1, character: "Self"), config, null, now) is null,
            "drops 'Self' appearances");

        Check(
            CreditRanker.Convert(Credit(id: 1, character: "Self - Guest"), config, null, now) is null,
            "drops 'Self - Guest' appearances");

        Check(
            CreditRanker.Convert(Credit(id: 1, character: "Himself"), config, null, now) is null,
            "drops 'Himself' appearances");

        Check(
            CreditRanker.Convert(Credit(id: 1, character: "Selfridge"), config, null, now) is not null,
            "does not mistake 'Selfridge' for a self appearance");

        Check(
            CreditRanker.Convert(Credit(id: 1, genreIds: new List<int> { TmdbGenres.Talk }), config, null, now) is null,
            "drops talk shows");

        Check(
            CreditRanker.Convert(Credit(id: 1, order: 25), config, null, now) is null,
            "drops credits billed below the cutoff");

        Check(
            CreditRanker.Convert(Credit(id: 1, order: 9), config, null, now) is not null,
            "keeps the tenth-billed credit");

        Check(
            CreditRanker.Convert(Credit(id: 1, order: null, mediaType: "tv", episodeCount: 1), config, null, now) is null,
            "drops a one-episode TV guest spot with no billing order");

        Check(
            CreditRanker.Convert(Credit(id: 1, order: null, mediaType: "tv", episodeCount: 40), config, null, now) is not null,
            "keeps a TV regular with no billing order");

        Check(
            CreditRanker.Convert(Credit(id: 1, date: "2030-01-01"), config, null, now) is null,
            "drops unreleased titles");

        Check(
            CreditRanker.Convert(Credit(id: 1, adult: true), config, null, now) is null,
            "drops adult titles");

        var libraryKeys = new HashSet<string> { FilmographyEntry.BuildKey(MediaKind.Movie, 1) };
        Check(
            CreditRanker.Convert(Credit(id: 1), config, libraryKeys, now) is null,
            "drops titles already in the library");
        Check(
            CreditRanker.Convert(Credit(id: 2), config, libraryKeys, now) is not null,
            "keeps titles not in the library");

        var noHide = new PluginConfiguration { HideItemsAlreadyInLibrary = false };
        Check(
            CreditRanker.Convert(Credit(id: 1), noHide, libraryKeys, now) is not null,
            "keeps library titles when the setting is off");

        Check(
            CreditRanker.Convert(Credit(id: 1, mediaType: "tv", order: 1), new PluginConfiguration { IncludeSeries = false }, null, now) is null,
            "honours IncludeSeries = false");

        // Scoring: a hugely popular, well-rated film beats an obscure one.
        var blockbuster = Credit(id: 10, popularity: 900, voteAverage: 8.5);
        var obscure = Credit(id: 11, popularity: 3, voteAverage: 6.0);
        Check(
            CreditRanker.Score(blockbuster, config) > CreditRanker.Score(obscure, config),
            "ranks a popular, well-rated title above an obscure one");

        Check(
            CreditRanker.Score(Credit(id: 12, popularity: 0, voteAverage: 0), config) >= 0
            && CreditRanker.Score(Credit(id: 13, popularity: 1e9, voteAverage: 10), config) <= 1.0,
            "keeps scores inside 0..1");

        var ratingOnly = new PluginConfiguration { PopularityWeight = 0, RatingWeight = 1 };
        Check(
            CreditRanker.Score(Credit(id: 14, popularity: 1, voteAverage: 9), ratingOnly)
            > CreditRanker.Score(Credit(id: 15, popularity: 900, voteAverage: 5), ratingOnly),
            "honours a rating-only weighting");

        // Dedupe + ordering + cap.
        var credits = new TmdbCombinedCredits();
        credits.Cast.Add(Credit(id: 100, popularity: 10, voteAverage: 7.0, character: "Bit Part"));
        credits.Cast.Add(Credit(id: 100, popularity: 10, voteAverage: 7.0, character: "Lead"));
        for (var i = 0; i < 40; i++)
        {
            credits.Cast.Add(Credit(id: 200 + i, popularity: i + 1, voteAverage: 7.0));
        }

        var ranked = CreditRanker.Rank(credits, config, null, now);
        Check(ranked.Count == 20, "caps the result at MaxItems");
        Check(ranked.Select(e => e.Key).Distinct().Count() == ranked.Count, "returns each title once");
        Check(
            ranked.Zip(ranked.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score),
            "returns entries in descending score order");

        var crewConfig = new PluginConfiguration { ActingCreditsOnly = false };
        var withCrew = new TmdbCombinedCredits();
        withCrew.Crew.Add(Credit(id: 300, character: null, job: "Director", order: null));
        Check(
            CreditRanker.Rank(withCrew, crewConfig, null, now).Count == 1,
            "includes crew credits when acting-only is off");
        Check(
            CreditRanker.Rank(withCrew, config, null, now).Count == 0,
            "excludes crew credits by default");
    }

    // ---------------------------------------------------------------- DtoBuilder

    private static void DtoBuilderTests()
    {
        var config = new PluginConfiguration { SeerrBaseUrl = "https://requests.example.com/" };
        var movie = Entry(MediaKind.Movie, 603, "The Matrix");
        var dto = DtoBuilder.Build(movie, config, "server-1");

        Check(dto["Type"]!.GetValue<string>() == "Movie", "types a film as Movie");
        Check(dto["LocationType"]!.GetValue<string>() == "Virtual", "marks the item Virtual");
        Check(dto["PlayAccess"]!.GetValue<string>() == "None", "denies playback");
        Check(dto["IsFolder"]!.GetValue<bool>() == false, "a film is not a folder");
        Check(dto["MediaType"]!.GetValue<string>() == "Video", "a film has MediaType Video");
        Check(dto["ServerId"]!.GetValue<string>() == "server-1", "carries the server id");
        Check(dto["ProviderIds"]!["Tmdb"]!.GetValue<string>() == "603", "carries the TMDb provider id");
        Check(dto["ImageTags"]!["Primary"] is not null, "advertises a primary image");
        Check(dto["ProductionYear"]!.GetValue<int>() == 1999, "derives the production year");
        Check(((JsonArray)dto["Tags"]!).Count == 1, "is tagged");
        Check(dto["MediaSources"] is JsonArray { Count: 0 }, "has no media sources");

        var urls = (JsonArray)dto["ExternalUrls"]!;
        Check(urls.Count == 2, "carries both external links");
        Check(
            urls[0]!["Url"]!.GetValue<string>() == "https://requests.example.com/movie/603",
            "builds the Jellyseerr request URL and strips the trailing slash");
        Check(
            urls[1]!["Url"]!.GetValue<string>() == "https://www.themoviedb.org/movie/603",
            "builds the TMDb URL");

        var series = DtoBuilder.Build(Entry(MediaKind.Series, 1396, "Breaking Bad"), config, "server-1");
        Check(series["Type"]!.GetValue<string>() == "Series", "types a show as Series");
        Check(series["IsFolder"]!.GetValue<bool>(), "a series is a folder");
        Check(series["MediaType"]!.GetValue<string>() == "Unknown", "a series has MediaType Unknown");
        Check(
            ((JsonArray)series["ExternalUrls"]!)[0]!["Url"]!.GetValue<string>() == "https://requests.example.com/tv/1396",
            "uses the tv segment for series request URLs");

        var noSeerr = DtoBuilder.Build(movie, new PluginConfiguration(), "server-1");
        Check(((JsonArray)noSeerr["ExternalUrls"]!).Count == 1, "omits the request link when no Seerr URL is set");

        var marked = DtoBuilder.Build(movie, new PluginConfiguration { MarkExternalTitles = true }, "s");
        Check(
            marked["Name"]!.GetValue<string>() == "The Matrix (not in library)",
            "appends the marker suffix when asked");

        var id = dto["Id"]!.GetValue<string>();
        Check(
            SyntheticId.TryParseAndDecode(id, out var k, out var t) && k == MediaKind.Movie && t == 603,
            "emits an id the middleware can decode");
    }

    // ---------------------------------------------------------------- TitleConverter

    private static void TitleConverterTests()
    {
        var movie = new TmdbTitle
        {
            Id = 603,
            Title = "The Matrix",
            OriginalTitle = "The Matrix",
            Overview = "A hacker learns the truth.",
            PosterPath = "/p.jpg",
            BackdropPath = "/b.jpg",
            ReleaseDate = "1999-03-30",
            VoteAverage = 8.223,
            VoteCount = 25000,
            Popularity = 90,
            Genres = new List<TmdbGenre>
            {
                new() { Id = 28, Name = "Action" },
                new() { Id = 878, Name = "Science Fiction" },
                new() { Id = 28, Name = "Action" },
            },
        };

        var entry = TitleConverter.Convert(movie, MediaKind.Movie);
        Check(entry is not null, "converts a film");
        Check(entry!.Name == "The Matrix", "keeps the title");
        Check(entry.OriginalTitle is null, "drops a redundant original title");
        Check(entry.PremiereDate == new DateTime(1999, 3, 30, 0, 0, 0, DateTimeKind.Utc), "parses the release date");
        Check(Math.Abs(entry.CommunityRating!.Value - 8.2) < 0.001, "rounds the rating");
        Check(entry.Genres.Count == 2, "de-duplicates genres");

        var series = TitleConverter.Convert(
            new TmdbTitle { Id = 1396, SeriesName = "Breaking Bad", FirstAirDate = "2008-01-20" },
            MediaKind.Series);
        Check(series!.Name == "Breaking Bad", "reads the series name field");
        Check(series.PremiereDate!.Value.Year == 2008, "reads the first-air date field");

        Check(TitleConverter.Convert(null, MediaKind.Movie) is null, "handles a null payload");
        Check(TitleConverter.Convert(new TmdbTitle { Id = 0 }, MediaKind.Movie) is null, "rejects a missing id");
        Check(TitleConverter.Convert(new TmdbTitle { Id = 5 }, MediaKind.Movie) is null, "rejects a missing title");
        Check(
            TitleConverter.Convert(new TmdbTitle { Id = 5, Title = "X", ReleaseDate = "not a date" }, MediaKind.Movie)!
                .PremiereDate is null,
            "tolerates an unparseable date");
    }

    // ---------------------------------------------------------------- RouteMatcher

    private static void RouteMatcherTests()
    {
        var personId = Guid.NewGuid();
        var synth = SyntheticId.ToApiString(SyntheticId.Create(MediaKind.Movie, 603));
        var real = Guid.NewGuid().ToString("N");

        Check(
            Match($"/Items", $"personIds={personId:N}").Kind == RouteKind.PersonItems,
            "matches the person query");
        Check(
            Match($"/Users/{Guid.NewGuid():N}/Items", $"personIds={personId:N}").Kind == RouteKind.PersonItems,
            "matches the user-scoped person query");
        Check(
            Match($"/jellyfin/Items", $"personIds={personId:N}").Kind == RouteKind.PersonItems,
            "matches behind a base path");
        Check(
            Match("/Items", $"PersonIds={personId:N}").Kind == RouteKind.PersonItems,
            "is case-insensitive about the query parameter");
        Check(
            Match("/Items", $"personIds={personId:N}").PersonId == personId,
            "extracts the person id");
        Check(
            Match("/Items", $"personIds={personId:N},{Guid.NewGuid():N}").Kind == RouteKind.None,
            "ignores multi-person queries");
        Check(Match("/Items", "recursive=true").Kind == RouteKind.None, "ignores ordinary item queries");
        Check(Match("/Items/Filters", string.Empty).Kind == RouteKind.None, "ignores /Items/Filters");
        Check(Match("/Users/x/Items/Resume", string.Empty).Kind == RouteKind.None, "ignores the resume row");
        Check(Match($"/Items/{real}", string.Empty).Kind == RouteKind.None, "ignores real item ids");
        Check(Match("/System/Info", string.Empty).Kind == RouteKind.None, "ignores unrelated routes");
        Check(Match("/", string.Empty).Kind == RouteKind.None, "ignores the root");

        Check(Match($"/Items/{synth}", string.Empty).Kind == RouteKind.SyntheticItem, "serves synthetic detail pages");
        Check(
            Match($"/Users/{Guid.NewGuid():N}/Items/{synth}", string.Empty).Kind == RouteKind.SyntheticItem,
            "serves user-scoped synthetic detail pages");
        Check(
            Match($"/Items/{synth}", string.Empty).TmdbId == 603,
            "decodes the TMDb id from the path");

        Check(Match($"/Items/{synth}/Images/Primary", string.Empty).Kind == RouteKind.SyntheticImage, "serves posters");
        Check(
            Match($"/Items/{synth}/Images/Primary/0", string.Empty).Kind == RouteKind.SyntheticImage,
            "serves indexed posters");
        Check(
            Match($"/Items/{synth}/Images/Backdrop", string.Empty).ImageType == "Backdrop",
            "reports the image type");

        Check(Match($"/Items/{synth}/Similar", string.Empty).Kind == RouteKind.EmptyQueryResult, "empties Similar");
        Check(Match($"/Shows/{synth}/Seasons", string.Empty).Kind == RouteKind.EmptyQueryResult, "empties Seasons");
        Check(Match($"/Shows/{synth}/Episodes", string.Empty).Kind == RouteKind.EmptyQueryResult, "empties Episodes");
        Check(Match("/Items", $"parentId={synth}").Kind == RouteKind.EmptyQueryResult, "empties child queries");
        Check(
            Match($"/Items/{synth}/SpecialFeatures", string.Empty).Kind == RouteKind.EmptyArray,
            "empties SpecialFeatures");

        Check(Match($"/Items/{synth}/PlaybackInfo", string.Empty).Kind == RouteKind.NotFound, "refuses PlaybackInfo");
        Check(Match($"/Videos/{synth}/stream.mp4", string.Empty).Kind == RouteKind.NotFound, "refuses streaming");
        Check(Match($"/Items/{synth}/ThemeMedia", string.Empty).Kind == RouteKind.NotFound, "refuses theme media");
        Check(Match($"/Shows/{synth}/NextUp", string.Empty).Kind == RouteKind.NotFound, "refuses unknown Shows routes");

        Check(RouteMatcher.GetIntParam(Query("limit=25"), "limit") == 25, "parses integer parameters");
        Check(RouteMatcher.GetIntParam(Query("limit=abc"), "limit") is null, "ignores unparseable integers");
        Check(RouteMatcher.GetIntParam(Query(string.Empty), "limit") is null, "ignores missing integers");
    }

    // ---------------------------------------------------------------- Injector

    private static void InjectorTests()
    {
        var config = new PluginConfiguration();
        var entries = new List<FilmographyEntry>
        {
            Entry(MediaKind.Movie, 603, "The Matrix"),
            Entry(MediaKind.Series, 1396, "Breaking Bad"),
            Entry(MediaKind.Movie, 27205, "Inception"),
        };

        var body = Body(2);

        var ok = ItemsResponseInjector.TryInject(body, entries, config, "s", new InjectionLimits(null, null, null), out var result, out var added);
        Check(ok && added == 3, "appends every entry");

        var parsed = JsonNode.Parse(result)!.AsObject();
        Check(((JsonArray)parsed["Items"]!).Count == 5, "grows the Items array");
        Check(parsed["TotalRecordCount"]!.GetValue<int>() == 5, "bumps TotalRecordCount");
        Check(
            ((JsonArray)parsed["Items"]!)[0]!["Name"]!.GetValue<string>() == "Real 0",
            "leaves the library's own items first");
        Check(
            ((JsonArray)parsed["Items"]!)[2]!["Name"]!.GetValue<string>() == "The Matrix",
            "appends in rank order");

        Check(
            !ItemsResponseInjector.TryInject(body, entries, config, "s", new InjectionLimits(20, null, null), out _, out _),
            "skips when the client is paging");

        Check(
            !ItemsResponseInjector.TryInject(Body(25), entries, config, "s", new InjectionLimits(null, 25, null), out _, out _),
            "skips when the page came back full");

        ItemsResponseInjector.TryInject(Body(23), entries, config, "s", new InjectionLimits(null, 25, null), out var capped, out var cappedAdded);
        Check(cappedAdded == 2, "respects the remaining page allowance");
        Check(((JsonArray)JsonNode.Parse(capped)!["Items"]!).Count == 25, "never overflows the page");

        ItemsResponseInjector.TryInject(
            body,
            entries,
            config,
            "s",
            new InjectionLimits(null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Movie" }),
            out var moviesOnly,
            out var movieAdded);
        Check(movieAdded == 2, "honours includeItemTypes");
        Check(
            ((JsonArray)JsonNode.Parse(moviesOnly)!["Items"]!)
                .Skip(2)
                .All(n => n!["Type"]!.GetValue<string>() == "Movie"),
            "injects only the requested types");

        // Idempotency: running over an already-extended body must not duplicate.
        Check(
            !ItemsResponseInjector.TryInject(result, entries, config, "s", new InjectionLimits(null, null, null), out _, out _),
            "does not duplicate items on a second pass");

        Check(
            !ItemsResponseInjector.TryInject(body, new List<FilmographyEntry>(), config, "s", new InjectionLimits(null, null, null), out _, out _),
            "does nothing when there is nothing to add");

        Check(
            !ItemsResponseInjector.TryInject(Encoding.UTF8.GetBytes("not json"), entries, config, "s", new InjectionLimits(null, null, null), out _, out _),
            "refuses a body it cannot parse");

        Check(
            !ItemsResponseInjector.TryInject(Encoding.UTF8.GetBytes("{\"Foo\":1}"), entries, config, "s", new InjectionLimits(null, null, null), out _, out _),
            "refuses a body with no Items array");

        Check(
            !ItemsResponseInjector.TryInject(Encoding.UTF8.GetBytes("[]"), entries, config, "s", new InjectionLimits(null, null, null), out _, out _),
            "refuses a bare array");
    }

    // ---------------------------------------------------------------- Codec

    private static async Task ResponseCodecTests()
    {
        Check(ResponseCodec.Parse(null) == BodyEncoding.Identity, "treats a missing header as identity");
        Check(ResponseCodec.Parse("gzip") == BodyEncoding.Gzip, "parses gzip");
        Check(ResponseCodec.Parse("br") == BodyEncoding.Brotli, "parses brotli");
        Check(ResponseCodec.Parse("deflate") == BodyEncoding.Deflate, "parses deflate");
        Check(ResponseCodec.Parse("zstd") == BodyEncoding.Unsupported, "refuses unknown encodings");
        Check(ResponseCodec.Parse("gzip, br") == BodyEncoding.Unsupported, "refuses stacked encodings");

        var payload = Encoding.UTF8.GetBytes(new string('x', 5000) + "{\"Items\":[]}");
        foreach (var encoding in new[] { BodyEncoding.Identity, BodyEncoding.Gzip, BodyEncoding.Brotli, BodyEncoding.Deflate })
        {
            var encoded = await ResponseCodec.EncodeAsync(payload, encoding, CancellationToken.None).ConfigureAwait(false);
            var decoded = await ResponseCodec.DecodeAsync(encoded, encoding, CancellationToken.None).ConfigureAwait(false);
            Check(decoded.SequenceEqual(payload), $"round-trips {encoding}");
            if (encoding != BodyEncoding.Identity)
            {
                Check(encoded.Length < payload.Length, $"actually compresses with {encoding}");
            }
        }
    }

    // ---------------------------------------------------------------- Image sizing

    private static void ImageSizeTests()
    {
        Check(
            ExtendedFilmographyMiddleware.SelectImageSize(Query(string.Empty), "Primary") == "w500",
            "defaults posters to w500");
        Check(
            ExtendedFilmographyMiddleware.SelectImageSize(Query("fillWidth=150"), "Primary") == "w185",
            "serves a small poster for a small request");
        Check(
            ExtendedFilmographyMiddleware.SelectImageSize(Query("maxWidth=1200"), "Primary") == "w780",
            "caps posters at w780");
        Check(
            ExtendedFilmographyMiddleware.SelectImageSize(Query("fillWidth=1000"), "Backdrop") == "w1280",
            "serves a large backdrop when asked");
        Check(
            ExtendedFilmographyMiddleware.SelectImageSize(Query("width=200"), "Primary") == "w342",
            "reads the plain width parameter");

        var entry = Entry(MediaKind.Movie, 603, "The Matrix");
        Check(
            ExtendedFilmographyMiddleware.SelectImagePath(entry, "Primary") == "/poster603.jpg",
            "maps Primary to the poster");
        Check(
            ExtendedFilmographyMiddleware.SelectImagePath(entry, "Backdrop") == "/backdrop603.jpg",
            "maps Backdrop to the backdrop");
        Check(
            ExtendedFilmographyMiddleware.SelectImagePath(entry, "Logo") is null,
            "has nothing to serve for a logo");
        Check(
            ExtendedFilmographyMiddleware.SelectImagePath(null, "Primary") is null,
            "handles an unknown item");
    }

    // ---------------------------------------------------------------- Cache

    private static async Task CacheTests()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xfilm-verify-" + Guid.NewGuid().ToString("N"));
        var cache = new Jellyfin.Plugin.ExtendedFilmography.Cache.FilmographyCache(
            dir,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        try
        {
            var entries = new List<FilmographyEntry> { Entry(MediaKind.Movie, 603, "The Matrix") };
            var ttl = TimeSpan.FromDays(30);

            Check(
                await cache.TryGetAsync(6384, "stamp", ttl, CancellationToken.None).ConfigureAwait(false) is null,
                "misses on an empty cache");

            await cache.SetAsync(6384, "stamp", entries, CancellationToken.None).ConfigureAwait(false);

            var hit = await cache.TryGetAsync(6384, "stamp", ttl, CancellationToken.None).ConfigureAwait(false);
            Check(hit is not null && hit.Count == 1 && hit[0].Name == "The Matrix", "reads back what it wrote");

            Check(
                await cache.TryGetAsync(6384, "different-stamp", ttl, CancellationToken.None).ConfigureAwait(false) is null,
                "invalidates when the settings stamp changes");

            Check(
                await cache.TryGetAsync(6384, "stamp", TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false) is null,
                "expires past the TTL");

            var usage = cache.Usage();
            Check(usage.Files == 1 && usage.Bytes > 0, "reports its disk usage");

            // Survives a restart: a fresh instance must read the file the old one wrote.
            var reopened = new Jellyfin.Plugin.ExtendedFilmography.Cache.FilmographyCache(
                dir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Check(
                await reopened.TryGetAsync(6384, "stamp", ttl, CancellationToken.None).ConfigureAwait(false) is not null,
                "survives a process restart");

            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, "999.json"), "{ broken")
                .ConfigureAwait(false);
            Check(
                await reopened.TryGetAsync(999, "stamp", ttl, CancellationToken.None).ConfigureAwait(false) is null,
                "tolerates a corrupt cache file");

            Check(cache.Clear() >= 1, "clears its files");
            Check(cache.Usage().Files == 0, "is empty after clearing");
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // Best effort.
            }
        }
    }

    // ---------------------------------------------------------------- Settings stamp

    private static void SettingsStampTests()
    {
        var baseline = Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
            .BuildSettingsStamp(new PluginConfiguration());

        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration()) == baseline,
            "is stable for identical settings");

        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration { MinVoteCount = 500 }) != baseline,
            "changes when a filter changes");

        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration { RatingWeight = 1.0 }) != baseline,
            "changes when a weight changes");

        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration { TmdbLanguage = "sv-SE" }) != baseline,
            "changes with the metadata language");

        // Presentation-only settings must NOT invalidate weeks of cached lookups.
        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration { MarkExternalTitles = true }) == baseline,
            "ignores presentation-only settings");

        Check(
            Jellyfin.Plugin.ExtendedFilmography.Services.FilmographyProvider
                .BuildSettingsStamp(new PluginConfiguration { SeerrBaseUrl = "https://x" }) == baseline,
            "ignores the Seerr URL");
    }

    // ---------------------------------------------------------------- End to end

    private static async Task EndToEndTests()
    {
        var personId = Guid.NewGuid();
        var provider = new FakeProvider(new PluginConfiguration { SeerrBaseUrl = "https://seerr.example.com" })
        {
            Entries = new List<FilmographyEntry>
            {
                Entry(MediaKind.Movie, 603, "The Matrix"),
                Entry(MediaKind.Series, 1396, "Breaking Bad"),
            },
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IFilmographyProvider>(provider);
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.MimeTypes = new[] { "application/json" };
        });

        var app = builder.Build();

        // Same order as in the server: our middleware runs first, so response compression
        // happens inside its call to next() - exactly the case it has to cope with.
        app.UseMiddleware<ExtendedFilmographyMiddleware>();
        app.UseResponseCompression();

        app.Run(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/Items") && ctx.Request.Query.ContainsKey("personIds"))
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.Body.WriteAsync(Body(2)).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 418;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("upstream").ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        var baseUrl = app.Urls.First();

        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        // 1. The person page gets extended, through the compression middleware.
        var personBody = await client.GetStringAsync($"/Items?personIds={personId:N}&recursive=true").ConfigureAwait(false);
        var personJson = JsonNode.Parse(personBody)!.AsObject();
        Check(((JsonArray)personJson["Items"]!).Count == 4, "person page gains the external titles over the wire");
        Check(personJson["TotalRecordCount"]!.GetValue<int>() == 4, "TotalRecordCount survives compression");
        Check(provider.LookupCount == 1, "looks the person up exactly once");

        var injected = ((JsonArray)personJson["Items"]!)[2]!.AsObject();
        Check(injected["LocationType"]!.GetValue<string>() == "Virtual", "injected items arrive marked Virtual");
        var injectedId = injected["Id"]!.GetValue<string>();

        // 2. The detail page for an injected item.
        var detail = JsonNode.Parse(await client.GetStringAsync($"/Items/{injectedId}").ConfigureAwait(false))!.AsObject();
        Check(detail["Name"]!.GetValue<string>() == "The Matrix", "detail page resolves the injected item");
        Check(detail["Overview"]!.GetValue<string>()!.Length > 0, "detail page carries the synopsis");
        Check(
            ((JsonArray)detail["ExternalUrls"]!)[0]!["Url"]!.GetValue<string>() == "https://seerr.example.com/movie/603",
            "detail page carries the request link");

        // 3. Poster redirects to TMDb.
        using var noRedirect = new HttpClientHandler { AllowAutoRedirect = false };
        using var rawClient = new HttpClient(noRedirect) { BaseAddress = new Uri(baseUrl) };
        var image = await rawClient.GetAsync($"/Items/{injectedId}/Images/Primary?fillWidth=400").ConfigureAwait(false);
        Check(image.StatusCode == HttpStatusCode.Found, "poster answers with a redirect");
        Check(
            image.Headers.Location!.ToString() == "https://image.tmdb.org/t/p/w500/poster603.jpg",
            "poster redirects to the right TMDb size");

        // 4. Child and playback routes are absorbed.
        var seasons = await client.GetAsync($"/Shows/{injectedId}/Seasons").ConfigureAwait(false);
        Check(seasons.StatusCode == HttpStatusCode.OK, "Seasons answers 200");
        Check(
            JsonNode.Parse(await seasons.Content.ReadAsStringAsync().ConfigureAwait(false))!["TotalRecordCount"]!.GetValue<int>() == 0,
            "Seasons answers with an empty result");

        var playback = await client.GetAsync($"/Items/{injectedId}/PlaybackInfo").ConfigureAwait(false);
        Check(playback.StatusCode == HttpStatusCode.NotFound, "PlaybackInfo is refused");

        var trailers = await client.GetAsync($"/Items/{injectedId}/LocalTrailers").ConfigureAwait(false);
        Check(
            (await trailers.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim() == "[]",
            "LocalTrailers answers with an empty array");

        // 5. Unrelated traffic is untouched.
        var passthrough = await client.GetAsync("/System/Info").ConfigureAwait(false);
        Check(passthrough.StatusCode == (HttpStatusCode)418, "unrelated requests pass straight through");

        // 6. Disabling the plugin restores stock behaviour.
        provider.Configuration.Enabled = false;
        var disabled = JsonNode.Parse(
            await client.GetStringAsync($"/Items?personIds={personId:N}").ConfigureAwait(false))!.AsObject();
        Check(((JsonArray)disabled["Items"]!).Count == 2, "the kill switch works");
        provider.Configuration.Enabled = true;

        // 7. A provider that throws must not break the page.
        provider.Throw = true;
        var resilient = JsonNode.Parse(
            await client.GetStringAsync($"/Items?personIds={personId:N}").ConfigureAwait(false))!.AsObject();
        Check(((JsonArray)resilient["Items"]!).Count == 2, "a failing lookup degrades to the stock response");
        provider.Throw = false;

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class FakeProvider : IFilmographyProvider
    {
        public FakeProvider(PluginConfiguration config) => Configuration = config;

        public PluginConfiguration Configuration { get; }

        public string ServerId => "test-server";

        public List<FilmographyEntry> Entries { get; set; } = new();

        public int LookupCount { get; private set; }

        public bool Throw { get; set; }

        public Task<IReadOnlyList<FilmographyEntry>> GetForPersonAsync(Guid personId, CancellationToken cancellationToken)
        {
            LookupCount++;
            if (Throw)
            {
                throw new InvalidOperationException("simulated TMDb failure");
            }

            return Task.FromResult<IReadOnlyList<FilmographyEntry>>(Entries);
        }

        public Task<FilmographyEntry?> GetEntryAsync(MediaKind kind, int tmdbId, CancellationToken cancellationToken)
            => Task.FromResult(Entries.FirstOrDefault(e => e.Kind == kind && e.TmdbId == tmdbId));
    }

    private static RouteMatch Match(string path, string queryString)
        => RouteMatcher.Match(new PathString(path), Query(queryString));

    private static IQueryCollection Query(string queryString)
        => new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString));

    private static byte[] Body(int itemCount)
    {
        var items = new JsonArray();
        for (var i = 0; i < itemCount; i++)
        {
            items.Add(new JsonObject
            {
                ["Name"] = "Real " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["Type"] = "Movie",
                ["ServerId"] = "s",
            });
        }

        var envelope = new JsonObject
        {
            ["Items"] = items,
            ["TotalRecordCount"] = itemCount,
            ["StartIndex"] = 0,
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    private static TmdbCredit Credit(
        int id,
        string mediaType = "movie",
        int votes = 5000,
        double popularity = 50,
        double voteAverage = 7.5,
        string? character = "Some Role",
        string? job = null,
        int? order = 1,
        int? episodeCount = null,
        string date = "1999-03-30",
        bool adult = false,
        List<int>? genreIds = null)
        => new()
        {
            Id = id,
            MediaType = mediaType,
            Title = mediaType == "movie" ? "Title " + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            SeriesName = mediaType == "tv" ? "Show " + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Overview = "Synopsis.",
            PosterPath = "/p.jpg",
            ReleaseDate = mediaType == "movie" ? date : null,
            FirstAirDate = mediaType == "tv" ? date : null,
            VoteAverage = voteAverage,
            VoteCount = votes,
            Popularity = popularity,
            Character = character,
            Job = job,
            Order = order,
            EpisodeCount = episodeCount,
            Adult = adult,
            GenreIds = genreIds ?? new List<int> { 18 },
        };

    private static FilmographyEntry Entry(MediaKind kind, int tmdbId, string name) => new()
    {
        TmdbId = tmdbId,
        Kind = kind,
        Name = name,
        Overview = "A synopsis for " + name + ".",
        PosterPath = "/poster" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg",
        BackdropPath = "/backdrop" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg",
        PremiereDate = new DateTime(1999, 3, 30, 0, 0, 0, DateTimeKind.Utc),
        CommunityRating = 8.2,
        VoteCount = 20000,
        Popularity = 90,
        Genres = new[] { "Action", "Science Fiction" },
        Score = 0.9,
    };

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  ok   " + description);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL " + description);
        }
    }
}
