using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Validates compact per-species area requirements. On invalid input the
/// caller must fail closed and cannot use area-based slaughter decisions.
/// </summary>
public static class RanchAreaConfiguration
{
    public static bool TryParseSpeciesAreas(string? input,
        IDictionary<string, double> destination)
    {
        if (destination == null || string.IsNullOrWhiteSpace(input))
            return false;
        var updated = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in input.Split(','))
        {
            var pieces = field.Split(':');
            if (pieces.Length != 2) return false;
            var species = pieces[0].Trim();
            if (species.Length == 0 || species.Length > 80 ||
                species.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                return false;
            if (!double.TryParse(pieces[1].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var size) ||
                double.IsNaN(size) || double.IsInfinity(size) ||
                size < 0.5 || size > 500 || !updated.TryAdd(species, size))
                return false;
        }
        if (updated.Count == 0 || updated.Count > 50) return false;
        // Atomic config replacement: don't partially alter prior values
        // when a trailing malformed entry is encountered.
        destination.Clear();
        foreach (var pair in updated) destination[pair.Key] = pair.Value;
        return true;
    }
}
