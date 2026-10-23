using System.Text.Json.Nodes;

namespace Ramphastos.Protocol;

/// <summary>
/// One operation in a patch batch. RFC 6902 ops (add/remove/replace/move/copy)
/// plus the Ramphastos-specific "textpatch" op carrying a diff_match_patch
/// payload for high-frequency string editing.
/// </summary>
public sealed record PatchOp
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Replace = "replace";
    public const string Move = "move";
    public const string Copy = "copy";
    public const string TextPatch = "textpatch";

    public required string Op { get; init; }
    /// <summary>JSON Pointer (RFC 6901) into the VM graph.</summary>
    public required string Path { get; init; }
    public JsonNode? Value { get; init; }
    /// <summary>Source pointer for move/copy.</summary>
    public string? From { get; init; }
    /// <summary>diff_match_patch payload for textpatch ops.</summary>
    public string? Patch { get; init; }

    public static PatchOp ReplaceOp(string path, JsonNode? value) =>
        new() { Op = Replace, Path = path, Value = value };
}

/// <summary>JSON Pointer (RFC 6901) escaping and joining.</summary>
public static class JsonPointer
{
    public static string Escape(string segment) =>
        segment.Replace("~", "~0").Replace("/", "~1");

    public static string Unescape(string segment) =>
        segment.Replace("~1", "/").Replace("~0", "~");

    public static string Join(string basePath, string segment) =>
        basePath == "/" ? "/" + Escape(segment) : basePath + "/" + Escape(segment);
}
