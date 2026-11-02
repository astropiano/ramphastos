namespace Ramphastos.Core;

/// <summary>Property exists server-side only; never serialized to clients or session records leave-node policy aside.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ServerOnlyAttribute : Attribute;

/// <summary>Property is not synchronized to clients.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NoSyncAttribute : Attribute;

/// <summary>Exposes a view-model method to clients as an invokable command.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>Minimum connection role required to execute.</summary>
    public string RequireRole { get; init; } = Protocol.SessionRole.Controller;
    /// <summary>Wire name override; defaults to camelCase method name.</summary>
    public string? Name { get; init; }
}

/// <summary>Marks a property whose changes may be coalesced/lossy-synced under load.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SyncReducedAttribute : Attribute;

/// <summary>
/// String property synced as diff_match_patch textpatch ops instead of
/// whole-value replaces — for high-frequency text editing (§4.4).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TextSyncAttribute : Attribute;

/// <summary>
/// Member resolves to a process-wide SharedObject instance shared across
/// sessions (§6.3): one actor per shared object; each session mirrors the
/// shared subtree at this member's path. Shared state is live — it is wire-
/// visible in snapshots and patches but never persisted in session records.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SharedAttribute : Attribute
{
    /// <summary>Registry key override; defaults to the property type's full name.</summary>
    public string? Name { get; init; }
}
