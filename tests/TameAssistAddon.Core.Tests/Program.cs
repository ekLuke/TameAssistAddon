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
    ("Blank/default sign placeholders", BlankSign),
    ("Custom signs are preserved", CustomSign),
    ("Repair only previously generated sign prefix", GeneratedPrefixRepair),
    ("Closed rectangular fence candidate", ClosedPen),
    ("Open gate rejects enclosure", OpenGate),
    ("Closed gate completes enclosure", ClosedGate),
    ("Broken fence rejects enclosure", BrokenPen),
    ("Unverified barrier cannot close enclosure", UnverifiedFence),
    ("Separated pens reject cross-membership", SeparatePens),
    ("Open enclosure cannot plan slaughter", OpenPenNoSlaughter),
    ("Mixed-species slaughter preserves breeders", MixedSlaughter),
    ("Slaughter never targets babies or wild", OnlySurplusAdults),
    ("Slaughter respects disabled default", SlaughterDisabled),
    ("Loaded or protected animals ignored", ProtectedSlaughter),
    ("Small pen slaughter versus large pen", AreaCapacity),
    ("Mixed species share a single space budget", SharedArea),
    ("Invalid area config never partially applied", InvalidAreaConfig)
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

static void ClosedGate()
{
    var walls = new[] { Wall(0,0,2,0), Wall(2,0,3,0),
        Wall(3,0,5,0), Wall(5,0,5,5),
        Wall(5,5,0,5), Wall(0,5,0,0) };
    var result = PenGeometry.Detect(new(2.5,2.5), walls, 0.25, 8);
    Check(result.Closed && result.Contains(new(2.5,2.5)));
}

static void BlankSign()
{
    Check(SignTextRules.IsBlankOrPlaceholder(null));
    Check(SignTextRules.IsBlankOrPlaceholder(""));
    Check(SignTextRules.IsBlankOrPlaceholder("   "));
    Check(SignTextRules.IsBlankOrPlaceholder("..."));
    Check(SignTextRules.IsBlankOrPlaceholder(" … "));
    Check(SignTextRules.IsBlankOrPlaceholder("<color=white>"));
    Check(SignTextRules.IsBlankOrPlaceholder("<color=white>..."));
    Check(SignTextRules.IsBlankOrPlaceholder("<color=\"white\" d>"));
    Check(!SignTextRules.IsBlankOrPlaceholder("<color=white>Attenzione!"));
    Check(SignTextRules.IsBlankOrPlaceholder("PLACEHOLDER", "PLACEHOLDER"));
}
static void GeneratedPrefixRepair()
{
    const string generated = "Barriera: wood_fence\\nRecinto: open-gap";
    Check(SignTextRules.IsKnownFormattingPrefixOf(
        "<color=\"white\" d>" + generated, generated));
    Check(!SignTextRules.IsKnownFormattingPrefixOf(generated, generated));
    Check(!SignTextRules.IsKnownFormattingPrefixOf(
        "<color=red>ATTENZIONE " + generated, generated));
    Check(!SignTextRules.IsKnownFormattingPrefixOf(
        "<color=red>" + generated + "edit", generated));
}
static void CustomSign()
{
    Check(!SignTextRules.IsBlankOrPlaceholder("Azienda agricola"));
    Check(!SignTextRules.IsBlankOrPlaceholder(".."));
    Check(!SignTextRules.IsBlankOrPlaceholder("<color=yellow>Boar"));
    Check(!SignTextRules.IsBlankOrPlaceholder("Cinghiali", "..."));
}

static RanchPolicy.Animal Ranch(string id, string species, int level=1,
    RanchPolicy.Disposition state=RanchPolicy.Disposition.Tamed) =>
    new() { Id=id, Species=species, Level=level, State=state,
        Stage=RanchPolicy.LifeStage.Adult };

static RanchPolicy.Settings SlaughterSettings() => new()
{
    AllowSlaughter=true, MinBreedersPerSpecies=2,
    UsableAreaFraction=0.8, MaxSlaughtersPerCycle=2
};
static void OpenPenNoSlaughter()
{
    var animals=Enumerable.Range(0,5).Select(i=>Ranch("b"+i,"Boar")).ToArray();
    var p=RanchPolicy.Preview(animals,false,20,SlaughterSettings());
    Check(p.Operations.Count==0 && !p.FullyAuthorized);
}
static void MixedSlaughter()
{
    var animals=Enumerable.Range(0,5).Select(i=>Ranch("b"+i,"Boar",i+1))
        .Concat(Enumerable.Range(0,4).Select(i=>Ranch("w"+i,"Wolf",i+1)))
        .ToArray();
    var p=RanchPolicy.Preview(animals,true,20,SlaughterSettings());
    Check(p.Operations.Count(a=>a.Type==RanchPolicy.Action.Slaughter)==2 &&
        p.Operations.All(a=>!a.CanExecute));
    // At least two adults of each species must remain even with a shared
    // area deficit; no independent per-species square-meter budgets.
    foreach (var species in new[]{"Boar","Wolf"})
      Check(p.Operations.Count(a=>a.Type==RanchPolicy.Action.Slaughter &&
         a.Species==species) <= animals.Count(a=>a.Species==species)-2);
}
static void OnlySurplusAdults()
{
    var animals=Enumerable.Range(0,4).Select(i=>Ranch("b"+i,"Boar"))
        .Concat(new[] { Ranch("wild","Boar",1,RanchPolicy.Disposition.Wild),
            new RanchPolicy.Animal {Id="baby", Species="Boar",
                State=RanchPolicy.Disposition.Tamed,
                Stage=RanchPolicy.LifeStage.Juvenile} }).ToArray();
    var p=RanchPolicy.Preview(animals,true,20,SlaughterSettings());
    Check(p.Operations.Count(a=>a.Type==RanchPolicy.Action.Slaughter)==2 &&
        p.Operations.All(a=>a.Type!=RanchPolicy.Action.Slaughter ||
            (a.AnimalId!="wild" && a.AnimalId!="baby")));
}
static void SlaughterDisabled()
{
    var animals=Enumerable.Range(0,7).Select(i=>Ranch("b"+i,"Boar")).ToArray();
    var p=RanchPolicy.Preview(animals,true,20,new RanchPolicy.Settings());
    Check(!p.Operations.Any(a=>a.Type==RanchPolicy.Action.Slaughter));
}
static void ProtectedSlaughter()
{
    var animals=Enumerable.Range(0,4).Select(i=>Ranch("b"+i,"Boar")).ToArray();
    animals[0].Protected=true;
    animals[1].Loaded=true;
    var p=RanchPolicy.Preview(animals,true,20,SlaughterSettings());
    Check(p.Operations.Count(a=>a.Type==RanchPolicy.Action.Slaughter)==2 &&
        p.Operations.All(a=>a.Type!=RanchPolicy.Action.Slaughter ||
            (a.AnimalId!="b0" && a.AnimalId!="b1")));
}
static void AreaCapacity()
{
    var animals=Enumerable.Range(0,5).Select(i=>Ranch("b"+i,"Boar")).ToArray();
    var small=RanchPolicy.Preview(animals,true,20,SlaughterSettings());
    var large=RanchPolicy.Preview(animals,true,80,SlaughterSettings());
    Check(small.SlaughterCandidates>0 &&
        large.SlaughterCandidates==0 && !small.FullyAuthorized);
}
static void SharedArea()
{
    var animals=new[]{Ranch("b0","Boar"),Ranch("b1","Boar"),
        Ranch("b2","Boar"),Ranch("w0","Wolf"),
        Ranch("w1","Wolf"),Ranch("w2","Wolf")};
    var p=RanchPolicy.Preview(animals,true,38,SlaughterSettings());
    Check(p.SlaughterCandidates>=1 &&
        p.Operations.All(a=>!a.CanExecute));
    var big=RanchPolicy.Preview(animals,true,100,SlaughterSettings());
    Check(big.SlaughterCandidates==0);
}
static void InvalidAreaConfig()
{
    var dict=new Dictionary<string,double>{{"Boar",6}};
    Check(!RanchAreaConfiguration.TryParseSpeciesAreas(
        "Boar:4,Wolf:-1",dict) && dict["Boar"]==6);
    Check(!RanchAreaConfiguration.TryParseSpeciesAreas(
        "Boar:4,Boar:5",dict) && dict.Count==1);
    Check(RanchAreaConfiguration.TryParseSpeciesAreas(
        "Boar:8,Wolf:11",dict) && dict.Count==2 &&
        dict["Boar"]==8 && dict["Wolf"]==11);
}
