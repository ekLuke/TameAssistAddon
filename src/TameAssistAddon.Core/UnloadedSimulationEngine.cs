using System;
using System.Collections.Generic;
using System.Linq;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Deterministic, side-effect-free planner. It never writes ZDOs or inventory.
/// The game adapter must validate ownership and atomically commit each proposed change.
/// These times are modelling defaults, not authoritative vanilla constants.
/// </summary>
public sealed class UnloadedSimulationEngine
{
    public SimulationResult Plan(
        IReadOnlyList<AnimalSnapshot> animals,
        IReadOnlyList<FoodSnapshot> food,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edibleBySpecies,
        SimulationSettings config,
        double now)
    {
        if (config.SpeedPercent < 0 || config.SpeedPercent > 200 ||
            config.MaxCatchupHours < 0 || config.FeedRadius < 0 ||
            config.MaxAnimalsNearby < 1 || config.TamingSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config));

        var result = new SimulationResult();
        // Local copies prevent plans for one animal consuming the same food twice.
        var available = food.ToDictionary(f => f.Id, f => f.Amount);
        var reservedBirths = new List<AnimalSnapshot>();
        foreach (var a in animals.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (a.Loaded || a.Id.Length == 0 || a.CheckpointAt > now)
                continue;
            var elapsed = Math.Min(now - a.CheckpointAt, config.MaxCatchupHours * 3600);
            if (elapsed <= 0) continue;
            var simulated = elapsed * config.SpeedPercent / 100;
            if (simulated <= 0) continue;
            var current = a.CheckpointAt + simulated;
            var fedAt = a.LastFedAt;
            var fed = fedAt > 0 && fedAt <= current && current - fedAt < config.FedDurationSeconds;
            if (!fed && edibleBySpecies.TryGetValue(a.Species, out var edible))
            {
                var source = food.Where(f =>
                    !f.Loaded && !f.InUse && f.ServerOwned &&
                    edible.Contains(f.Item) &&
                    DistanceSquared(a.X, a.Y, a.Z, f.X, f.Y, f.Z) <= config.FeedRadius * config.FeedRadius &&
                    available[f.Id] > (f.IsContainer ? config.MinFoodRemaining : 0))
                    .OrderBy(f => DistanceSquared(a.X, a.Y, a.Z, f.X, f.Y, f.Z))
                    .ThenBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault();
                if (source != null)
                {
                    available[source.Id]--;
                    result.Changes.Add(new PlannedChange { Kind = ChangeKind.ConsumeFood, AnimalId = a.Id, ResourceId = source.Id, Number = 1 });
                    result.Changes.Add(new PlannedChange { Kind = ChangeKind.Feed, AnimalId = a.Id, Number = current });
                    fed = true;
                    fedAt = current;
                }
            }
            if (!a.Tamed && !a.Juvenile && fed)
            {
                var progress = a.TamingProgressSeconds + simulated;
                result.Changes.Add(new PlannedChange { Kind = ChangeKind.TamingProgress, AnimalId = a.Id, Number = Math.Min(progress, config.TamingSeconds) });
                if (progress >= config.TamingSeconds)
                    result.Changes.Add(new PlannedChange { Kind = ChangeKind.Tamed, AnimalId = a.Id });
            }
            if (a.Juvenile && current - a.BornAt >= config.GrowthSeconds)
                result.Changes.Add(new PlannedChange { Kind = ChangeKind.GrowUp, AnimalId = a.Id });
            if (a.Tamed && !a.Juvenile && fed)
            {
                if (a.PregnancyStartedAt >= 0 && current - a.PregnancyStartedAt >= config.PregnancySeconds)
                {
                    var population = animals.Concat(reservedBirths).Count(x => x.Species == a.Species &&
                        DistanceSquared(a.X, a.Y, a.Z, x.X, x.Y, x.Z) <= config.PopulationRadius * config.PopulationRadius);
                    if (population < config.MaxAnimalsNearby)
                    {
                        result.Changes.Add(new PlannedChange { Kind = ChangeKind.Birth, AnimalId = a.Id, Number = current, Note = "Requires verified vanilla prefab/ZDO spawning, level inheritance and idempotency" });
                        reservedBirths.Add(new AnimalSnapshot { Species = a.Species, X = a.X, Y = a.Y, Z = a.Z });
                    }
                    else result.Diagnostics.Add(a.Id + ": pen full");
                }
                else if (a.PregnancyStartedAt < 0 && (a.LastBreedAt < 0 || current - a.LastBreedAt >= config.BreedCooldownSeconds))
                {
                    bool partner = animals.Any(x => x.Id != a.Id && !x.Juvenile && x.Tamed &&
                        x.Species == a.Species && !x.Loaded &&
                        x.LastFedAt > 0 && x.LastFedAt <= current && current - x.LastFedAt < config.FedDurationSeconds &&
                        DistanceSquared(a.X, a.Y, a.Z, x.X, x.Y, x.Z) <= config.PartnerRadius * config.PartnerRadius);
                    if (partner)
                        result.Changes.Add(new PlannedChange { Kind = ChangeKind.Pregnancy, AnimalId = a.Id, Number = current, Note = "Deterministic proposal, not vanilla random love-point mechanic" });
                    else result.Diagnostics.Add(a.Id + ": no eligible partner");
                }
            }
            result.Changes.Add(new PlannedChange { Kind = ChangeKind.Checkpoint, AnimalId = a.Id, Number = now });
        }
        return result;
    }

    private static double DistanceSquared(double ax, double ay, double az, double bx, double by, double bz)
    {
        var dx = ax - bx; var dy = ay - by; var dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }
}
