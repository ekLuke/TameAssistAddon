using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Server-prefab introspection only. These are the runtime values installed
/// by Valheim, so do not hardcode feed items or offspring behavior across
/// species. No world writes are performed.
/// </summary>
internal static class RanchSpeciesMetadata
{
    private const BindingFlags Flags = BindingFlags.Public |
        BindingFlags.NonPublic | BindingFlags.Instance;

    internal static string Describe(GameObject adult)
    {
        if (adult == null) return "invalid-prefab";
        var tameable = adult.GetComponent<Tameable>();
        var breeding = adult.GetComponent<Procreation>();
        var ai = adult.GetComponent<MonsterAI>();
        var offspring = breeding != null ? breeding.m_offspring : null;
        bool egg = offspring != null &&
            offspring.GetComponent<ItemDrop>() != null;
        var young = offspring != null ? offspring.GetComponent<Growup>() : null;
        string food = DescribeFoods(ai);
        string tame = tameable == null ? "not-tameable" :
            tameable.m_tamingTime.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        string growth = young == null ? "not-vanilla-Growup" :
            FieldFloat(young, "m_growTime");
        return "foodPrefabs=" + food + ", tameSeconds=" + tame +
            ", babyKind=" + (egg ? "egg-ItemDrop" :
                offspring == null ? "none" : "creature") +
            ", babyGrowSeconds=" + growth;
    }

    private static string DescribeFoods(MonsterAI? ai)
    {
        if (ai == null) return "unknown-no-MonsterAI";
        var field = ai.GetType().GetField("m_consumeItems", Flags);
        if (field == null) return "unknown-no-consumeItems";
        if (field.GetValue(ai) is not IEnumerable values)
            return "unknown-not-enumerable";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in values)
        {
            if (obj is ItemDrop drop && drop.gameObject != null)
                names.Add(drop.gameObject.name);
            else if (obj is GameObject go)
                names.Add(go.name);
        }
        if (names.Count == 0) return "none-defined";
        return string.Join(",", names.OrderBy(x => x, StringComparer.Ordinal)
            .Take(25));
    }

    private static string FieldFloat(Component instance, string name)
    {
        var value = instance.GetType().GetField(name, Flags)?.GetValue(instance);
        return value is float f ? f.ToString("0",
            System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }
}
