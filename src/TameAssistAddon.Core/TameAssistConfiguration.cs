using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Optional compatibility reader for documented BepInEx config keys.
/// Does not reference the TameAssist assembly or any private API.
/// Missing/unified/world-specific files require explicit configuration or fallback.
/// </summary>
public sealed class TameAssistConfiguration
{
    public bool FeedFromContainers { get; private set; }
    public float FeedFromContainersRange { get; private set; }
    public int FeedFromContainersLeaveAtLeast { get; private set; } = 1;
    public float FedDurationMultiplier { get; private set; } = 1f;
    public float TamingTimeMultiplier { get; private set; } = 1f;

    public static TameAssistConfiguration Read(string path) => Parse(File.ReadAllText(path));

    public static TameAssistConfiguration Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var stripped = line.Trim();
            if (stripped.Length == 0 || stripped[0] == '#' || stripped[0] == ';' || stripped[0] == '[')
                continue;
            int equal = stripped.IndexOf('=');
            if (equal <= 0) continue;
            values[stripped.Substring(0, equal).Trim()] = stripped.Substring(equal + 1).Trim();
        }
        var result = new TameAssistConfiguration();
        if (values.TryGetValue("FeedFromContainers", out var enabled) && bool.TryParse(enabled, out var feed))
            result.FeedFromContainers = feed;
        if (values.TryGetValue("FeedFromContainersRange", out var range) &&
            float.TryParse(range, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r >= 0 && r <= 1000)
            result.FeedFromContainersRange = r;
        if (values.TryGetValue("FeedFromContainersLeaveAtLeast", out var leave) && int.TryParse(leave, out var l) && l >= 0)
            result.FeedFromContainersLeaveAtLeast = l;
        if (values.TryGetValue("FedDurationMultiplier", out var fedTime) &&
            float.TryParse(fedTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
            result.FedDurationMultiplier = f;
        if (values.TryGetValue("TamingTimeMultiplier", out var tameTime) &&
            float.TryParse(tameTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0)
            result.TamingTimeMultiplier = t;
        return result;
    }
}
