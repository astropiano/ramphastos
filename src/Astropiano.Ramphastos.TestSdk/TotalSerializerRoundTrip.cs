using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Astropiano.TotalSerializer.Configuration;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.Protocol;
using TotalSerializerImpl = Astropiano.TotalSerializer.TotalSerializer;

namespace Astropiano.Ramphastos.TestSdk;

/// <summary>
/// Session-state round-trip check through TotalSerializer (architecture
/// §5.4): the VM graph — private fields, references, collections included —
/// is serialized and deserialized with the same fidelity the session
/// providers will rely on for suspend/resume records.
///
/// Change-notification delegates are excluded: a persisted session must not
/// capture the tracker's handler subscriptions; a restored VM re-attaches
/// through a fresh Session instead.
/// </summary>
public static class TotalSerializerRoundTrip
{
    public static Config CreateConfig()
    {
        var config = new Config();
        // Delegate fields (event backing stores) point into live
        // infrastructure — never serialize subscriptions.
        config.IgnoreType<PropertyChangedEventHandler>();
        config.IgnoreType<NotifyCollectionChangedEventHandler>();
        config.IgnoreType<Delegate>();
        return config;
    }

    /// <summary>Serialize + deserialize the VM; returns the restored instance.</summary>
    public static TVm RoundTrip<TVm>(TVm vm) where TVm : ApplicationModel
    {
        var serializer = new TotalSerializerImpl(CreateConfig());
        var json = serializer.Serialize(vm);
        return serializer.Deserialize<TVm>(json)!;
    }

    /// <summary>Both VMs must project the same wire state (server-only included).</summary>
    public static JsonObject Project<TVm>(TVm vm) where TVm : ApplicationModel
    {
        var state = new JsonObject();
        var model = MemberModel.For(vm.GetType(), includeServerOnly: true);
        var seen = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var (wire, prop) in model.Properties)
            state[wire] = VmSerializer.ToJson(prop.Read(vm),
                "/" + JsonPointer.Escape(wire), seen, includeServerOnly: true);
        return state;
    }
}
