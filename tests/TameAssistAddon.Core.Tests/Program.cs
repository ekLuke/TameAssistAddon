using System;
using System.Collections.Generic;
using System.Linq;
using TheLair.TameAssistAddon.Core;

var tests = new (string Name, Action Run)[]
{
    ("Loaded animals ignored", LoadedIgnored),
    ("No duplicate food consumption", NoDuplicateFood),
    ("Container reserve respected", ReserveRespected),
    ("Unowned containers skipped", UnownedSkipped),
    ("No partner no pregnancy", NoPartner),
    ("Pen capacity blocks births", PenFull),
    ("Catchup bounded", CatchupBounded),
    ("Stable deterministic plan", Deterministic)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
return failures == 0 ? 0 : 1;

static AnimalSnapshot Boar(string id) => new() { Id = id, Species = "Boar", CheckpointAt = 100, LastFedAt = -1000 };
static FoodSnapshot Chest(int amount, bool owner = true) => new() { Id = "chest", Item = "Raspberry", Amount = amount, IsContainer = true, ServerOwned = owner };
static SimulationResult Plan(AnimalSnapshot[] a, FoodSnapshot[]? food = null, SimulationSettings? cfg = null) =>
    new UnloadedSimulationEngine().Plan(a, food ?? Array.Empty<FoodSnapshot>(),
        new Dictionary<string, IReadOnlyCollection<string>> { ["Boar"] = new[] { "Raspberry" } },
        cfg ?? new SimulationSettings(), 200);
static void Check(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
static void LoadedIgnored() { var a = Boar("a"); a.Loaded = true; Check(Plan(new[] { a }).Changes.Count == 0); }
static void NoDuplicateFood() {
    var p = Plan(new[] { Boar("a"), Boar("b") }, new[] { Chest(2) });
    Check(p.Changes.Count(c => c.Kind == ChangeKind.ConsumeFood) == 1);
}
static void ReserveRespected() { Check(!Plan(new[] { Boar("a") }, new[] { Chest(1) }).Changes.Any(c => c.Kind == ChangeKind.ConsumeFood)); }
static void UnownedSkipped() { Check(!Plan(new[] { Boar("a") }, new[] { Chest(3, false) }).Changes.Any(c => c.Kind == ChangeKind.ConsumeFood)); }
static void NoPartner() { var a=Boar("a"); a.Tamed = true; a.LastFedAt = 120; Check(!Plan(new[]{a}).Changes.Any(c => c.Kind == ChangeKind.Pregnancy)); }
static void PenFull() {
    var a = Boar("a"); a.Tamed=true; a.LastFedAt=120; a.PregnancyStartedAt=100;
    var b = Boar("b"); b.Tamed=true;
    var cfg=new SimulationSettings { MaxAnimalsNearby=2 };
    Check(!Plan(new[]{a,b}, null,cfg).Changes.Any(c => c.Kind == ChangeKind.Birth));
}
static void CatchupBounded() {
    var a=Boar("a"); a.TamingProgressSeconds=0; a.LastFedAt=100;
    var cfg=new SimulationSettings { MaxCatchupHours=1.0/3600.0, SpeedPercent=100 };
    Check(Plan(new[]{a}, null,cfg).Changes.Single(c=>c.Kind==ChangeKind.TamingProgress).Number <= 1.0001);
}
static void Deterministic() {
    var a=new[]{Boar("b"),Boar("a")};
    var x=Plan(a,new[]{Chest(3)}).Changes.Select(c=>c.Kind+":"+c.AnimalId+":"+c.ResourceId);
    var y=Plan(a,new[]{Chest(3)}).Changes.Select(c=>c.Kind+":"+c.AnimalId+":"+c.ResourceId);
    Check(x.SequenceEqual(y));
}
