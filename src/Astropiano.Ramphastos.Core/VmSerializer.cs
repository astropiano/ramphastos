using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// Serializes VM graph values honoring the attribute policy at every depth:
/// [ServerOnly]/[NoSync] members never leave the server, not just at the
/// root. Cycles and shared nodes serialize as {"$ref":"<pointer>"} to the
/// node's first-encountered path — object identity is preserved on the wire
/// without a decycle pass (§4.3).
/// </summary>
internal static class VmSerializer
{
    /// <summary>Leaf types go through System.Text.Json as-is.</summary>
    internal static bool IsLeafType(Type t) =>
        t.IsPrimitive || t.IsEnum ||
        t == typeof(string) || t == typeof(decimal) ||
        t == typeof(DateTime) || t == typeof(DateTimeOffset) ||
        t == typeof(TimeSpan) || t == typeof(Guid) ||
        t == typeof(Uri) || t == typeof(byte[]) ||
        typeof(JsonNode).IsAssignableFrom(t) ||
        Nullable.GetUnderlyingType(t) is { } u && IsLeafType(u);

    /// <param name="path">Pointer path of the value — used to emit $ref targets.</param>
    /// <param name="seen">Object→first-path map for the whole serialize run.</param>
    public static JsonNode? ToJson(object? value, string path = "", Dictionary<object, string>? seen = null,
        bool includeServerOnly = false)
    {
        if (value is null) return null;

        var type = value.GetType();
        if (IsLeafType(type))
            return JsonSerializer.SerializeToNode(value, MessageSerializer.Options);

        seen ??= new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        if (type.IsClass || IsRefLikeStruct(type))
        {
            if (seen.TryGetValue(value, out var firstPath))
                return new JsonObject { ["$ref"] = firstPath };
            seen[value] = path;
        }

        if (value is IDictionary dict)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is not string key) continue;
                obj[key] = ToJson(entry.Value, JsonPointer.Join(path, key), seen, includeServerOnly);
            }
            return obj;
        }

        if (value is IEnumerable seq && value is not string)
        {
            var arr = new JsonArray();
            var i = 0;
            foreach (var item in seq)
                arr.Add(ToJson(item, JsonPointer.Join(path, i++.ToString()), seen, includeServerOnly));
            return arr;
        }

        var model = MemberModel.For(type, includeServerOnly);
        var result = new JsonObject();
        foreach (var (wire, prop) in model.Properties)
            result[wire] = ToJson(prop.Read(value), JsonPointer.Join(path, wire), seen, includeServerOnly);
        return result;
    }

    private static bool IsRefLikeStruct(Type t) => t.IsValueType && !t.IsPrimitive && !t.IsEnum;

    /// <summary>Deserialize a wire value back to a typed member value (hydration, write intents).</summary>
    public static object? ToValue(JsonNode? node, Type targetType) =>
        node is null ? null : node.Deserialize(targetType, MessageSerializer.Options);

    /// <summary>
    /// Hydrate a persisted state object onto a freshly created VM (session
    /// resume). Server-only members are restored here — they exist in the
    /// session record even though they never reach the wire.
    /// </summary>
    public static void Hydrate(ApplicationModel vm, JsonObject state)
    {
        var model = MemberModel.For(vm.GetType(), includeServerOnly: true);
        foreach (var (wire, prop) in model.Properties)
        {
            // Shared members are re-resolved from the live registry at session
            // creation — never hydrated from a record.
            if (prop.Shared || prop.Writer is null || state[wire] is not { } node) continue;
            prop.Write(vm, ToValue(node, prop.Info.PropertyType));
        }
    }
}
