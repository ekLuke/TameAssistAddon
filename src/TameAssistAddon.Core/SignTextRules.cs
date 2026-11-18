using System;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Classify an untouched vanilla sign's placeholder separately from genuine
/// player text. Never replace arbitrary nonempty user-authored messages.
/// </summary>
public static class SignTextRules
{
    public static bool IsBlankOrPlaceholder(string? raw, string? prefabDefault = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var text = raw.Trim();
        if (text == "..." || text == "…" || text == "···") return true;
        return !string.IsNullOrWhiteSpace(prefabDefault) &&
            string.Equals(text, prefabDefault.Trim(), StringComparison.Ordinal);
    }
}
