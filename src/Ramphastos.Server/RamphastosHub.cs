using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Ramphastos.Protocol;

namespace Ramphastos.Server;

/// <summary>
/// Single SignalR endpoint for the protocol. Transport is thin: one
/// client→server method ("Send") carrying envelope JSON, one server→client
/// callback ("message") carrying envelope JSON.
/// </summary>
public sealed class RamphastosHub : Hub
{
    private readonly RamphastosConnectionHandler _handler;

    public RamphastosHub(RamphastosConnectionHandler handler) => _handler = handler;

    /// <summary>Client→server entry point. Payload is one message envelope.</summary>
    public async Task Send(JsonObject envelope)
    {
        RamphastosMessage message;
        try
        {
            message = MessageSerializer.Deserialize(envelope);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("message", new ErrorMessage
            {
                Error = new ErrorBody
                {
                    Code = ErrorCode.SessionCorrupted,
                    Message = $"Bad envelope: {ex.Message}"
                }
            });
            return;
        }

        switch (message)
        {
            case HelloMessage hello:
                await _handler.HandleHello(Context.ConnectionId, hello);
                break;
            case ByeMessage:
                _handler.HandleDisconnect(Context.ConnectionId);
                break;
            default:
                // invoke, stream-open/-close from clients; snapshot, patch,
                // result, stream-* from authority connections — the session
                // endpoint dispatches by connection role.
                _handler.HandleInbound(Context.ConnectionId, message);
                break;
        }
    }

    /// <summary>
    /// Data-plane entry point: one framed stream payload (see StreamFrame).
    /// byte[] rides base64 under the JSON hub protocol and true binary under
    /// MessagePack — the frame format is identical either way.
    /// </summary>
    public void SendFrame(byte[] frame) => _handler.HandleFrame(Context.ConnectionId, frame);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _handler.HandleDisconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
