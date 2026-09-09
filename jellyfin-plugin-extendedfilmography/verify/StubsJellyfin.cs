// Compile-only stand-ins for the Jellyfin API surface the plugin touches.
//
// Their ONLY purpose is to let the offline harness type-check the Jellyfin-coupled source files
// (Plugin, PluginServiceRegistrator, FilmographyProvider, LibraryTmdbIndex, TmdbClient,
// FilmographyCache) without a package feed. They are never executed and are not shipped.
//
// IMPORTANT: because these are hand-written, they prove the plugin's own code is internally
// consistent - not that it matches the real Jellyfin API. The authority on that is the CI build
// in .github/workflows/build.yml, which restores the actual Jellyfin.Controller and
// Jellyfin.Model packages. If a member below has drifted from upstream, CI is where it shows up.
#pragma warning disable SA1402, SA1649, CA1040, CA2227, CA1002, CA1819

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Data.Enums
{
    public enum BaseItemKind
    {
        Movie,
        Series,
        Person,
    }
}

namespace MediaBrowser.Common.Configuration
{
    public interface IApplicationPaths
    {
        string CachePath { get; }

        string DataPath { get; }

        string PluginsPath { get; }
    }
}

namespace MediaBrowser.Model.Serialization
{
    public interface IXmlSerializer
    {
    }
}

namespace MediaBrowser.Model.Plugins
{
    public class PluginPageInfo
    {
        public string? Name { get; set; }

        public string? EmbeddedResourcePath { get; set; }
    }

    public interface IHasWebPages
    {
        IEnumerable<PluginPageInfo> GetPages();
    }
}

namespace MediaBrowser.Common.Plugins
{
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Serialization;

    public abstract class BasePlugin<TConfigurationType>
        where TConfigurationType : BasePluginConfiguration, new()
    {
        protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        {
            ApplicationPaths = applicationPaths;
            XmlSerializer = xmlSerializer;
            Configuration = new TConfigurationType();
        }

        public void SaveConfiguration() { }

        public abstract string Name { get; }

        public virtual Guid Id { get; }

        public virtual string Description => string.Empty;

        public TConfigurationType Configuration { get; }

        protected IApplicationPaths ApplicationPaths { get; }

        protected IXmlSerializer XmlSerializer { get; }
    }
}

namespace MediaBrowser.Controller
{
    public interface IServerApplicationHost
    {
        string SystemId { get; }
    }
}

namespace MediaBrowser.Controller.Plugins
{
    public interface IPluginServiceRegistrator
    {
        void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost);
    }
}

namespace MediaBrowser.Controller.Dto
{
    public class DtoOptions
    {
        public DtoOptions()
        {
        }

        public DtoOptions(bool allFields) => AllFields = allFields;

        public bool AllFields { get; }
    }
}

namespace MediaBrowser.Controller.Entities
{
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Dto;

    public class BaseItem
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }

        public Dictionary<string, string> ProviderIds { get; set; } = new();
    }

    public class InternalItemsQuery
    {
        public BaseItemKind[] IncludeItemTypes { get; set; } = Array.Empty<BaseItemKind>();

        public bool Recursive { get; set; }

        public bool? IsVirtualItem { get; set; }

        public DtoOptions? DtoOptions { get; set; }
    }
}

namespace MediaBrowser.Controller.Entities.Movies
{
    public class Movie : BaseItem
    {
    }
}

namespace MediaBrowser.Controller.Entities.TV
{
    public class Series : BaseItem
    {
    }
}

namespace MediaBrowser.Controller.Library
{
    using MediaBrowser.Controller.Entities;

    public interface ILibraryManager
    {
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);

        BaseItem? GetItemById(Guid id);
    }
}

#pragma warning restore SA1402, SA1649, CA1040, CA2227, CA1002, CA1819
