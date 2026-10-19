using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.Server;

/// <summary>
/// Bridges the transport-agnostic session manager to SignalR: outbound
/// messages go to concrete connection ids via IHubContext.
/// </summary>
public sealed class RamphastosConnectionHandler : IAsyncDisposable
{
    private readonly RamphastosSessionManager _manager;
    private readonly IHubContext<RamphastosHub> _hubContext;

    public RamphastosConnectionHandler(
        RamphastosOptions options,
        IHubContext<RamphastosHub> hubContext,
        ILogger<RamphastosConnectionHandler>? logger = null)
    {
        _hubContext = hubContext;
        var sessionOptions = new SessionOptions
        {
            Provider = options.Provider,
            DisconnectGrace = options.DisconnectGrace,
            AuthorizeHello = options.AuthorizeHello,
            OpLogCapacity = options.OpLogCapacity,
            ConfigureSession = options.ConfigureSession,
            Codec = options.Codec,
            Logger = logger,
        };
        foreach (var m in options.Migrators) sessionOptions.Migrators.Add(m);
        foreach (var (key, factory) in options.SharedObjects)
            sessionOptions.SharedObjects[key] = factory;
        _manager = new RamphastosSessionManager(
            options.VmFactory, SendTo, sessionOptions, sendFrameTo: SendFrameTo);
    }

    public RamphastosSessionManager Manager => _manager;

    public Task HandleHello(string connectionId, HelloMessage hello) =>
        _manager.HandleHello(connectionId, hello);

    /// <summary>Any post-hello envelope — the session endpoint dispatches by
    /// connection role (client invoke/stream-* vs authority upstream).</summary>
    public void HandleInbound(string connectionId, RamphastosMessage msg) =>
        _manager.HandleInbound(connectionId, msg);

    public void HandleFrame(string connectionId, byte[] frame) =>
        _manager.HandleFrame(connectionId, frame);

    public void HandleDisconnect(string connectionId) =>
        _manager.HandleDisconnect(connectionId);

    private async Task SendTo(string connectionId, RamphastosMessage message) =>
        await _hubContext.Clients.Client(connectionId)
            .SendAsync("message", (object)message)
            .ConfigureAwait(false);

    private async Task SendFrameTo(string connectionId, byte[] frame) =>
        await _hubContext.Clients.Client(connectionId)
            .SendAsync("frame", frame)
            .ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();
}
