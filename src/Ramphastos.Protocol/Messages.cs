namespace Ramphastos.Protocol;

/// <summary>Protocol-level constants.</summary>
public static class ProtocolConstants
{
    public const int Version = 1;
}

public static class MessageType
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Snapshot = "snapshot";
    public const string Patch = "patch";
    public const string Invoke = "invoke";
    public const string Result = "result";
    public const string Call = "call";
    public const string Event = "event";
    public const string Error = "error";
    public const string Bye = "bye";
    public const string StreamOpen = "stream-open";
    public const string StreamOpened = "stream-opened";
    public const string StreamClose = "stream-close";
}

public static class SessionRole
{
    public const string Viewer = "viewer";
    public const string Controller = "controller";
    /// <summary>
    /// The client-side authority for a mirrored session (§4.9): pushes
    /// snapshot/patch upstream, owns commands and stream data; the server
    /// holds a replica and replays to viewers/controllers.
    /// </summary>
    public const string Authority = "authority";
}

public static class ErrorCode
{
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string AuthExpired = "auth_expired";
    public const string SessionCorrupted = "session_corrupted";
    public const string SchemaDrift = "schema_drift";
    public const string InvokeFailed = "invoke_failed";
    public const string Forbidden = "forbidden";
    public const string UnknownPath = "unknown_path";
    public const string UnknownStream = "unknown_stream";
    public const string StreamRejected = "stream_rejected";
    /// <summary>A mirrored session's authority is not attached (§4.9).</summary>
    public const string AuthorityOffline = "authority_offline";
    /// <summary>Sent to a superseded authority conn — another authority
    /// hello took over the session (§4.9).</summary>
    public const string AuthorityReplaced = "authority_replaced";
}
