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
        public int MaxAdultsPerSpecies { get; set; } = 6;
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
        bool candidateClosed2d, Settings settings)
    {
        if (inside2d == null) throw new ArgumentNullException(nameof(inside2d));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings.MinBreedersPerSpecies < 2 ||
            settings.MaxAdultsPerSpecies < settings.MinBreedersPerSpecies ||
            settings.MaxSlaughtersPerCycle < 0 ||
            settings.MaxSlaughtersPerCycle > 20)
            throw new ArgumentOutOfRangeException(nameof(settings));

        var result = new Result();
        if (!candidateClosed2d)
        {
            result.Diagnostic.Add("open-or-unverified-2d: no management candidates");
            return result;
        }

        foreach (var group in inside2d
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id) &&
                !string.IsNullOrWhiteSpace(a.Species))
            .GroupBy(a => a.Species, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Multiple species can share a pen, but cannot be counted as
            // interchangeable partners or surplus adults.
            var adult = group.Where(a => a.Stage == LifeStage.Adult &&
                a.State == Disposition.Tamed).ToList();
            int tamedAdults = adult.Count;
            int childCount = group.Count(a => a.Stage != LifeStage.Adult);
            int wildCount = group.Count(a => a.State == Disposition.Wild &&
                a.Stage == LifeStage.Adult);
            result.Diagnostic.Add(group.Key + ": tamedAdults=" + tamedAdults +
                ", youngOrEggs=" + childCount + ", wild=" + wildCount);

            foreach (var a in group.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (a.Protected || a.Loaded)
                    continue;
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
                    tamedAdults >= settings.MinBreedingPopulation &&
                    tamedAdults + childCount < settings.MaxAdultsPerSpecies)
                    Add(result, a, Action.Breed,
                        "requires vanilla partner and population validation");
            }

            if (!settings.AllowSlaughter || tamedAdults <= settings.MaxAdultsPerSpecies)
                continue;

            int surplus = Math.Max(0,
                Math.Min(tamedAdults - settings.MaxAdultsPerSpecies,
                    tamedAdults - settings.MinBreedersPerSpecies));
            // Preserve the highest-level adults as potential breeders.
            // Never slaughter wild, taming, juvenile, or protected animals.
            foreach (var a in adult.Where(a => !a.Loaded && !a.Protected)
                .OrderBy(a => a.Level)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .Take(Math.Min(surplus,
                    Math.Max(0, settings.MaxSlaughtersPerCycle -
                        result.SlaughterCandidates))))
                Add(result, a, Action.Slaughter,
                    "tamed surplus adult; retain breeding stock");
        }

        result.Diagnostic.Add("PREVIEW ONLY: no verified 3D pen, no slaughter/drop transaction");
        return result;
    }

    private static void Add(Result result, Animal animal,
        Action action, string reason) =>
        result.Operations.Add(new Operation
        {
            AnimalId = animal.Id, Species = animal.Species,
            Type = action, Reason = reason, CanExecute = false
        });
}
