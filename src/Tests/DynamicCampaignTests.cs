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

    private (BriefingRoom briefingRoom, DynamicCampaignState state) CreateCampaign(string theater = "Caucasus", int seed = 1234, string aircraft = null)
    {
        var briefingRoom = new BriefingRoom(fixture.Db);
        var template = new CampaignTemplate(briefingRoom, Path.Combine(BriefingRoom.GetBriefingRoomRootPath(), "testTemplates", "Test.cbrt"));
        if (aircraft != null) template.PlayerFlightGroups.ForEach(x => x.Aircraft = aircraft);
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
            Assert.Equal(mission.PlayerAirbase.ID, mission.PlayerAirbaseDestination.ID);
            Assert.Contains(mission.Briefing.FlightBriefings.SelectMany(x => x.Waypoints), x => x.Contains("[TARGET]"));

            var miz = await mission.SaveToMizBytes(fixture.Db);
            using var zip = new ZipArchive(new MemoryStream(miz));
            var scripts = string.Join("\n", zip.Entries.Where(x => x.FullName.EndsWith(".lua")).Select(x =>
            {
                using var reader = new StreamReader(x.Open());
                return reader.ReadToEnd();
            }));
            Assert.Contains($"briefingRoom.dynamicCampaign.token = \"{state.Pending.Token}\"", scripts);
            Assert.DoesNotContain("$TOKEN$", scripts);
            Assert.DoesNotContain("$ENDMISSIONLABEL$", scripts);
            Assert.DoesNotContain("$RADARSDOWN$", scripts);
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

    private const string DEBRIEF_JSON = """{"pilots":[{"key":"P:Sipmann","name":"Sipmann","ai":false,"aircraft":"F-16C_50","takeoffs":1,"landings":1,"flightTime":3725,"status":"Landed","hitsTaken":0,"friendlyKills":0,"shotDownBy":"","kills":{"air":0,"helicopter":0,"ground":1,"airDefense":2,"ship":0,"structure":0},"weapons":[{"name":"AGM-88C HARM","category":"ARM","gun":false,"shots":2,"hits":2,"kills":2},{"name":"M61A1","category":"Gun","gun":true,"shots":0,"hits":40,"kills":1}]},{"key":"A:Viper-2","name":"Enfield12","ai":true,"aircraft":"F-16C_50","takeoffs":1,"landings":0,"flightTime":1800,"status":"Ejected","hitsTaken":1,"friendlyKills":0,"shotDownBy":"SA-11 Buk LN 9A310M1","kills":{"air":0,"helicopter":0,"ground":0,"airDefense":0,"ship":0,"structure":0},"weapons":[{"name":"AGM-88C HARM","category":"ARM","gun":false,"shots":2,"hits":0,"kills":0}]}],"events":[{"time":43200,"pilot":"P:Sipmann","kind":"Takeoff","weapon":"","target":"Kutaisi"},{"time":44000,"pilot":"P:Sipmann","kind":"Shot","weapon":"AGM-88C HARM","target":"SA-11 Buk SR 9S18M1"},{"time":44030,"pilot":"P:Sipmann","kind":"Kill","weapon":"AGM-88C HARM","target":"SA-11 Buk SR 9S18M1"},{"time":44100,"pilot":"A:Viper-2","kind":"ShotDown","weapon":"SA-11","target":"SA-11 Buk LN 9A310M1"},{"time":44200,"pilot":"P:Sipmann","kind":"Shot","weapon":"Mk-82","target":""}],"airDefenseKills":[{"x":-1000,"y":2000,"radar":true}]}""";

    [Fact]
    public void ParsesSiteResultsAndDebrief()
    {
        var log = new StringBuilder()
            .AppendLine("2026-10-28 20:10:00.000 INFO    SCRIPTING (Main): BRDC|0123456789ab|3|600|0|1|1/2/0/3/8|W:ARM=4|L:F-16C_50=1")
            .AppendLine("2026-10-28 20:10:00.000 INFO    SCRIPTING (Main): BRDCS|0123456789ab|3|" + DEBRIEF_JSON)
            .AppendLine("2026-10-28 20:05:00.000 INFO    SCRIPTING (Main): BRDCS|0123456789ab|2|{\"pilots\":[],\"events\":[],\"airDefenseKills\":[]}")
            .AppendLine("2026-10-28 20:06:00.000 INFO    SCRIPTING (Main): BRDCS|0123456789ab|4|{broken")
            .ToString();

        var result = DynamicCampaignLogParser.Parse(log, "0123456789ab")!;

        var objective = Assert.Single(result.Objectives);
        Assert.Equal((1, 2, 3, 8), (objective.Dead, objective.Total, objective.SiteDead, objective.SiteTotal));
        Assert.Equal(2, result.Pilots.Count);
        var pilot = result.Pilots[0];
        Assert.Equal("Sipmann", pilot.Name);
        Assert.False(pilot.Ai);
        Assert.Equal(3, pilot.Kills.Total());
        Assert.Equal(2, pilot.Shots()); // Gun bursts are not shots
        Assert.Equal(2, pilot.Hits());
        Assert.True(result.Pilots[1].AircraftLost());
        Assert.Equal(5, result.Events.Count);
        var kill = Assert.Single(result.AirDefenseKills);
        Assert.True(kill.Radar);
        Assert.Equal(-1000, kill.X);

        // Reports of older missions: the site is the objective
        var old = DynamicCampaignLogParser.Parse("BRDC|0123456789ab|1|60|0|0|2/4/0", "0123456789ab")!;
        Assert.Equal((2, 4), (old.Objectives[0].SiteDead, old.Objectives[0].SiteTotal));
        Assert.Empty(old.Pilots);
    }

    [Fact]
    public void DestroyedRadarsBlindTheSiteForTwoMissions()
    {
        var (briefingRoom, state) = CreateCampaign();
        var site = state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense).First();
        state.GetAsset(site.SupplierId)!.Stock = 100;
        var coverBefore = DynamicCampaignMissionMaker.GetAirDefenseCover(state, state.EnemyCoalition, site.Coordinates);
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = site.Id, Kind = DCSortieKind.SEAD, Turn = state.Turn };

        // The search radar only (1 of 2 radars), 1 unit of a site of 8
        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 1, Total = 2, SiteDead = 1, SiteTotal = 8 } } });

        Assert.Equal(DynamicCampaignRules.AIR_DEFENSE_RADAR_REPAIR_TURNS - 1, site.RadarRepairTurns);
        Assert.InRange(site.Strength, 80, 100);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventRadarsDown" && x.Args.Contains($"@{site.Id}"));
        Assert.True(DynamicCampaignMissionMaker.GetAirDefenseCover(state, state.EnemyCoalition, site.Coordinates) < coverBefore);
        Assert.Contains(DynamicCampaignService.GetSorties(briefingRoom, state), x => x.TargetId == site.Id);

        // Next mission: the site is spawned without radars, its launchers are the objective
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = site.Id, Kind = DCSortieKind.SEAD, Turn = state.Turn, RadarsDown = true };
        state.GetAsset(site.SupplierId)!.Stock = 100;
        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 2, Total = 6, SiteDead = 2, SiteTotal = 6 } } });
        Assert.True(site.RadarsDown);

        state.GetAsset(site.SupplierId)!.Stock = 100;
        DynamicCampaignService.SkipTurn(state);
        Assert.False(site.RadarsDown);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventRadarsRepaired" && x.Args.Contains($"@{site.Id}"));
    }

    [Fact]
    public void BlindSiteDestroyedWhenAllLaunchersAreKilled()
    {
        var (_, state) = CreateCampaign();
        var site = state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense).First();
        site.RadarRepairTurns = 2;
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = site.Id, Kind = DCSortieKind.SEAD, Turn = state.Turn, RadarsDown = true };

        DynamicCampaignService.ResolveTurn(state, new DCMissionResult { Objectives = new() { new DCObjectiveResult { Dead = 6, Total = 6, Complete = true, SiteDead = 6, SiteTotal = 6 } } });

        Assert.False(site.IsAlive);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventAssetDestroyed" && x.Args.Contains($"@{site.Id}"));
    }

    [Fact]
    public void AirDefenseKillsAroundTheTargetHurtTheNearestCampaignSite()
    {
        var (_, state) = CreateCampaign();
        var sites = state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense).ToList();
        // A depot covered by a SAM site: kills near that site are taken from it
        var site = sites.First();
        var target = state.GetAssets(state.EnemyCoalition, DCAssetKind.Depot)
            .MinBy(x => x.Coordinates.GetDistanceFrom(site.Coordinates))!;
        Assert.True(target.Coordinates.GetDistanceFrom(site.Coordinates) < DynamicCampaignRules.AIR_DEFENSE_COVER_KM * 1000);
        var nearest = sites.Where(x => x.Coordinates.GetDistanceFrom(target.Center) < DynamicCampaignRules.AIR_DEFENSE_COVER_KM * 1000)
            .MinBy(x => x.Coordinates.GetDistanceFrom(new Coordinates(site.X + 500, site.Y)))!;
        state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = target.Id, Kind = DCSortieKind.StrikeDepot, Turn = state.Turn };

        DynamicCampaignService.ResolveTurn(state, new DCMissionResult
        {
            Objectives = new() { new DCObjectiveResult { Dead = 0, Total = 4 } },
            AirDefenseKills = new()
            {
                new DCAirDefenseKill { X = site.X + 500, Y = site.Y, Radar = true },
                new DCAirDefenseKill { X = site.X + 500, Y = site.Y, Radar = false },
            },
        });

        Assert.True(nearest.RadarsDown);
        Assert.Contains(state.Reports.Last().Events, x => x.Key == "DCEventAirDefenseUnitsDestroyed" && x.Args.SequenceEqual(new[] { $"@{nearest.Id}", "2" }));
    }

    [Fact]
    public void EnemyAirDefensesAreCappedByTheFlightSize()
    {
        var (briefingRoom, state) = CreateCampaign();
        var sorties = DynamicCampaignService.GetSorties(briefingRoom, state);
        // Test template: 2 aircraft
        Assert.Equal(2, state.PlayerAircraft.Values.Sum());
        Assert.All(sorties, x => Assert.True(x.Threat <= AmountNR.Average));

        // Every site of the coalition in one spot: the uncapped level is the highest
        var site = state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense).First();
        foreach (var other in state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense)) { other.X = site.X; other.Y = site.Y; }
        Assert.Equal(AmountNR.VeryHigh, DynamicCampaignMissionMaker.GetAirDefenseLevel(state, state.EnemyCoalition, site.Coordinates));
        Assert.Equal(AmountNR.Average, DynamicCampaignMissionMaker.GetEnemyAirDefenseLevel(state, site.Coordinates));
        state.PlayerAircraft = state.PlayerAircraft.ToDictionary(x => x.Key, _ => 4);
        Assert.Equal(AmountNR.High, DynamicCampaignMissionMaker.GetEnemyAirDefenseLevel(state, site.Coordinates));
        state.PlayerAircraft = state.PlayerAircraft.ToDictionary(x => x.Key, _ => 6);
        Assert.Equal(AmountNR.VeryHigh, DynamicCampaignMissionMaker.GetEnemyAirDefenseLevel(state, site.Coordinates));
    }

    [Fact]
    public void MissionDebriefIsKeptAndSummedInThePilotCareer()
    {
        var (briefingRoom, state) = CreateCampaign();
        var result = DynamicCampaignLogParser.Parse("BRDC|0123456789ab|3|600|0|1|1/2/0/3/8\nBRDCS|0123456789ab|3|" + DEBRIEF_JSON, "0123456789ab")!;
        for (var mission = 0; mission < 2; mission++)
        {
            var site = state.GetAssets(state.EnemyCoalition, DCAssetKind.AirDefense).First(x => x.IsAlive);
            state.Pending = new DCPendingMission { Token = "0123456789ab", TargetId = site.Id, Kind = DCSortieKind.SEAD, Turn = state.Turn, MissionName = $"Operation {mission}" };
            DynamicCampaignService.ResolveTurn(state, result);
        }
        DynamicCampaignService.SkipTurn(state);

        var debrief = state.Reports[^2].Debrief!;
        Assert.Equal("Operation 1", debrief.MissionName);
        Assert.Equal(38, debrief.ObjectivePercent); // 3 of 8 units of the site
        Assert.Null(state.Reports.Last().Debrief);
        var loaded = DynamicCampaignStore.Deserialize(DynamicCampaignStore.Serialize(state));
        Assert.Equal(2, loaded.Reports[^2].Debrief!.Pilots.Count);

        var pilot = Assert.Single(DynamicCampaignService.GetPilots(new[] { loaded })); // AI wingmen have no career
        Assert.Equal("Sipmann", pilot.Name);
        Assert.Equal(2, pilot.Missions);
        Assert.Equal(6, pilot.Kills.Total());
        Assert.Equal(4, pilot.Kills.AirDefense);
        Assert.Equal(100, pilot.Accuracy);
        Assert.Equal(2, pilot.Aircraft["F-16C_50"]);
        Assert.Equal(4, pilot.Weapons.First(x => x.Name == "AGM-88C HARM").Kills);
        Assert.Equal(2, pilot.History.Count);
        Assert.Equal("2:04", DynamicCampaignText.Duration(pilot.FlightTime));

        foreach (var debriefEvent in debrief.Events)
            Assert.DoesNotContain("ERR_", DynamicCampaignText.DebriefEvent(briefingRoom, debrief, debriefEvent));
        Assert.Contains("Enfield12 (AI)", DynamicCampaignText.DebriefEvent(briefingRoom, debrief, debrief.Events[3]));
        foreach (var status in new[] { "OnGround", "Airborne", "Landed", "Ejected", "Killed", "Lost" })
            Assert.DoesNotContain("ERR_", DynamicCampaignText.PilotStatus(briefingRoom, status));
        foreach (var threat in Enum.GetValues<AmountNR>().Where(x => x != AmountNR.Random))
            Assert.DoesNotContain("ERR_", DynamicCampaignText.Threat(briefingRoom, threat));
    }

    [Fact]
    public void ViperFlightPlanHasASteerpointOverTheDepartureAirbase()
    {
        var (briefingRoom, state) = CreateCampaign(aircraft: "F-16C_50");
        var sortie = DynamicCampaignService.GetSorties(briefingRoom, state).First(x => x.Unavailable == null && x.Kind == DCSortieKind.StrikeDepot);
        var mission = DynamicCampaignService.GenerateMission(briefingRoom, state, sortie);

        // The F-16 has no steerpoint 0: steerpoint 1 is the departure airbase, the target comes after
        var waypoints = mission.Briefing.FlightBriefings.First().Waypoints;
        Assert.StartsWith($"1: {mission.PlayerAirbase.UIDisplayName.Get(mission.LangKey).ToUpper()}", waypoints[1]);
        Assert.Contains(waypoints.Skip(2), x => x.Contains("[TARGET]"));
    }
}
