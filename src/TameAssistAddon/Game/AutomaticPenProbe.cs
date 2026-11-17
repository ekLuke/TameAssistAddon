using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Read-only discovery of potential pens from ZDO positions and prefab
/// BoxColliders. This is a 2D PROBE, not proof of enclosure: doors, terrain
/// and vertical escape paths still require a separate validation layer.
/// Never use this output as authorization for ZDO world writes.
/// </summary>
internal static class AutomaticPenProbe
{
    private const float SearchRadius = 28f;
    private const int MaxCandidates = 12;
    private const float NearbyRadiusSqr = SearchRadius * SearchRadius;
    private static readonly string[] BarrierTerms =
    {
        "fence", "stakewall", "palisade", "stone_wall",
        "wood_wall", "blackmarble_wall", "grausten_wall"
    };

    private sealed class Piece
    {
        internal ZDO Object = null!;
        internal GameObject Prefab = null!;
        internal bool Gate;
    }

    internal static void LogSweep(ManualLogSource log)
    {
        var scene = ZNetScene.instance;
        var man = ZDOMan.instance;
        var net = ZNet.instance;
        if (scene == null || man == null || net == null || !net.IsServer())
            return;

        var animals = new List<(ZDO zdo, string species)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in scene.m_prefabs)
        {
            if (!prefab || !seen.Add(prefab.name) ||
                prefab.GetComponent<Tameable>() == null) continue;
            var zdos = Scan(prefab.name, out bool complete);
            if (!complete)
            {
                log.LogWarning($"AutoPen candidate scan incomplete for {prefab.name}; aborting.");
                return;
            }
            foreach (var z in zdos)
                if (z != null && z.IsValid() && z.GetBool(ZDOVars.s_tamed))
                    animals.Add((z, prefab.name));
        }

        if (animals.Count == 0)
        {
            log.LogInfo("AutoPen probe: no tamed animals; no candidates. Read-only.");
            return;
        }

        var centerGroups = animals.OrderBy(a => a.zdo.m_uid.ToString(),
            StringComparer.Ordinal).Take(MaxCandidates).ToArray();
        var prefabPieces = new List<Piece>();
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in scene.m_prefabs)
        {
            if (!prefab || !scanned.Add(prefab.name)) continue;
            var name = prefab.name.ToLowerInvariant();
            bool gate = name.Contains("gate") || name.Contains("door");
            if (!gate && !BarrierTerms.Any(name.Contains)) continue;
            // If we cannot prove that a piece is a rigid physical wall,
            // don't let it close the geometry. In particular, doors and
            // gates require current open/closed state and are never used.
            var colliders = prefab.GetComponentsInChildren<BoxCollider>(true);
            if (!gate && (colliders == null || colliders.Length == 0)) continue;
            var zdos = Scan(prefab.name, out bool complete);
            if (!complete)
            {
                log.LogWarning($"AutoPen barrier scan incomplete for {prefab.name}; aborting.");
                return;
            }
            foreach (var z in zdos)
                if (z != null && z.IsValid() && centerGroups.Any(c =>
                    (c.zdo.GetPosition() - z.GetPosition()).sqrMagnitude <= NearbyRadiusSqr))
                    prefabPieces.Add(new Piece { Object = z, Prefab = prefab, Gate = gate });
        }

        int likely = 0;
        var logged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in centerGroups)
        {
            var pos = candidate.zdo.GetPosition();
            var walls = new List<PenGeometry.Wall>();
            int gates = 0, rejected = 0;
            foreach (var piece in prefabPieces)
            {
                if ((piece.Object.GetPosition() - pos).sqrMagnitude > NearbyRadiusSqr)
                    continue;
                if (piece.Gate) { gates++; continue; }
                if (!AddVerifiedSegments(piece, walls))
                    rejected++;
            }

            var result = PenGeometry.Detect(
                new PenGeometry.Point(pos.x, pos.z), walls, 0.25, SearchRadius);
            if (result.Closed) likely++;
            var key = candidate.zdo.m_uid.ToString();
            if (!logged.Add(key)) continue;
            log.LogInfo($"AutoPen probe (2D only): animal={key}, species={candidate.species}, " +
                $"candidate={result.Closed}, reason={result.Reason}, " +
                $"area2D={result.AreaSquareMeters:0.0}m2, segments={walls.Count}, " +
                $"gatesUnverified={gates}, collidersUnverified={rejected}. " +
                "NOT 3D-validated; never authorizes offline writes.");
        }
        log.LogInfo($"AutoPen probe summary: sampledAnimals={centerGroups.Length}, " +
            $"2DCandidates={likely}, nearbyPieces={prefabPieces.Count}, totalTamed={animals.Count}. " +
            "No pen is verified for simulation.");
    }

    private static bool AddVerifiedSegments(Piece piece, List<PenGeometry.Wall> walls)
    {
        var boxes = piece.Prefab.GetComponentsInChildren<BoxCollider>(true);
        if (boxes == null || boxes.Length == 0) return false;
        bool any = false;
        var world = piece.Object.GetPosition();
        var rotation = piece.Object.GetRotation();
        foreach (var box in boxes)
        {
            if (box == null || !box.enabled || box.isTrigger) continue;
            // Convert the prefab collider to the prefab root coordinate
            // frame before applying the saved ZDO rotation.
            var root = piece.Prefab.transform;
            Vector3 offset = root.InverseTransformPoint(
                box.transform.TransformPoint(box.center));
            Vector3 vx = root.InverseTransformVector(
                box.transform.TransformVector(new Vector3(box.size.x, 0, 0)));
            Vector3 vz = root.InverseTransformVector(
                box.transform.TransformVector(new Vector3(0, 0, box.size.z)));
            Vector3 vy = root.InverseTransformVector(
                box.transform.TransformVector(new Vector3(0, box.size.y, 0)));
            // Require at least 0.65m height, a sufficiently long continuous
            // collider, and sufficiently thin horizontal cross-section.
            if (vy.magnitude < 0.65f) continue;
            var major = vx.sqrMagnitude >= vz.sqrMagnitude ? vx : vz;
            var minor = vx.sqrMagnitude >= vz.sqrMagnitude ? vz : vx;
            if (major.magnitude < 0.7f || minor.magnitude > 1.25f)
                continue;
            var center = world + rotation * offset;
            var d = rotation * major * 0.5f;
            walls.Add(new PenGeometry.Wall(
                new PenGeometry.Point(center.x - d.x, center.z - d.z),
                new PenGeometry.Point(center.x + d.x, center.z + d.z), true));
            any = true;
        }
        return any;
    }

    private static List<ZDO> Scan(string name, out bool complete)
    {
        var result = new List<ZDO>();
        int index = 0;
        for (int pass = 0; pass < 1024; pass++)
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, result, ref index))
            {
                complete = true;
                return result;
            }
        complete = false;
        return result;
    }
}
