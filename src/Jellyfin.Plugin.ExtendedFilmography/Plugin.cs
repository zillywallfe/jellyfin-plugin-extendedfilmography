using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ExtendedFilmography;

/// <summary>
/// Shows what an actor is known for beyond your library, on the person page, in every client.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's path provider.</param>
    /// <param name="xmlSerializer">Jellyfin's configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (Configuration.MigrateDefaults())
        {
            SaveConfiguration();
        }

        // The plugin's only disk footprint. It lives under the server's cache directory, so
        // "Clear cache" in the dashboard sweeps it and nothing here is ever a backup concern.
        CacheDirectory = Path.Combine(applicationPaths.CachePath, "extended-filmography");
    }

    /// <summary>
    /// Gets the running instance, so the middleware can read live configuration without a
    /// second DI round trip on every request.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the directory the filmography cache is stored in.</summary>
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public override string Name => "Extended Filmography";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("54fa2f7d-5365-456e-8017-75aad96f47d4");

    /// <inheritdoc />
    public override string Description =>
        "Extends the person page beyond your library: adds an actor's best-known films and series "
        + "from TMDb, in every client, without writing anything to the library database.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        },
    };
}
