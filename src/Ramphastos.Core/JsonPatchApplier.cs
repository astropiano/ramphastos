using System.Text.Json.Nodes;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// Applies RFC-6902-style patch ops onto a JsonNode graph — the server-side
/// replica of a client-authoritative session (§4.9) and the test client's
/// mirror share this applier, so both observe identical semantics.
/// Malformed ops throw JsonPatchException; callers decide whether the record
/// stays usable.
/// </summary>
internal static class JsonPatchApplier
{
    public sealed class JsonPatchException(string path) : Exception($"Patch path broken at '{path}'");

    public static void Apply(JsonNode root, PatchOp op)
    {
        var segs = op.Path.Split('/').Skip(1).Select(JsonPointer.Unescape).ToArray();
        var parent = root as JsonNode;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            parent = parent switch
            {
                JsonObject o => o[segs[i]],
                JsonArray a => int.TryParse(segs[i], out var idx) && idx < a.Count ? a[idx] : null,
                _ => null,
            };
            if (parent is null) throw new JsonPatchException(op.Path);
        }
        var key = segs.Length == 0 ? null : segs[^1];

        switch (op.Op)
        {
            case PatchOp.Add:
            case PatchOp.Copy:
                if (parent is JsonArray arr)
                    arr.Insert(key == "-" ? arr.Count : int.Parse(key!), op.Value?.DeepClone());
                else if (parent is JsonObject o)
                    o[key!] = op.Value?.DeepClone();
                else if (key is null)
                    throw new JsonPatchException(op.Path); // can't add at the root
                break;
            case PatchOp.Replace:
                Set(parent, key, op.Value?.DeepClone());
                break;
            case PatchOp.TextPatch:
                if (op.Patch is not null && Read(parent, key) is JsonValue v &&
                    v.TryGetValue<string>(out var current) &&
                    TextDiffer.ApplyPatch(current, op.Patch) is { } patched)
                {
                    Set(parent, key, JsonValue.Create(patched));
                }
                else
                {
                    Set(parent, key, op.Value?.DeepClone()); // Value carries the full string as fallback
                }
                break;
            case PatchOp.Remove:
                if (parent is JsonArray a) a.RemoveAt(int.Parse(key!));
                else if (parent is JsonObject o2) o2.Remove(key!);
                break;
            case PatchOp.Move:
                var val = ReadAt(root, op.From!)?.DeepClone();
                Apply(root, new PatchOp { Op = PatchOp.Remove, Path = op.From! });
                Apply(root, new PatchOp { Op = PatchOp.Add, Path = op.Path, Value = val });
                break;
        }
    }

    public static JsonNode? ReadAt(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        if (pointer is "" or "/") return node;
        foreach (var seg in pointer.Split('/').Skip(1).Select(JsonPointer.Unescape))
        {
            node = node switch
            {
                JsonObject o => o[seg],
                JsonArray a => int.TryParse(seg, out var i) && i < a.Count ? a[i] : null,
                _ => null,
            };
        }
        return node;
    }

    private static JsonNode? Read(JsonNode? parent, string? key) => parent switch
    {
        JsonObject o => o[key!],
        JsonArray a => int.TryParse(key, out var i) && i < a.Count ? a[i] : null,
        _ => null,
    };

    private static void Set(JsonNode? parent, string? key, JsonNode? value)
    {
        if (parent is JsonArray a) a[int.Parse(key!)] = value;
        else if (parent is JsonObject o) o[key!] = value;
    }
}
