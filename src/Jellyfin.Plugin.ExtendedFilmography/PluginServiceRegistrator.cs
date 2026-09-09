using System;
using System.IO;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.ExtendedFilmography.Abstractions;
using Jellyfin.Plugin.ExtendedFilmography.Cache;
using Jellyfin.Plugin.ExtendedFilmography.Middleware;
using Jellyfin.Plugin.ExtendedFilmography.Services;
using Jellyfin.Plugin.ExtendedFilmography.Startup;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography;

/// <summary>
/// Wires the plugin's services into Jellyfin's container, and - via
/// <see cref="ExtendedFilmographyStartupFilter"/> - its middleware into the HTTP pipeline.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddHttpClient(TmdbClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-ExtendedFilmography/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });

        serviceCollection.AddHttpClient(ExtendedFilmographyMiddleware.ImageHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-ExtendedFilmography/1.0");
        });

        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<LibraryTmdbIndex>();

        serviceCollection.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<FilmographyCache>();

            // Plugin.Instance is set by the time services are resolved, but fall back to the
            // same path so a resolution during startup cannot produce a null directory.
            var directory = Plugin.Instance?.CacheDirectory
                            ?? Path.Combine(paths.CachePath, "extended-filmography");

            return new FilmographyCache(directory, logger);
        });

        serviceCollection.AddSingleton<IFilmographyProvider, FilmographyProvider>();
        serviceCollection.AddSingleton<IStartupFilter, ExtendedFilmographyStartupFilter>();
    }
}
