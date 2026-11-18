using System;
using System.Collections.Generic;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Conservative temporary eligibility for *loaded* wild animals, supplied
/// only by the current automatic pen probe. This is not a 3D enclosure
/// validator. Without an opt-in and a closed candidate, no feed is attempted.
/// Never grant eligibility based merely on nearby walls or a sign.
/// </summary>
internal static class VerifiedPenFeedCandidates
{
    private static readonly Dictionary<ZDOID, float> LastSeen =
        new Dictionary<ZDOID, float>();
    private const float MaxAgeSeconds = 75f;

    internal static void Replace(IEnumerable<ZDOID> ids)
    {
        LastSeen.Clear();
        if (ids == null) return;
        float now = Time.unscaledTime;
        foreach (var id in ids) LastSeen[id] = now;
    }

    internal static bool IsEligible(ZDO animal)
    {
        if (animal == null || !animal.IsValid() || ZNet.instance == null ||
            !ZNet.instance.IsServer() || ZNetScene.instance == null ||
            ZNetScene.instance.FindInstance(animal) == null)
            return false;
        return LastSeen.TryGetValue(animal.m_uid, out var timestamp) &&
            Time.unscaledTime >= timestamp &&
            Time.unscaledTime - timestamp <= MaxAgeSeconds;
    }
}
