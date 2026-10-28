using System.IO.Compression;
using System.Text;
using BriefingRoom4DCS.DynamicCampaign;
using BriefingRoom4DCS.Mission;
using BriefingRoom4DCS.Template;

namespace BriefingRoom4DCS.Tests;

[Collection("Database collection")]
public class DynamicCampaignTests
{
    private readonly DatabaseFixture fixture;

    public DynamicCampaignTests(DatabaseFixture fixture)
    {
        this.fixture = fixture;
    }

    private (BriefingRoom briefingRoom, DynamicCampaignState state) CreateCampaign(string theater = "Caucasus", int seed = 1234)
    {
        var briefingRoom = new BriefingRoom(fixture.Db);
        var template = new CampaignTemplate(briefingRoom, Path.Combine(BriefingRoom.GetBriefingRoomRootPath(), "testTemplates", "Test.cbrt"));
        template.ContextTheater = theater;
        template.ContextSituation = "";
        template.RandomSeed = seed;
        return (briefingRoom, DynamicCampaignService.Create(briefingRoom, template, "Test"));
    }

    [Theory]
    [InlineData("Afghanistan")]
    [InlineData("Caucasus")]
    [InlineData("Falklands")]
    [InlineData("GermanyCW")]
    [InlineData("Iraq")]
    [InlineData("Kola")]
    [InlineData("MarianaIslands")]
    [InlineData("MarianaIslandsWWII")]
    [InlineData("Nevada")]
    [InlineData("Normandy")]
    [InlineData("PersianGulf")]
    [InlineData("SinaiMap")]
    [InlineData("Syria")]
    [InlineData("TheChannel")]
    public void CreatesBothCoalitionsWithSupplyNetwork(string theater)
    {
        var (_, state) = CreateCampaign(theater);

        Assert.True(state.FrontLine.Count >= 2);
        foreach (var coalition in new[] { Coalition.Blue, Coalition.Red })
        {
            foreach (var kind in new[] { DCAssetKind.Factory, DCAssetKind.Depot, DCAssetKind.FrontUnit, DCAssetKind.AirDefense, DCAssetKind.Airbase, DCAssetKind.GroundRoute })
                Assert.True(state.GetAssets(coalition, kind).Any(), $"{coalition} has no {kind}");
            // Every front unit gets its convoys from its depot
            foreach (var unit in state.GetAssets(coalition, DCAssetKind.FrontUnit))
            {
                Assert.NotNull(state.GetAsset(unit.SupplierId));
                Assert.Contains(state.Assets, x => x.Kind == DCAssetKind.GroundRoute && x.ToId == unit.Id && x.FromId == unit.SupplierId);
            }
        }
        Assert.Equal(state.Assets.Count, state.Assets.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void SameSeedCreatesSameLayout()
    {
        var (_, first) = CreateCampaign(seed: 99);
        var (_, second) = CreateCampaign(seed: 99);
        Assert.Equal(first.Situation, second.Situation);
        Assert.Equal(first.Assets.Select(x => (x.Kind, x.X, x.Y)), second.Assets.Select(x => (x.Kind, x.X, x.Y)));
    }

    [Theory]
    [InlineData("Caucasus")]
    [InlineData("PersianGulf")]
    public async Task GeneratesMissionForEachSortieKindWithReporter(string theater)
    {
        var (briefingRoom, state) = CreateCampaign(theater);
        var sorties = DynamicCampaignService.GetSorties(briefingRoom, state);
        Assert.NotEmpty(sorties);
        Assert.All(sorties, x => Assert.Equal(state.EnemyCoalition, state.GetAsset(x.TargetId).Coalition));

        var failures = new List<string>();
        foreach (var sortie in sorties.GroupBy(x => x.Kind).Select(x => x.First()))
        {
            DCSMission mission;
            try
            {
                mission = DynamicCampaignService.GenerateMission(briefingRoom, state, sortie);
            }
            catch (BriefingRoomException ex)
            {
                failures.Add($"{sortie.Kind}: {ex.Message} {ex.InnerException?.Message}");
                continue;
            }
            Assert.NotNull(mission);
            Assert.NotNull(state.Pending);
            Assert.Equal(sortie.TargetId, state.Pending.TargetId);

            var miz = await mission.SaveToMizBytes(fixture.Db);
            using var zip = new ZipArchive(new MemoryStream(miz));
            var scripts = string.Join("\n", zip.Entries.Where(x => x.FullName.EndsWith(".lua")).Select(x =>
            {
                using var reader = new StreamReader(x.Open());
                return reader.ReadToEnd();
            }));
            Assert.Contains($"briefingRoom.dynamicCampaign.token = \"{state.Pending.Token}\"", scripts);
            Assert.DoesNotContain("$TOKEN$", scripts);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ParsesLatestReportOfTheMission()
    {
        var log = new StringBuilder()
            .AppendLine("2026-10-28 20:00:01.000 INFO    SCRIPTING (Main): BRDC|aaaaaaaaaaaa|1|62|0|0|0/4/0")
            .AppendLine("2026-10-28 20:00:02.000 INFO    SCRIPTING (Main): BRDC|0123456789ab|1|2|0|0|0/4/0")
            .AppendLine("2026-10-28 20:10:00.000 INFO    SCRIPTING (Main): BRDC|0123456789ab|3|600|1|1|4/4/1")
            .AppendLine("2026-10-28 20:05:00.000 INFO    SCRIPTING (Main): BRDC|0123456789ab|2|300|0|0|2/4/0")
            .ToString();

        var result = DynamicCampaignLogParser.Parse(log, "0123456789ab");

        Assert.NotNull(result);
        Assert.Equal(3, result.Sequence);
        Assert.True(result.MissionComplete);
        Assert.Equal(1, result.PlayerAircraftLost);
        var objective = Assert.Single(result.Objectives);
        Assert.Equal(4, objective.Dead);
        Assert.Equal(4, objective.Total);
        Assert.True(objective.Complete);
        Assert.Null(DynamicCampaignLogParser.Parse(log, "ffffffffffff"));
    }

    [Fact]
    public void DestroyingADepotStarvesItsFrontUnits()
    {
        var (_, state) = CreateCampaign();
        var enemy = state.EnemyCoalition;
        var unit = state.GetAssets(enemy, DCAssetKind.FrontUnit).First();
        var depot = state.GetAsset(unit.SupplierId)!;

        // Destroy every depot so nobody can re-route supplies
        foreach (var other in state.GetAssets(enemy, DCAssetKind.Depot)) { other.Strength = 0; other.Stock = 0; }
        var before = unit.Strength;
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = depot.Id, Turn = state.Turn };
        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 4, Total = 4, Complete = true } } });

        Assert.True(unit.Strength < before);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventUnitOutOfSupplies" && x.Args.Contains($"@{unit.Id}"));
    }

    [Fact]
    public void InterdictedConvoyRouteIsCut()
    {
        var (_, state) = CreateCampaign();
        var route = state.GetAssets(state.EnemyCoalition, DCAssetKind.GroundRoute).First();
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = route.Id, Turn = state.Turn };

        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 3, Total = 4 } } });

        Assert.True(route.CutTurns > 0);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventRouteCut" && x.Args.Contains($"@{route.Id}"));
    }

    [Fact]
    public void CampaignRunsManyTurnsAndSurvivesSaveAndLoad()
    {
        var (briefingRoom, state) = CreateCampaign();
        var rng = new Random(5);
        for (var turn = 0; turn < 40 && state.Outcome == DCOutcome.Ongoing; turn++)
        {
            var sorties = DynamicCampaignService.GetSorties(briefingRoom, state);
            Assert.NotEmpty(sorties);
            var sortie = sorties[rng.Next(sorties.Count)];
            state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = sortie.TargetId, Kind = sortie.Kind, Turn = state.Turn };
            var total = rng.Next(1, 6);
            DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = rng.Next(total + 1), Total = total } } });

            Assert.All(state.Assets, x =>
            {
                Assert.InRange(x.Strength, 0, 100);
                Assert.True(x.Stock >= 0);
            });
            foreach (var campaignEvent in state.Reports.Last().Events)
                Assert.DoesNotContain("ERR_Missing_Translation_key", DynamicCampaignText.Event(briefingRoom, state, campaignEvent));
        }
        Assert.True(state.Turn > 1);

        var json = DynamicCampaignStore.Serialize(state);
        var loaded = DynamicCampaignStore.Deserialize(json);
        Assert.Equal(json, DynamicCampaignStore.Serialize(loaded));

        var directory = Path.Combine(Path.GetTempPath(), $"br-dc-{Guid.NewGuid():N}");
        try
        {
            var store = new DynamicCampaignStore(directory);
            store.Save(state);
            Assert.Equal(state.Id, store.Load(state.Id)!.Id);
            Assert.Single(store.List());
            store.Delete(state.Id);
            Assert.Empty(store.List());
            Assert.Throws<ArgumentException>(() => store.Load("../etc/passwd"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AllTextsAreTranslated()
    {
        var (briefingRoom, state) = CreateCampaign();
        foreach (var kind in Enum.GetValues<DCAssetKind>())
            Assert.DoesNotContain("ERR_", DynamicCampaignText.AssetKind(briefingRoom, kind));
        foreach (var kind in Enum.GetValues<DCSortieKind>())
        {
            Assert.DoesNotContain("ERR_", DynamicCampaignText.SortieKind(briefingRoom, kind));
            Assert.DoesNotContain("ERR_", briefingRoom.Translate($"DCBriefingImpact{kind}", "x"));
        }
        briefingRoom.LanguageKey = "pt";
        Assert.Equal("Fábrica", DynamicCampaignText.AssetKind(briefingRoom, DCAssetKind.Factory));
    }

    [Fact]
    public void AirbasesStartWithAircraftAndWeapons()
    {
        var (_, state) = CreateCampaign();
        Assert.Equal(2, state.PlayerAircraft["Su-25T"]);
        foreach (var airbase in state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase))
        {
            Assert.True(airbase.Aircraft!["Su-25T"] > 0);
            Assert.True(airbase.Aircraft["Fighter"] > 0);
            Assert.True(airbase.Weapons!["AAM"] > 0);
        }
        Assert.All(state.GetAssets(state.EnemyCoalition, DCAssetKind.Airbase), x => Assert.False(x.Aircraft!.ContainsKey("Su-25T")));
        Assert.All(state.GetAssets(state.EnemyCoalition, DCAssetKind.Depot), x => Assert.True(x.Weapons!["Bomb"] > 0));
    }

    [Fact]
    public void CampaignsWithoutStockGetOneWhenOpened()
    {
        var (briefingRoom, state) = CreateCampaign();
        foreach (var asset in state.Assets) { asset.Weapons = null; asset.Aircraft = null; }
        state.PlayerAircraft = new();

        DynamicCampaignService.GetSorties(briefingRoom, state);

        Assert.All(state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase), x => Assert.True(x.Aircraft!["Su-25T"] > 0));
    }

    [Fact]
    public void MissionConsumesWeaponsAndAircraftOfTheDepartureAirbase()
    {
        var (briefingRoom, state) = CreateCampaign();
        var sortie = DynamicCampaignService.GetSorties(briefingRoom, state).First(x => x.Unavailable == null);
        var departure = state.GetAsset(sortie.DepartureId)!;
        var aircraftBefore = departure.Aircraft!["Su-25T"];
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = sortie.TargetId, Kind = sortie.Kind, DepartureId = departure.Id, Turn = state.Turn };

        DynamicCampaignService.ResolveTurn(state, new DCMissionResult
        {
            PlayerAircraftLost = 1,
            Objectives = new() { new DCObjectiveResult { Dead = 1, Total = 4 } },
            WeaponsUsed = new() { { "AGM", 4 } },
            AircraftLost = new() { { "Su-25T", 1 } },
        });

        Assert.Equal(aircraftBefore - 1, departure.Aircraft["Su-25T"]);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventWeaponsUsed");
        var text = DynamicCampaignText.Event(briefingRoom, state, state.Reports.Last().Events.First(x => x.Key == "DCEventWeaponsUsed"));
        Assert.Contains("Air-to-ground missiles 4", text);
    }

    [Fact]
    public void SortiesNeedAircraftAndWeapons()
    {
        var (briefingRoom, state) = CreateCampaign();
        foreach (var airbase in state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase)) airbase.Weapons!["AAM"] = 0;
        var sorties = DynamicCampaignService.GetSorties(briefingRoom, state);
        Assert.All(sorties.Where(x => x.Kind == DCSortieKind.InterceptCargo), x => Assert.Equal("DCUnavailableNoWeapons", x.Unavailable));
        Assert.Contains(sorties, x => x.Kind == DCSortieKind.StrikeDepot && x.Unavailable == null);

        foreach (var airbase in state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase)) airbase.Aircraft!["Su-25T"] = 1;
        sorties = DynamicCampaignService.GetSorties(briefingRoom, state);
        Assert.All(sorties, x => Assert.Equal("DCUnavailableNoAircraft", x.Unavailable));
        Assert.Throws<BriefingRoomException>(() => DynamicCampaignService.GenerateMission(briefingRoom, state, sorties.First()));
    }

    [Fact]
    public void FactoriesProduceAndAirbaseStrikesDestroyParkedAircraft()
    {
        var (_, state) = CreateCampaign();
        var enemy = state.EnemyCoalition;
        var factory = state.GetAssets(enemy, DCAssetKind.Factory).First();
        var airbase = state.GetAssets(enemy, DCAssetKind.Airbase).First();
        var parkedBefore = airbase.Aircraft!["Attack"] + airbase.Aircraft["Fighter"];
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = airbase.Id, Kind = DCSortieKind.AirbaseStrike, Turn = state.Turn };

        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 3, Total = 4 } } });

        Assert.True(factory.Weapons!.Values.Sum() > 0 || state.GetAssets(enemy, DCAssetKind.Depot).Any(x => x.Weapons!["Bomb"] > 120));
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventAircraftDestroyedOnGround" && x.Args.Contains($"@{airbase.Id}"));
        Assert.True(airbase.Aircraft["Attack"] + airbase.Aircraft["Fighter"] < parkedBefore);

        // Without factories nothing new is produced
        foreach (var f in state.GetAssets(enemy, DCAssetKind.Factory)) { f.Strength = 0; f.Weapons!.Clear(); }
        DynamicCampaignService.SkipTurn(state);
        Assert.All(state.GetAssets(enemy, DCAssetKind.Factory), x => Assert.Equal(0, x.Weapons!.Values.Sum()));
    }

    [Fact]
    public void ParsesWeaponsUsedAndAircraftLost()
    {
        var result = DynamicCampaignLogParser.Parse(
            "INFO    SCRIPTING (Main): BRDC|0123456789ab|2|900|1|1|3/4/1|W:AGM=4;Rocket=14;Junk=3|L:FA-18C_hornet=1",
            "0123456789ab")!;
        Assert.Equal(4, result.WeaponsUsed["AGM"]);
        Assert.Equal(14, result.WeaponsUsed["Rocket"]);
        Assert.False(result.WeaponsUsed.ContainsKey("Junk"));
        Assert.Equal(1, result.AircraftLost["FA-18C_hornet"]);
    }
}
