using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ramphastos.Core;

namespace Ramphastos.Server;

public static class RamphastosExtensions
{
    /// <summary>Registers Ramphastos services with a VM factory and optional configuration.</summary>
    public static IServiceCollection AddRamphastos(
        this IServiceCollection services,
        Func<IServiceProvider, ApplicationModel> vmFactory,
        Action<RamphastosOptions>? configure = null)
    {
        // The hub serializes envelope args with SignalR's own JsonHubProtocol
        // — not MessageSerializer — so null-elision must be configured here.
        // Without it, e.g. a forwarded stream-open reaches clients with an
        // explicit "streamId": null, which is distinguishable from absent.
        services.AddSignalR().AddJsonProtocol(options =>
            options.PayloadSerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)
            // Authority snapshots carry the full mirrored state (workspaces,
            // sessions, pluginStates, scrollback) — the 32KB SignalR default
            // kills the authority connection mid-publish and leaves viewers
            // staring at authority_offline forever.
            .AddHubOptions<RamphastosHub>(options =>
                options.MaximumReceiveMessageSize = 32 * 1024 * 1024);
        services.AddSingleton(sp =>
        {
            var options = new RamphastosOptions { VmFactory = () => vmFactory(sp) };
            configure?.Invoke(options);
            return options;
        });
        services.AddSingleton<RamphastosConnectionHandler>();
        return services;
    }

    /// <summary>Registers Ramphastos with a parameterless VM type.</summary>
    public static IServiceCollection AddRamphastos<TVm>(
        this IServiceCollection services,
        Action<RamphastosOptions>? configure = null)
        where TVm : ApplicationModel, new() =>
        services.AddRamphastos(_ => new TVm(), configure);

    /// <summary>
    /// Maps the Ramphastos SignalR endpoint. Pass requireAuthorization to
    /// enforce endpoint-level auth on top of (or instead of) the
    /// <see cref="RamphastosOptions.AuthorizeHello"/> envelope hook.
    /// </summary>
    public static IEndpointRouteBuilder MapRamphastos(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/ramphastos",
        bool requireAuthorization = false)
    {
        var builder = endpoints.MapHub<RamphastosHub>(pattern);
        if (requireAuthorization)
            builder.RequireAuthorization();
        return endpoints;
    }
}
