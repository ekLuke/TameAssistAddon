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
    ("Stable deterministic plan", Deterministic),
    ("TameAssist config defaults", ConfigDefaults),
    ("TameAssist config values", ConfigValues),
    ("Closed rectangular fence candidate", ClosedPen),
    ("Open gate rejects enclosure", OpenGate),
    ("Broken fence rejects enclosure", BrokenPen),
    ("Unverified barrier cannot close enclosure", UnverifiedFence),
    ("Separated pens reject cross-membership", SeparatePens)
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

static void ConfigDefaults() {
    var c = TameAssistConfiguration.Parse("# empty\n");
    Check(!c.FeedFromContainers && c.FeedFromContainersLeaveAtLeast == 1 && c.FedDurationMultiplier == 1f);
}
static void ConfigValues() {
    var c = TameAssistConfiguration.Parse("[General]\\nFeedFromContainers = true\\nFeedFromContainersRange = 8.5\\nFeedFromContainersLeaveAtLeast = 2\\nFedDurationMultiplier = 1.5\\nTamingTimeMultiplier = 0.5".Replace("\\n", "\n"));
    Check(c.FeedFromContainers && c.FeedFromContainersRange == 8.5f && c.FeedFromContainersLeaveAtLeast == 2 && c.FedDurationMultiplier == 1.5f && c.TamingTimeMultiplier == 0.5f);
}

static PenGeometry.Wall Wall(double ax, double az, double bx, double bz,
    bool verified=true) =>
    new(new PenGeometry.Point(ax,az), new PenGeometry.Point(bx,bz), verified);

static PenGeometry.Wall[] BoxWalls(double x, double z) => new[]
{
    Wall(x,z,x+5,z), Wall(x+5,z,x+5,z+5),
    Wall(x+5,z+5,x,z+5), Wall(x,z+5,x,z)
};
static void ClosedPen()
{
    var res = PenGeometry.Detect(new(2.5,2.5), BoxWalls(0,0), 0.25, 8);
    Check(res.Closed && res.Contains(new(2,2)) &&
        !res.Contains(new(7,7)) && res.AreaSquareMeters > 20);
}
static void OpenGate()
{
    var walls = new[] { Wall(0,0,2,0), Wall(3,0,5,0),
        Wall(5,0,5,5), Wall(5,5,0,5), Wall(0,5,0,0) };
    Check(!PenGeometry.Detect(new(2.5,2.5), walls, 0.25, 8).Closed);
}
static void BrokenPen()
{
    var walls = new[] { Wall(0,0,5,0), Wall(5,0,5,5),
        Wall(5,5,0,5) };
    Check(!PenGeometry.Detect(new(2.5,2.5), walls, 0.25, 8).Closed);
}
static void UnverifiedFence()
{
    var walls = new[] { Wall(0,0,5,0), Wall(5,0,5,5),
        Wall(5,5,0,5), Wall(0,5,0,0,false) };
    Check(!PenGeometry.Detect(new(2.5,2.5), walls, 0.25, 8).Closed);
}
static void SeparatePens()
{
    var walls = BoxWalls(0,0).Concat(BoxWalls(8,0)).ToArray();
    var res = PenGeometry.Detect(new(2.5,2.5), walls, 0.25, 16);
    Check(res.Closed && res.Contains(new(2.5,2.5)) &&
        !res.Contains(new(10,2.5)));
}
