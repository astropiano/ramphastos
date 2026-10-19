using System.Text.Json.Nodes;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// Schema-drift path (§5.4): when a persisted record's schema fingerprint
/// doesn't match the current VM model, a declared migrator maps the old
/// record state into the current shape before hydration. No migrator → the
/// record is rejected and a fresh session is created — never silently
/// deserialize into a drifted schema.
/// </summary>
public interface ISessionMigrator
{
    /// <summary>Can this migrator map a record written under <paramref name="fromSchema"/>?</summary>
    bool CanMigrate(string fromSchema, string toSchema);

    /// <summary>
    /// Transform the persisted state into the current record shape.
    /// Return null to reject the record (fresh session).
    /// Meaningful for "stj" (JsonObject) states; "ts" blobs are opaque and
    /// should normally be rejected.
    /// </summary>
    JsonNode? Migrate(JsonNode oldState, string fromSchema, string toSchema);
}
