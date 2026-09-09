// Minimal stand-ins for the Jellyfin base types touched by the files under test.
// The real definitions come from the Jellyfin.Model NuGet package in the actual build; these
// exist only so the harness can compile without network access to a package feed.
namespace MediaBrowser.Model.Plugins
{
    /// <summary>Stub of Jellyfin's plugin configuration base class (it is an empty marker type).</summary>
    public class BasePluginConfiguration
    {
    }
}
