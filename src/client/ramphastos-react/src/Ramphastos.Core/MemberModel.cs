using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TotalSerializer.ReflectionTools;

namespace Ramphastos.Core;

/// <summary>
/// One syncable property: wire name, compiled accessors, sync policy flags.
/// </summary>
internal sealed class VmProperty
{
    public required PropertyInfo Info { get; init; }
    public required string WireName { get; init; }
    public required IMemberReader Reader { get; init; }
    public IMemberWriter? Writer { get; init; }
    public bool SyncReduced { get; init; }
    public bool TextSync { get; init; }
    /// <summary>SharedObject key (attribute name or member type full name); null = regular member.</summary>
    public string? SharedKey { get; init; }
    public bool Shared => SharedKey is not null;

    public object? Read(object target) => Reader.GetUntypedValue(target);

    public void Write(object target, object? value)
    {
        if (Writer is null) return;
        object boxed = target;
        Writer.SetUntypedValue(ref boxed, value!);
    }
}

/// <summary>
/// Reflection model of a synced object type: serializable members honoring
/// the attribute policy. Used for the VM root and every nested object type
/// the tracker walks into — the wire contract is the same shape at any depth.
/// Commands exist only on the root model (<see cref="VmModel"/>).
/// </summary>
internal class MemberModel
{
    private static readonly ConcurrentDictionary<(Type, bool), MemberModel> Cache = new();

    public required Type ModelType { get; init; }
    public required Dictionary<string, VmProperty> Properties { get; init; } // wire name -> prop

    /// <param name="includeServerOnly">Session records persist server-only state;
    /// wire serialization excludes it.</param>
    public static MemberModel For(Type type, bool includeServerOnly = false) =>
        Cache.GetOrAdd((type, includeServerOnly), static key => Build(key.Item1, key.Item2));

    private static MemberModel Build(Type type, bool includeServerOnly)
    {
        var props = new Dictionary<string, VmProperty>();

        // Collections/dictionaries serialize as JSON arrays/objects — their CLR
        // surface (Count, Item[], Capacity…) is not part of the wire model.
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return new MemberModel { ModelType = type, Properties = props };

        foreach (var p in PropertyInfoCache.GetPropertyInfos(type))
        {
            if (p.GetIndexParameters().Length != 0 || p.GetMethod is null || !p.GetMethod.IsPublic)
                continue;
            if (p.IsDefined(typeof(NoSyncAttribute)))
                continue;
            if (p.IsDefined(typeof(ServerOnlyAttribute)) && !includeServerOnly)
                continue;

            props[VmModel.Camel(p.Name)] = new VmProperty
            {
                Info = p,
                WireName = VmModel.Camel(p.Name),
                Reader = MemberAccess.ReaderFor(p),
                Writer = p.SetMethod is not null ? MemberAccess.WriterFor(p) : null,
                SyncReduced = p.IsDefined(typeof(SyncReducedAttribute)),
                TextSync = p.IsDefined(typeof(TextSyncAttribute)),
                SharedKey = p.GetCustomAttribute<SharedAttribute>() is { } shared
                    ? shared.Name ?? p.PropertyType.FullName
                    : null,
            };
        }

        return new MemberModel { ModelType = type, Properties = props };
    }

    /// <summary>
    /// Fingerprint of the synced member shape (§6.5): same fingerprint → a
    /// serialized session can be directly rehydrated. Walked over the
    /// ReflectionTools member model so schema and serializer always agree.
    /// </summary>
    public string ComputeSchemaFingerprint()
    {
        var sb = new StringBuilder(ModelType.FullName);
        foreach (var (wire, prop) in Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(';').Append(wire).Append(':').Append(prop.Info.PropertyType.FullName);
        }
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha1:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
