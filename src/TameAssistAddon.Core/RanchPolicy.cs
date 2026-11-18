using System;
using System.Collections.Generic;
using System.Linq;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Deterministic policy planner. No game objects or drops are created here.
/// A preview never grants authorization for destructive world changes.
/// All actual operations require a separate 3D-enclosure and durable
/// transaction verifier, which is intentionally NOT implemented here.
/// </summary>
public static class RanchPolicy
{
    public enum LifeStage { Adult, Juvenile, Egg }
    public enum Disposition { Wild, Taming, Tamed }
    public enum Action { Ignore, Feed, Tame, Breed, Grow, Hatch, Slaughter }

    public sealed class Animal
    {
        public string Id { get; set; } = "";
        public string Species { get; set; } = "";
        public LifeStage Stage { get; set; }
        public Disposition State { get; set; }
        public int Level { get; set; } = 1;
        public bool Loaded { get; set; }
        public bool Hungry { get; set; }
        public bool Pregnant { get; set; }
        public bool HasBreedingPartner { get; set; }
        public bool ReadyToGrow { get; set; }
        public bool ReadyToHatch { get; set; }
        public bool Protected { get; set; }
    }

    public sealed class Settings
    {
        public int MinBreedersPerSpecies { get; set; } = 2;
        /// <summary>Minimum floor area required by an adult of each species.</summary>
        public Dictionary<string, double> SquareMetersPerAdult { get; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Boar"] = 6, ["Wolf"] = 9, ["Lox"] = 28,
                ["Hen"] = 2, ["Asksvin"] = 18, ["Moose"] = 25
            };
        public double DefaultSquareMetersPerAdult { get; set; } = 12;
        public double YoungAreaFraction { get; set; } = 0.5;
        public double EggAreaFraction { get; set; } = 0.2;
        public double UsableAreaFraction { get; set; } = 0.80;
        public int MinBreedingPopulation { get; set; } = 2;
        public int MaxSlaughtersPerCycle { get; set; } = 2;
        public bool AllowSlaughter { get; set; }
        public bool AllowBreeding { get; set; } = true;
        public bool AllowTaming { get; set; } = true;
        public bool AllowFeeding { get; set; } = true;
    }

    public sealed class Operation
    {
        public string AnimalId { get; set; } = "";
        public string Species { get; set; } = "";
        public Action Type { get; set; }
        public string Reason { get; set; } = "";
        // Always false until authoritative game adapter validates 3D pen
        // and transaction/recovery. A 2D detection never makes this true.
        public bool CanExecute { get; set; }
    }

    public sealed class Result
    {
        public List<Operation> Operations { get; } = new List<Operation>();
        public List<string> Diagnostic { get; } = new List<string>();
        public bool FullyAuthorized { get; internal set; }
        public int SlaughterCandidates => Operations.Count(x => x.Type == Action.Slaughter);
    }

    /// <summary>
    /// Candidates only when physically inside a detected 2D contour.
    /// A closed 2D contour is NOT proof of real fenced confinement.
    /// </summary>
    public static Result Preview(IReadOnlyList<Animal> inside2d,
        bool candidateClosed2d, double interiorSquareMeters, Settings settings)
    {
        if (inside2d == null) throw new ArgumentNullException(nameof(inside2d));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings.MinBreedersPerSpecies < 2 ||
            settings.DefaultSquareMetersPerAdult <= 0 ||
            settings.SquareMetersPerAdult.Values.Any(v => v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) ||
            settings.UsableAreaFraction <= 0 || settings.UsableAreaFraction > 1 ||
            settings.YoungAreaFraction <= 0 || settings.YoungAreaFraction > 1 ||
            settings.EggAreaFraction <= 0 || settings.EggAreaFraction > 1 ||
            settings.MaxSlaughtersPerCycle < 0 ||
            settings.MaxSlaughtersPerCycle > 20)
            throw new ArgumentOutOfRangeException(nameof(settings));

        var result = new Result();
        if (!candidateClosed2d || double.IsNaN(interiorSquareMeters) ||
            double.IsInfinity(interiorSquareMeters) || interiorSquareMeters <= 0)
        {
            result.Diagnostic.Add("open-or-unverified-2d: no management candidates");
            return result;
        }

        // Space is shared across species in the same pen. A wolf and a
        // boar consume different area, but not two independent pen budgets.
        var occupants = inside2d
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id) &&
                !string.IsNullOrWhiteSpace(a.Species))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.First()).ToArray();
        double allowedArea = interiorSquareMeters * settings.UsableAreaFraction;
        double occupiedArea = occupants.Sum(a => AreaFor(a, settings));
        result.Diagnostic.Add("penArea=" +
            interiorSquareMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
            ", usableArea=" + allowedArea.ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture) +
            ", occupiedArea=" + occupiedArea.ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture));

        foreach (var group in occupants
            .GroupBy(a => a.Species, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var adults = group.Where(a => a.Stage == LifeStage.Adult &&
                a.State == Disposition.Tamed).ToList();
            int adultCount = adults.Count;
            int childCount = group.Count(a => a.Stage != LifeStage.Adult);
            int wildCount = group.Count(a => a.State == Disposition.Wild &&
                a.Stage == LifeStage.Adult);
            double speciesSpace = group.Sum(a => AreaFor(a, settings));
            result.Diagnostic.Add(group.Key + ": tamedAdults=" + adultCount +
                ", youngOrEggs=" + childCount + ", wild=" + wildCount +
                ", usedSquareMeters=" + speciesSpace.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture));

            foreach (var a in group.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (a.Protected || a.Loaded) continue;
                if (a.Stage == LifeStage.Egg)
                {
                    if (a.ReadyToHatch)
                        Add(result, a, Action.Hatch, "egg requires validated incubation");
                    continue;
                }
                if (a.Stage == LifeStage.Juvenile)
                {
                    if (a.ReadyToGrow)
                        Add(result, a, Action.Grow, "young animal fully matured");
                    continue;
                }
                if (a.State != Disposition.Tamed)
                {
                    if (settings.AllowTaming)
                        Add(result, a, Action.Tame,
                            "wild/partly-tamed adult inside 2D candidate");
                    if (a.Hungry && settings.AllowFeeding)
                        Add(result, a, Action.Feed, "requires species-specific food");
                    continue;
                }
                if (a.Hungry && settings.AllowFeeding)
                    Add(result, a, Action.Feed, "adult hungry");
                if (settings.AllowBreeding && a.HasBreedingPartner &&
                    adultCount >= settings.MinBreedingPopulation &&
                    occupiedArea + AreaFor(a, settings) *
                        settings.YoungAreaFraction <= allowedArea)
                    Add(result, a, Action.Breed,
                        "requires vanilla partner, area and population validation");
            }
        }

        // First estimate shared space, then select only tamed surplus
        // adults. Preserve minimum breeders per species AND protected or
        // loaded animals. Wild and juvenile occupants count toward space
        // but are never slaughter candidates.
        if (settings.AllowSlaughter && occupiedArea > allowedArea)
        {
            var groups = occupants.GroupBy(a => a.Species,
                    StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(),
                    StringComparer.Ordinal);
            var eligible = new List<Animal>();
            foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var adults = group.Value.Where(a =>
                    a.Stage == LifeStage.Adult &&
                    a.State == Disposition.Tamed).ToArray();
                int surplus = Math.Max(0,
                    adults.Length - settings.MinBreedersPerSpecies);
                if (surplus == 0) continue;
                // Keep high-level tame adults as breeders; never include
                // loaded/protected animals in the slaughter pool.
                eligible.AddRange(adults.Where(a => !a.Loaded && !a.Protected)
                    .OrderBy(a => a.Level).ThenBy(a => a.Id,
                        StringComparer.Ordinal).Take(surplus));
            }

            foreach (var target in eligible
                .OrderBy(a => a.Level)
                .ThenByDescending(a => AreaFor(a, settings))
                .ThenBy(a => a.Species, StringComparer.Ordinal)
                .ThenBy(a => a.Id, StringComparer.Ordinal))
            {
                if (occupiedArea <= allowedArea ||
                    result.SlaughterCandidates >= settings.MaxSlaughtersPerCycle)
                    break;
                Add(result, target, Action.Slaughter,
                    "area over capacity; preserve species breeding stock");
                occupiedArea -= AreaFor(target, settings);
            }
        }

        result.Diagnostic.Add("PREVIEW ONLY: no verified 3D pen, no slaughter/drop transaction");
        return result;
    }

    private static double AreaFor(Animal a, Settings settings)
    {
        double adult = settings.SquareMetersPerAdult.TryGetValue(a.Species,
            out var value) ? value : settings.DefaultSquareMetersPerAdult;
        return a.Stage == LifeStage.Egg ? adult * settings.EggAreaFraction :
            a.Stage == LifeStage.Juvenile ? adult * settings.YoungAreaFraction :
            adult;
    }

    private static void Add(Result result, Animal animal,
        Action action, string reason) =>
        result.Operations.Add(new Operation
        {
            AnimalId = animal.Id, Species = animal.Species,
            Type = action, Reason = reason, CanExecute = false
        });
}
