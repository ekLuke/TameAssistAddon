using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Read-only discovery of potential pens from ZDO positions and prefab
/// BoxColliders. This is a 2D PROBE, not proof of enclosure: gate animation,
/// terrain and vertical escape paths still require a separate validation layer.
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

    internal static void LogSweep(ManualLogSource log, bool showSigns, bool signDebug, RanchPolicy.Settings ranchSettings)
    {
        var scene = ZNetScene.instance;
        var man = ZDOMan.instance;
        var net = ZNet.instance;
        if (scene == null || man == null || net == null || !net.IsServer())
        {
            VerifiedPenFeedCandidates.Replace(Array.Empty<ZDOID>());
            return;
        }
        var eligibleWild = new HashSet<ZDOID>();

        var animals = new List<(ZDO zdo, string species, string state)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in scene.m_prefabs)
        {
            if (!prefab || !seen.Add(prefab.name) ||
                prefab.GetComponent<Tameable>() == null &&
                prefab.GetComponent<Procreation>() == null) continue;
            var zdos = Scan(prefab.name, out bool complete);
            if (!complete)
            {
                log.LogWarning($"AutoPen candidate scan incomplete for {prefab.name}; aborting.");
                return;
            }
            var tameable = prefab.GetComponent<Tameable>();
            foreach (var z in zdos)
            {
                if (z == null || !z.IsValid()) continue;
                bool tamed = z.GetBool(ZDOVars.s_tamed, false);
                bool taming = false;
                if (!tamed && tameable != null)
                {
                    float fullTime = z.GetFloat("tamingTime", tameable.m_tamingTime);
                    float remaining = z.GetFloat(ZDOVars.s_tameTimeLeft, fullTime);
                    taming = fullTime > 0 && remaining >= 0 && remaining < fullTime;
                }
                animals.Add((z, prefab.name, tamed ? "tamed" : taming ? "taming" : "wild"));
            }
        }

        if (animals.Count == 0)
        {
            log.LogInfo("AutoPen probe: no breedable or tameable adult animals; no candidates. Read-only.");
            VerifiedPenFeedCandidates.Replace(eligibleWild);
            BarrierSigns.Sync(log, Array.Empty<BarrierSigns.Target>(), showSigns, signDebug);
            return;
        }

        // Collect candidate barriers near any eligible animal first.
        // Sampling the first four wild ZDOIDs would often pick unpenned
        // animals on the other side of the world and miss wild-only pens.
        var prefabPieces = new List<Piece>();
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in scene.m_prefabs)
        {
            if (!prefab || !scanned.Add(prefab.name)) continue;
            var name = prefab.name.ToLowerInvariant();
            bool gate = name.Contains("gate") || name.Contains("door");
            if (!gate && !BarrierTerms.Any(name.Contains)) continue;
            // Gate prefab geometry may be evaluated in its closed-pose
            // coordinate frame, but only if it has a vanilla Door component
            // and the saved state is closed. Still read-only / 2D candidate.
            var colliders = prefab.GetComponentsInChildren<BoxCollider>(true);
            if (!gate && (colliders == null || colliders.Length == 0)) continue;
            var zdos = Scan(prefab.name, out bool complete);
            if (!complete)
            {
                log.LogWarning($"AutoPen barrier scan incomplete for {prefab.name}; aborting.");
                return;
            }
            foreach (var z in zdos)
                if (z != null && z.IsValid() && animals.Any(a =>
                    (a.zdo.GetPosition() - z.GetPosition()).sqrMagnitude <= NearbyRadiusSqr))
                    prefabPieces.Add(new Piece { Object = z, Prefab = prefab, Gate = gate });
        }

        // Prefer animals actually close to barrier construction, even when
        // entirely wild. Deterministic tie-breaking keeps log samples stable.
        var closeToWalls = animals.Select(a => new
        {
            Animal = a,
            Barriers = prefabPieces.Count(p =>
                (p.Object.GetPosition() - a.zdo.GetPosition()).sqrMagnitude <=
                NearbyRadiusSqr)
        }).ToArray();
        var centerGroups = closeToWalls
            .Where(a => a.Animal.state != "wild")
            .OrderByDescending(a => a.Barriers)
            .ThenBy(a => a.Animal.zdo.m_uid.ToString(), StringComparer.Ordinal)
            .Take(MaxCandidates - 4)
            .Concat(closeToWalls.Where(a => a.Animal.state == "wild" && a.Barriers > 0)
                .OrderByDescending(a => a.Barriers)
                .ThenBy(a => a.Animal.zdo.m_uid.ToString(), StringComparer.Ordinal)
                .Take(4))
            .Select(a => a.Animal).ToArray();

        int likely = 0;
        var signTargets = new List<BarrierSigns.Target>();
        var logged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in centerGroups)
        {
            var pos = candidate.zdo.GetPosition();
            var walls = new List<PenGeometry.Wall>();
            int gatesOpen = 0, gatesClosed = 0, gatesUnverified = 0, rejected = 0;
            var gateDetails = new List<string>();
            var rejectedDetails = new HashSet<string>(StringComparer.Ordinal);
            foreach (var piece in prefabPieces)
            {
                if ((piece.Object.GetPosition() - pos).sqrMagnitude > NearbyRadiusSqr)
                    continue;
                if (piece.Gate)
                {
                    // Vanilla Door's ZDO "state" is 0 for closed and nonzero
                    // for open (including left/right hinged leaf variants).
                    // Never treat an unknown gate prefab as closed.
                    if (piece.Prefab.GetComponent<Door>() == null)
                    {
                        gatesUnverified++;
                        if (gateDetails.Count < 8)
                            gateDetails.Add(piece.Prefab.name + ":unknown-prefab");
                        continue;
                    }
                    int state = piece.Object.GetInt(ZDOVars.s_state, 0);
                    if (state != 0)
                    {
                        gatesOpen++;
                        if (gateDetails.Count < 8)
                            gateDetails.Add(piece.Prefab.name + ":open(" + state + ")");
                        continue;
                    }
                    // This covers only the prefab's closed *2D pose*.
                    // No authoritative collision/vertical test is performed.
                    if (AddVerifiedSegments(piece, walls))
                    {
                        gatesClosed++;
                        if (gateDetails.Count < 8)
                            gateDetails.Add(piece.Prefab.name + ":closed");
                    }
                    else
                    {
                        gatesUnverified++;
                        if (gateDetails.Count < 8)
                            gateDetails.Add(piece.Prefab.name + ":closed-no-box");
                    }
                    continue;
                }
                if (!AddVerifiedSegments(piece, walls))
                {
                    rejected++;
                    if (rejectedDetails.Count < 8)
                        rejectedDetails.Add(piece.Prefab.name);
                }
            }

            var result = PenGeometry.Detect(
                new PenGeometry.Point(pos.x, pos.z), walls, 0.25, SearchRadius);
            if (result.Closed) likely++;
            // Include ALL species and wild animals physically located inside
            // this 2D candidate. Still no world writes: terrain, gates and
            // 3D clearance are not yet independently verified.
            var enclosed = result.Closed
                ? animals.Where(a => result.Contains(new PenGeometry.Point(
                    a.zdo.GetPosition().x, a.zdo.GetPosition().z))).ToArray()
                : Array.Empty<(ZDO zdo, string species, string state)>();
            // For test-only initial feeding, require a closed 2D contour,
            // no open/unverified gates, and no rejected wall colliders.
            // This rejects uncontained wilderness and unknown barriers.
            if (result.Closed && gatesOpen == 0 && gatesUnverified == 0 &&
                rejected == 0 && result.AreaSquareMeters >= 4 &&
                result.AreaSquareMeters <= 2000)
            {
                foreach (var a in enclosed.Where(a => a.state == "wild" &&
                    scene.FindInstance(a.zdo) != null))
                    eligibleWild.Add(a.zdo.m_uid);
            }
            int wildInside = enclosed.Count(a => a.state == "wild");
            int tamingInside = enclosed.Count(a => a.state == "taming");
            int tamedInside = enclosed.Count(a => a.state == "tamed");
            // Preview is intentionally 2D-only. No world modification or
            // drop creation can be performed from this policy output.
            var planned = RanchPolicy.Preview(enclosed.Select(a =>
                new RanchPolicy.Animal
                {
                    Id = a.zdo.m_uid.ToString(),
                    Species = a.species,
                    State = a.state == "tamed" ? RanchPolicy.Disposition.Tamed :
                        a.state == "taming" ? RanchPolicy.Disposition.Taming :
                        RanchPolicy.Disposition.Wild,
                    Stage = RanchPolicy.LifeStage.Adult,
                    Loaded = scene.FindInstance(a.zdo) != null,
                    Level = a.zdo.GetInt(ZDOVars.s_level, 1),
                    Hungry = false
                }).ToArray(), result.Closed, result.AreaSquareMeters, ranchSettings);
            var key = candidate.zdo.m_uid.ToString();
            if (!logged.Add(key)) continue;

            // A single nearby barrier per animal is enough to display
            // diagnostics. The target barrier ZDO ID deduplicates signs
            // when several animals share the same fence section.
            var targetBarrier = prefabPieces.Where(p =>
                p.Object != null && p.Object.IsValid() &&
                (p.Object.GetPosition() - pos).sqrMagnitude <= NearbyRadiusSqr)
                .OrderBy(p => (p.Object.GetPosition() - pos).sqrMagnitude)
                .ThenBy(p => p.Object.m_uid.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (targetBarrier != null)
            {
                string prefabName = targetBarrier.Prefab.name;
                if (prefabName.Length > 26) prefabName = prefabName.Substring(0, 26);
                string dims = BarrierDimensionTag(targetBarrier.Prefab);
                // Keep within the vanilla sign's short readable text size.
                string signText =
                    "Barriera: " + prefabName + "\n" +
                    dims + "\n" +
                    "Recinto: " + result.Reason + "\n" +
                    "Seg: " + walls.Count + " Porte C/A/?: " +
                    gatesClosed + "/" + gatesOpen + "/" + gatesUnverified + "\n" +
                    "Animali S/T/D: " + wildInside + "/" + tamingInside + "/" + tamedInside;
                // Pass all local barrier ZDOs to the sign manager. A
                // manually placed sign on a *different section* of the same
                // pen must suppress the diagnostic auto sign for the group.
                var nearbyBarriers = prefabPieces.Where(p =>
                    p.Object != null && p.Object.IsValid() &&
                    (p.Object.GetPosition() - pos).sqrMagnitude <= NearbyRadiusSqr)
                    .Select(p => p.Object).GroupBy(z => z.m_uid)
                    .Select(g => g.First()).ToArray();
                signTargets.Add(new BarrierSigns.Target
                {
                    Animal = candidate.zdo,
                    Barrier = targetBarrier.Object,
                    Viewer = pos,
                    Text = signText,
                    NearbyBarriers = nearbyBarriers
                });
            }
            if (planned.SlaughterCandidates > 0)
                log.LogInfo($"Ranch slaughter PREVIEW ONLY: animalSeed={key}, " +
                    $"candidates={string.Join(",", planned.Operations.Where(o =>
                        o.Type == RanchPolicy.Action.Slaughter).Select(o => o.AnimalId))}, " +
                    "no kill, no drops; 3D enclosure and journal not verified.");
            log.LogInfo($"AutoPen probe (2D only): animal={key}, species={candidate.species}, " +
                $"animalState={candidate.state}, candidate={result.Closed}, reason={result.Reason}, " +
                $"animalsInside2D={enclosed.Length}, wildInside2D={wildInside}, " +
                $"tamingInside2D={tamingInside}, tamedInside2D={tamedInside}, " +
                $"area2D={result.AreaSquareMeters:0.0}m2, segments={walls.Count}, " +
                $"escape2D={(result.FirstEscape.HasValue ? result.FirstEscape.Value.X.ToString("0.00") + "," + result.FirstEscape.Value.Z.ToString("0.00") : "none")}, " +
                $"slaughterPreview={planned.SlaughterCandidates}, executable=0, " +
                $"gatesClosed2D={gatesClosed}, gatesOpen={gatesOpen}, " +
                $"gatesUnverified={gatesUnverified}, collidersUnverified={rejected}, " +
                $"gateDetails=[{string.Join(",", gateDetails)}], " +
                $"rejectedPrefabs=[{string.Join(",", rejectedDetails.OrderBy(n => n, StringComparer.Ordinal))}]. " +
                "NOT 3D-validated; never authorizes offline writes.");
        }
        VerifiedPenFeedCandidates.Replace(eligibleWild);
        log.LogInfo($"AutoPen wild first-feed candidates={eligibleWild.Count} " +
            "(2D closed and no open/unknown doors, test-only; NOT 3D verified).");
        BarrierSigns.Sync(log, signTargets, showSigns, signDebug);
        log.LogInfo($"AutoPen probe summary: sampledAnimals={centerGroups.Length}, " +
            $"2DCandidates={likely}, nearbyPieces={prefabPieces.Count}, " +
            $"totalAdults={animals.Count}, wildAdults={animals.Count(a => a.state == "wild")}, " +
            $"wildNearBarriers={closeToWalls.Count(a => a.Animal.state == "wild" && a.Barriers > 0)}. " +
            "No pen is verified for simulation.");
    }

    private static string BarrierDimensionTag(GameObject prefab)
    {
        // Approximate prefab-local dimensions only; never interpret these
        // as authoritative 3D escape clearance.
        float length = 0f, height = 0f;
        foreach (var box in prefab.GetComponentsInChildren<BoxCollider>(true))
        {
            if (box == null || !box.enabled || box.isTrigger) continue;
            Vector3 size = Vector3.Scale(box.size, box.transform.lossyScale);
            length = Mathf.Max(length, Mathf.Abs(size.x), Mathf.Abs(size.z));
            height = Mathf.Max(height, Mathf.Abs(size.y));
        }
        return length > 0f && height > 0f
            ? $"L~{length:0.0}m H~{height:0.0}m"
            : "Dimensioni: non verificate";
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
