using System;
using System.Text.RegularExpressions;

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
        // Valheim stores an empty custom sign as a rich-text color prefix,
        // sometimes followed by its default ellipsis. Strip formatting for
        // classification only: never strip data from saved player text.
        var text = Regex.Replace(raw, @"<[^>]*>", "").Trim();
        if (text == "..." || text == "…" || text == "···") return true;
        return !string.IsNullOrWhiteSpace(prefabDefault) &&
            string.Equals(text, Regex.Replace(prefabDefault, @"<[^>]*>", "").Trim(),
                StringComparison.Ordinal);
    }
}
