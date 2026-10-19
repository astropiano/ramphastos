using System.Reflection;
using System.Text.Json.Nodes;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// Root view-model model: the shared <see cref="MemberModel"/> plus the
/// invokable commands (which exist only at the root). Member access goes
/// through Astropiano.ReflectionTools compiled accessors (§6.5).
/// </summary>
internal sealed class VmModel
{
    public required MemberModel Members { get; init; }
    public required Dictionary<string, MethodInfo> Commands { get; init; }      // wire name -> method
    public required Dictionary<string, CommandAttribute> CommandAttributes { get; init; }

    public Type VmType => Members.ModelType;
    public Dictionary<string, VmProperty> Properties => Members.Properties;

    public static VmModel For(Type type)
    {
        var commands = new Dictionary<string, MethodInfo>();
        var attrs = new Dictionary<string, CommandAttribute>();
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = m.GetCustomAttribute<CommandAttribute>();
            if (attr is null)
                continue;
            var name = attr.Name ?? Camel(m.Name);
            commands[name] = m;
            attrs[name] = attr;
        }

        return new VmModel
        {
            Members = MemberModel.For(type),
            Commands = commands,
            CommandAttributes = attrs,
        };
    }

    /// <summary>Member schema fingerprint + command surface (§5.4 schema gate).</summary>
    public string SchemaFingerprint =>
        Members.ComputeSchemaFingerprint() +
        "|cmd:" + string.Join(",", Commands.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Snapshot root per protocol: {type, props, commands}.</summary>
    public JsonObject BuildSnapshot(SyncedObject vm)
    {
        var props = new JsonObject();
        var seen = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var (wireName, prop) in Properties)
            props[wireName] = VmSerializer.ToJson(prop.Read(vm), "/" + JsonPointer.Escape(wireName), seen);

        return new JsonObject
        {
            ["type"] = VmType.Name,
            ["props"] = props,
            ["commands"] = new JsonArray(Commands.Keys.Select(k => (JsonNode)k).ToArray())
        };
    }

    public bool TryGetProperty(string wireName, out VmProperty prop) =>
        Properties.TryGetValue(wireName, out prop!);

    /// <summary>Wire name for a CLR property name, or null if not synced.</summary>
    public string? WireNameOf(string clrName) =>
        Properties.TryGetValue(Camel(clrName), out var p) && p.Info.Name == clrName
            ? Camel(clrName)
            : null;

    public static string Camel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
