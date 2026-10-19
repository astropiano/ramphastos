using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Astropiano.TotalSerializer.Configuration;
using Astropiano.Ramphastos.Protocol;
using TsSerializer = Astropiano.TotalSerializer.TotalSerializer;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// How session records serialize the VM graph (§5.4). The format tag is
/// stored on the record — resume only decodes state whose format the
/// configured codec can read.
/// </summary>
public interface ISessionStateCodec
{
    /// <summary>Format tag written to new records ("stj" | "ts").</summary>
    string Format { get; }

    /// <summary>Can this codec decode a record written in <paramref name="format"/>?</summary>
    bool CanRead(string format);

    /// <summary>Serialize the VM root inside the actor turn.</summary>
    JsonNode SerializeState(ApplicationModel root);

    /// <summary>
    /// Restore the session VM: hydrate into the factory instance, or produce
    /// a new instance when the codec owns object construction (TotalSerializer).
    /// </summary>
    ApplicationModel? DeserializeState(JsonNode state, string format, Func<ApplicationModel> factory);
}

/// <summary>
/// Default codec: the member-model policy projection (JsonObject state,
/// hydrated into a factory-created VM). Reads/writes format "stj".
/// </summary>
public sealed class SessionStateCodecJson : ISessionStateCodec
{
    public const string FormatName = "stj";
    public static readonly SessionStateCodecJson Instance = new();

    public string Format => FormatName;
    public bool CanRead(string format) => format == FormatName;

    public JsonNode SerializeState(ApplicationModel root)
    {
        var state = new JsonObject();
        var model = MemberModel.For(root.GetType(), includeServerOnly: true);
        var seen = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var (wire, prop) in model.Properties)
        {
            if (prop.Shared) continue; // shared state is live — never recorded
            state[wire] = VmSerializer.ToJson(prop.Read(root),
                "/" + JsonPointer.Escape(wire), seen, includeServerOnly: true);
        }
        return state;
    }

    public ApplicationModel? DeserializeState(
        JsonNode state, string format, Func<ApplicationModel> factory)
    {
        if (state is not JsonObject obj || !CanRead(format)) return null;
        var vm = factory();
        VmSerializer.Hydrate(vm, obj);
        return vm;
    }
}

/// <summary>
/// TotalSerializer codec (§5.4): the whole VM graph travels — private fields,
/// delegates, cyclic references — so a resumed session keeps its internal
/// wiring (live migration, not rehydration). The codec owns VM construction;
/// the factory is only used for legacy "stj" records.
///
/// Defaults ignore <see cref="PropertyChangedEventHandler"/> /
/// <see cref="NotifyCollectionChangedEventHandler"/> fields — those belong to
/// session machinery (the GraphTracker re-subscribes on attach); serializing
/// them would drag tracker/session objects into the record.
///
/// Node-bound members (loggers, DI services, CancellationTokenSource):
/// register `Config.RegisterRecreateTypeFactory&lt;T&gt;` / `IgnoreType` via
/// <paramref name="configure"/> — per §5.4, VM graphs must not hard-bind
/// node-local resources.
/// </summary>
public sealed class SessionStateCodecTotalSerializer : ISessionStateCodec
{
    public const string FormatName = "ts";
    private readonly TsSerializer _serializer;

    public SessionStateCodecTotalSerializer(Action<Config>? configure = null)
    {
        var config = new Config();
        config.IgnoreType<PropertyChangedEventHandler>();
        config.IgnoreType<NotifyCollectionChangedEventHandler>();
        configure?.Invoke(config);
        _serializer = new TsSerializer(config);
    }

    public string Format => FormatName;

    /// <summary>Reads its own format and upgrades legacy stj projections.</summary>
    public bool CanRead(string format) => format is FormatName or SessionStateCodecJson.FormatName;

    public JsonNode SerializeState(ApplicationModel root) =>
        JsonValue.Create(_serializer.Serialize(root))!;

    public ApplicationModel? DeserializeState(
        JsonNode state, string format, Func<ApplicationModel> factory)
    {
        if (format == SessionStateCodecJson.FormatName)
            return SessionStateCodecJson.Instance.DeserializeState(state, format, factory);
        return format == FormatName && state is JsonValue v && v.TryGetValue<string>(out var blob)
            ? _serializer.Deserialize<ApplicationModel>(blob)
            : null;
    }
}
