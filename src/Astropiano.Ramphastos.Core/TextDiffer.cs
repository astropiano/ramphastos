using DiffMatchPatch;

namespace Astropiano.Ramphastos.Core;

/// <summary>diff_match_patch glue for [TextSync] members (§4.4 textpatch op).</summary>
internal static class TextDiffer
{
    private static readonly diff_match_patch Dmp = new();

    /// <summary>Patch text that transforms <paramref name="oldText"/> into <paramref name="newText"/>.</summary>
    public static string CreatePatch(string oldText, string newText) =>
        Dmp.patch_toText(Dmp.patch_make(oldText, newText));

    /// <summary>Apply a diff_match_patch patch text; null when the patch can't apply cleanly.</summary>
    public static string? ApplyPatch(string oldText, string patch)
    {
        var result = Dmp.patch_apply(Dmp.patch_fromText(patch), oldText);
        return result.Length > 0 ? (string)result[0] : null;
    }
}
