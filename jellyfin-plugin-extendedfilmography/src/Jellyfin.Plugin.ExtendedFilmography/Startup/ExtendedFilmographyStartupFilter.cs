using System;
using Jellyfin.Plugin.ExtendedFilmography.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.ExtendedFilmography.Startup;

/// <summary>
/// Inserts the plugin's middleware into Jellyfin's HTTP pipeline.
/// </summary>
/// <remarks>
/// Jellyfin has no hook for adding middleware, but ASP.NET Core resolves every registered
/// <see cref="IStartupFilter"/> from DI and applies it around the host's own configuration.
/// Registering one from a plugin's service registrator is therefore the supported way in.
/// <para>
/// The middleware is added <em>before</em> <c>next</c>, which puts it at the very front of the
/// pipeline. That matters in both directions: the request reaches it before routing, and the
/// response reaches it last, after Jellyfin's response compression has run - which is why
/// <see cref="ResponseCodec"/> exists.
/// </para>
/// </remarks>
public sealed class ExtendedFilmographyStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            builder.UseMiddleware<ExtendedFilmographyMiddleware>();
            next(builder);
        };
    }
}
