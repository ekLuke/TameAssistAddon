using System;
using System.Collections.Generic;

namespace TheLair.TameAssistAddon.Core;

public sealed class SimulationSettings
{
    public double SpeedPercent { get; set; } = 50;
    public double MaxCatchupHours { get; set; } = 12;
    public double FeedRadius { get; set; } = 8;
    public double PartnerRadius { get; set; } = 10;
    public double PopulationRadius { get; set; } = 10;
    public int MaxAnimalsNearby { get; set; } = 4;
    public double FedDurationSeconds { get; set; } = 600;
    public double TamingSeconds { get; set; } = 1800;
    public double PregnancySeconds { get; set; } = 600;
    public double BreedCooldownSeconds { get; set; } = 120;
    public double GrowthSeconds { get; set; } = 3000;
    public int MinFoodRemaining { get; set; } = 1;
}
public sealed class AnimalSnapshot
{
    public string Id { get; set; } = "";
    public string Species { get; set; } = "Boar";
    public int Level { get; set; } = 1;
    public bool Loaded { get; set; }
    public bool Tamed { get; set; }
    public bool Juvenile { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double LastFedAt { get; set; }
    public double TamingProgressSeconds { get; set; }
    public double PregnancyStartedAt { get; set; } = -1;
    public double LastBreedAt { get; set; } = -1;
    public double BornAt { get; set; }
    public double CheckpointAt { get; set; }
}
public sealed class FoodSnapshot
{
    public string Id { get; set; } = "";
    public string Item { get; set; } = "";
    public int Amount { get; set; }
    public bool IsContainer { get; set; }
    public bool Loaded { get; set; }
    public bool InUse { get; set; }
    public bool ServerOwned { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
public enum ChangeKind { ConsumeFood, Feed, TamingProgress, Tamed, Pregnancy, Birth, GrowUp, Checkpoint }
public sealed class PlannedChange
{
    public ChangeKind Kind { get; set; }
    public string AnimalId { get; set; } = "";
    public string? ResourceId { get; set; }
    public double Number { get; set; }
    public string? Note { get; set; }
}
public sealed class SimulationResult
{
    public List<PlannedChange> Changes { get; } = new List<PlannedChange>();
    public List<string> Diagnostics { get; } = new List<string>();
}
