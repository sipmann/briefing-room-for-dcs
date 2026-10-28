/*
==========================================================================
This file is part of Briefing Room for DCS World, a mission
generator for DCS World, by @akaAgar (https://github.com/akaAgar/briefing-room-for-dcs)

Briefing Room for DCS World is free software: you can redistribute it
and/or modify it under the terms of the GNU General Public License
as published by the Free Software Foundation, either version 3 of
the License, or (at your option) any later version.

Briefing Room for DCS World is distributed in the hope that it will
be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with Briefing Room for DCS World. If not, see https://www.gnu.org/licenses/
==========================================================================
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BriefingRoom4DCS.Data;
using BriefingRoom4DCS.Generator;
using BriefingRoom4DCS.Template;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Lays out the initial theater: for each coalition, factories deep in the
    // rear, depots behind the front, ground formations on the front line, SAM
    // sites, airbases, and the convoy/cargo routes linking them.
    internal static class DynamicCampaignBuilder
    {
        private static readonly SpawnPointType[] LARGE_SPOTS = { SpawnPointType.LandLarge };
        private static readonly SpawnPointType[] MEDIUM_SPOTS = { SpawnPointType.LandMedium, SpawnPointType.LandLarge };
        private static readonly SpawnPointType[] ANY_LAND_SPOTS = { SpawnPointType.LandSmall, SpawnPointType.LandMedium, SpawnPointType.LandLarge };

        internal static DynamicCampaignState Create(IBriefingRoom briefingRoom, CampaignTemplate template, string name)
        {
            var database = briefingRoom.Database;
            var seed = template.RandomSeed ?? Random.Shared.Next();
            var rng = new Random(seed);
            var invert = template.OptionsMission.Contains("InvertCountriesCoalitions");
            var situation = PickSituation(briefingRoom, template, rng);
            var theater = database.GetEntry<DBEntryTheater>(template.ContextTheater);

            var state = new DynamicCampaignState
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? GeneratorTools.GenerateCampaignName(database, briefingRoom.LanguageKey, "") : name.Trim(),
                CreatedUtc = DateTime.UtcNow,
                Date = GenerateStartDate(template, rng),
                Seed = seed,
                Theater = template.ContextTheater,
                Situation = situation.ID,
                InvertCoalitions = invert,
                PlayerCoalition = template.ContextPlayerCoalition,
                TemplateIni = Encoding.UTF8.GetString(template.GetIniBytes()),
            };

            // Real airfields only: heliports and helipads have no room for planes
            var airbases = situation.GetAirbases(database, invert)
                .Where(x => x.RunwayLengthFt >= 3000 && x.ParkingSpots.Count(p => p.ParkingType != ParkingSpotType.HelicopterOnly) >= 6)
                .ToList();
            var spotsByCoalition = new Dictionary<Coalition, List<DBEntryTheaterSpawnPoint>>();
            foreach (var coalition in new[] { Coalition.Blue, Coalition.Red })
            {
                var zones = coalition == Coalition.Blue ? situation.GetBlueZones(invert) : situation.GetRedZones(invert);
                spotsByCoalition[coalition] = theater.SpawnPoints
                    .Where(x => ANY_LAND_SPOTS.Contains(x.PointType))
                    .Where(x => ShapeManager.IsPosValid(x.Coordinates, zones))
                    .Where(x => situation.NoSpawnZones.Count == 0 || !ShapeManager.IsPosValid(x.Coordinates, situation.NoSpawnZones))
                    .ToList();
                if (spotsByCoalition[coalition].Count == 0 || !airbases.Any(x => x.Coalition == coalition))
                    throw new BriefingRoomException(database, briefingRoom.LanguageKey, "DynamicCampaignNoSpace", coalition);
            }

            // Most situations only define the zones of each side: the front line is then where they meet
            var frontLine = situation.Frontline.Count >= 2
                ? situation.Frontline
                : CreateFrontLine(spotsByCoalition[Coalition.Blue].Select(x => x.Coordinates).ToList(), spotsByCoalition[Coalition.Red].Select(x => x.Coordinates).ToList());
            state.FrontLine = frontLine.Select(x => x.ToArray()).ToList();

            var usedSpots = new List<Coordinates>();
            foreach (var coalition in new[] { Coalition.Blue, Coalition.Red })
            {
                var layout = new CoalitionLayout(state, coalition, rng, frontLine, spotsByCoalition[coalition], usedSpots, airbases);
                layout.Build(airbases.Where(x => x.Coalition == coalition).ToList());
            }

            DynamicCampaignInventory.EnsureInitialized(state, DynamicCampaignInventory.GetPlayerAircraft(template));

            state.Reports.Add(new DCTurnReport
            {
                Turn = 0,
                Date = state.Date,
                Events = new() { new DCEvent("DCEventCampaignStarted", null, state.Name) }
            });
            return state;
        }

        private static DBEntrySituation PickSituation(IBriefingRoom briefingRoom, CampaignTemplate template, Random rng)
        {
            var database = briefingRoom.Database;
            static bool IsUsable(DBEntrySituation situation) =>
                situation.RedZones.Count > 0 && situation.BlueZones.Count > 0;

            var selected = database.GetEntry<DBEntrySituation>(template.ContextSituation);
            if (selected != null && Toolbox.StringICompare(selected.Theater, template.ContextTheater) && IsUsable(selected))
                return selected;

            var candidates = database.GetAllEntries<DBEntrySituation>()
                .Where(x => Toolbox.StringICompare(x.Theater, template.ContextTheater) && IsUsable(x))
                .ToList();
            if (candidates.Count == 0)
                throw new BriefingRoomException(database, briefingRoom.LanguageKey, "DynamicCampaignNoSituation", template.ContextTheater);
            if (selected != null)
                briefingRoom.PrintTranslatableWarning("DynamicCampaignSituationReplaced", template.ContextSituation);
            return candidates[rng.Next(candidates.Count)];
        }

        private static DateTime GenerateStartDate(CampaignTemplate template, Random rng)
        {
            var year = Toolbox.GetRandomYearFromDecade(template.ContextDecade);
            var month = rng.Next(1, 13);
            return new DateTime(year, month, rng.Next(1, DateTime.DaysInMonth(year, month) + 1));
        }

        // Front line between two sets of points: the axis goes from one side to the other, and for
        // each slice across it the front is halfway between the most forward points of each side.
        internal static List<Coordinates> CreateFrontLine(List<Coordinates> blue, List<Coordinates> red, int slices = 8)
        {
            var blueCenter = Coordinates.Sum(blue) / blue.Count;
            var redCenter = Coordinates.Sum(red) / red.Count;
            var axis = (redCenter - blueCenter).Normalize();
            var across = new Coordinates(-axis.Y, axis.X);
            static double Dot(Coordinates a, Coordinates b) => a.X * b.X + a.Y * b.Y;

            var blueAcross = blue.Select(x => Dot(x, across)).ToList();
            var redAcross = red.Select(x => Dot(x, across)).ToList();
            var min = Math.Max(blueAcross.Min(), redAcross.Min());
            var max = Math.Min(blueAcross.Max(), redAcross.Max());
            if (max - min < 20000) // Sides barely overlap: use the whole width
            {
                min = Math.Min(blueAcross.Min(), redAcross.Min());
                max = Math.Max(blueAcross.Max(), redAcross.Max());
            }

            var line = new List<Coordinates>();
            var width = (max - min) / slices;
            for (var i = 0; i < slices; i++)
            {
                var center = min + width * (i + 0.5);
                bool InSlice(Coordinates x) => Math.Abs(Dot(x, across) - center) <= width * 0.75;
                var blueInSlice = blue.Where(InSlice).ToList();
                var redInSlice = red.Where(InSlice).ToList();
                if (blueInSlice.Count == 0 || redInSlice.Count == 0) continue;
                var along = (blueInSlice.Max(x => Dot(x, axis)) + redInSlice.Min(x => Dot(x, axis))) / 2;
                line.Add(axis * along + across * center);
            }

            if (line.Count >= 2) return line;
            // Fallback: a straight line halfway between the closest points of each side
            var (closestBlue, closestRed) = blue.SelectMany(b => red.Select(r => (b, r))).MinBy(x => x.b.GetDistanceFrom(x.r));
            var middle = Coordinates.Lerp(closestBlue, closestRed, 0.5);
            return new() { middle - across * 40000, middle + across * 40000 };
        }

        internal static double DistanceToLine(Coordinates coordinates, List<Coordinates> line) =>
            ShapeManager.GetNearestPointBorder(coordinates, line).Item1;

        // Places the assets of one coalition
        private class CoalitionLayout
        {
            private readonly DynamicCampaignState State;
            private readonly Coalition Coalition;
            private readonly Random Rng;
            private readonly List<Coordinates> FrontLine;
            private readonly List<DBEntryTheaterSpawnPoint> Spots;
            private readonly List<Coordinates> UsedSpots;
            private readonly List<DBEntryAirbase> AllAirbases;
            private readonly string Prefix;

            internal CoalitionLayout(DynamicCampaignState state, Coalition coalition, Random rng, List<Coordinates> frontLine,
                List<DBEntryTheaterSpawnPoint> spots, List<Coordinates> usedSpots, List<DBEntryAirbase> allAirbases)
            {
                State = state;
                Coalition = coalition;
                Rng = rng;
                FrontLine = frontLine;
                Spots = spots;
                UsedSpots = usedSpots;
                AllAirbases = allAirbases;
                Prefix = coalition == Coalition.Blue ? "B" : "R";
            }

            internal void Build(List<DBEntryAirbase> ownAirbases)
            {
                var factories = PlaceSpread(DCAssetKind.Factory, "FAC", DynamicCampaignRules.FACTORIES, LARGE_SPOTS, 90, 400, deepest: true);
                var depots = PlaceSpread(DCAssetKind.Depot, "DEP", DynamicCampaignRules.DEPOTS, LARGE_SPOTS, 35, 100);
                foreach (var depot in depots) depot.Stock = 60;
                var frontUnits = PlaceAlongFront(DCAssetKind.FrontUnit, "BDE", DynamicCampaignRules.FRONT_UNITS, MEDIUM_SPOTS, 3, 25);
                var airDefenses = PlaceAirDefenses(depots, frontUnits);
                var airbases = PlaceAirbases(ownAirbases);

                // Every consumer is fed by its nearest depot
                foreach (var asset in frontUnits.Concat(airDefenses).Concat(airbases))
                    asset.SupplierId = Nearest(depots, asset.Coordinates)?.Id;

                // Convoys: factory > depot, depot > front unit
                foreach (var depot in depots)
                {
                    var factory = Nearest(factories, depot.Coordinates);
                    if (factory != null) AddGroundRoute(factory, depot);
                }
                foreach (var unit in frontUnits)
                {
                    var depot = State.GetAsset(unit.SupplierId);
                    if (depot != null) AddGroundRoute(depot, unit);
                }

                // Cargo flights: rear airbase > forward airbases (which then get no convoys)
                if (airbases.Count >= 2)
                {
                    var byDepth = airbases.OrderByDescending(x => DistanceToLine(x.Coordinates, FrontLine)).ToList();
                    var rear = byDepth.First();
                    foreach (var forward in byDepth.Skip(1).Reverse().Take(DynamicCampaignRules.AIR_ROUTES))
                        AddAirRoute(rear, forward);
                }
            }

            private List<DCAsset> PlaceSpread(DCAssetKind kind, string code, int count, SpawnPointType[] spotTypes, double minKm, double maxKm, bool deepest = false)
            {
                var candidates = Spots.Where(x => spotTypes.Contains(x.PointType)).Select(x => x.Coordinates).ToList();
                if (candidates.Count(IsFree) < count) // Not enough large spots (islands, small zones): any land spot will do
                    candidates = Spots.Select(x => x.Coordinates).ToList();

                var placed = new List<DCAsset>();
                for (var i = 0; i < count; i++)
                {
                    var free = candidates.Where(IsFree).ToList();
                    if (free.Count == 0) free = candidates.Where(x => IsFreeWithin(x, 1)).ToList(); // Crowded zone: allow neighbours
                    if (free.Count == 0) break;
                    var inRange = free.Where(x => IsInDepth(x, minKm, maxKm)).ToList();
                    if (inRange.Count == 0) // Zone too shallow or too deep: closest to the wanted depth
                        inRange = (deepest
                            ? free.OrderByDescending(x => DistanceToLine(x, FrontLine))
                            : free.OrderBy(x => Math.Abs(DistanceToLine(x, FrontLine) / 1000 - (minKm + maxKm) / 2)))
                            .Take(12).ToList();

                    // Farthest-point sampling, randomized among the best candidates, keeps assets spread along the front
                    var others = placed.Select(x => x.Coordinates).ToList();
                    var ranked = others.Count == 0
                        ? (deepest ? inRange.OrderByDescending(x => DistanceToLine(x, FrontLine)).ToList() : inRange.OrderBy(_ => Rng.Next()).ToList())
                        : inRange.OrderByDescending(x => others.Min(o => o.GetDistanceFrom(x))).ToList();
                    var choice = ranked[Rng.Next(Math.Min(3, ranked.Count))];
                    placed.Add(AddAsset(kind, $"{code}-{i + 1}", choice));
                }
                return placed;
            }

            private List<DCAsset> PlaceAlongFront(DCAssetKind kind, string code, int count, SpawnPointType[] spotTypes, double minKm, double maxKm)
            {
                var candidates = Spots.Where(x => spotTypes.Contains(x.PointType)).Select(x => x.Coordinates).ToList();
                if (candidates.Count(IsFree) < count)
                    candidates = Spots.Select(x => x.Coordinates).ToList();
                var placed = new List<DCAsset>();
                for (var i = 0; i < count; i++)
                {
                    var anchor = PointAlongLine(FrontLine, (i + 0.5) / count);
                    var free = candidates.Where(IsFree).ToList();
                    if (free.Count == 0) free = candidates.Where(x => IsFreeWithin(x, 1)).ToList();
                    var near = free.Where(x => IsInDepth(x, minKm, maxKm)).ToList();
                    if (near.Count == 0) near = free;
                    if (near.Count == 0) break;
                    var choice = near.MinBy(x => x.GetDistanceFrom(anchor));
                    placed.Add(AddAsset(kind, $"{code}-{i + 1}", choice));
                }
                return placed;
            }

            private List<DCAsset> PlaceAirDefenses(List<DCAsset> depots, List<DCAsset> frontUnits)
            {
                var candidates = Spots.Where(x => ANY_LAND_SPOTS.Contains(x.PointType)).Select(x => x.Coordinates).ToList();
                var placed = new List<DCAsset>();
                // Half protect depots, half cover the front
                var protectees = depots.Concat(frontUnits.OrderBy(_ => Rng.Next())).ToList();
                for (var i = 0; i < DynamicCampaignRules.AIR_DEFENSES && protectees.Count > 0; i++)
                {
                    var protectee = protectees[i % protectees.Count];
                    var free = candidates.Where(IsFree).Where(x => DistanceToLine(x, FrontLine) > 12000).ToList();
                    if (free.Count == 0) free = candidates.Where(IsFree).ToList();
                    if (free.Count == 0) free = candidates.Where(x => IsFreeWithin(x, 1)).ToList();
                    if (free.Count == 0) break;
                    var choice = free.OrderBy(x => x.GetDistanceFrom(protectee.Coordinates)).Take(4).ToList();
                    placed.Add(AddAsset(DCAssetKind.AirDefense, $"SAM-{i + 1}", choice[Rng.Next(choice.Count)]));
                }
                return placed;
            }

            private List<DCAsset> PlaceAirbases(List<DBEntryAirbase> ownAirbases)
            {
                // Closest to the front first, but not right on it
                var selected = ownAirbases
                    .OrderBy(x => DistanceToLine(x.Coordinates, FrontLine) < 10000 ? double.MaxValue : DistanceToLine(x.Coordinates, FrontLine))
                    .Take(DynamicCampaignRules.AIRBASES)
                    .ToList();
                return selected.Select(airbase =>
                {
                    var asset = AddAsset(DCAssetKind.Airbase, airbase.Name, airbase.Coordinates, markUsed: false);
                    asset.AirbaseId = airbase.ID;
                    asset.Stock = 40;
                    return asset;
                }).ToList();
            }

            private void AddGroundRoute(DCAsset from, DCAsset to) => AddRoute(DCAssetKind.GroundRoute, "MSR", from, to);

            private void AddAirRoute(DCAsset from, DCAsset to) => AddRoute(DCAssetKind.AirRoute, "AIR", from, to);

            private void AddRoute(DCAssetKind kind, string code, DCAsset from, DCAsset to)
            {
                var index = State.Assets.Count(x => x.Coalition == Coalition && x.Kind == kind) + 1;
                var route = AddAsset(kind, $"{code}-{index}", from.Coordinates, markUsed: false);
                route.FromId = from.Id;
                route.ToId = to.Id;
                route.X2 = to.X;
                route.Y2 = to.Y;
                route.Near = $"{from.Name} > {to.Name}";
            }

            private DCAsset AddAsset(DCAssetKind kind, string name, Coordinates coordinates, bool markUsed = true)
            {
                var asset = new DCAsset
                {
                    Id = $"{Prefix}{State.Assets.Count + 1}",
                    Coalition = Coalition,
                    Kind = kind,
                    Name = name,
                    X = coordinates.X,
                    Y = coordinates.Y,
                    Near = kind == DCAssetKind.Airbase ? null : AllAirbases.MinBy(x => x.Coordinates.GetDistanceFrom(coordinates))?.Name,
                };
                State.Assets.Add(asset);
                if (markUsed) UsedSpots.Add(coordinates);
                return asset;
            }

            private bool IsFree(Coordinates coordinates) => IsFreeWithin(coordinates, DynamicCampaignRules.MIN_ASSET_SEPARATION_KM);

            private bool IsFreeWithin(Coordinates coordinates, double separationKm) =>
                UsedSpots.All(x => x.GetDistanceFrom(coordinates) > separationKm * 1000);

            private bool IsInDepth(Coordinates coordinates, double minKm, double maxKm)
            {
                var distance = DistanceToLine(coordinates, FrontLine) / 1000;
                return distance >= minKm && distance <= maxKm;
            }

            private static DCAsset Nearest(IEnumerable<DCAsset> assets, Coordinates coordinates) =>
                assets.Where(x => x.IsAlive).MinBy(x => x.Coordinates.GetDistanceFrom(coordinates));
        }

        internal static Coordinates PointAlongLine(List<Coordinates> line, double fraction)
        {
            var lengths = new List<double>();
            for (var i = 1; i < line.Count; i++) lengths.Add(line[i - 1].GetDistanceFrom(line[i]));
            var target = lengths.Sum() * Math.Clamp(fraction, 0, 1);
            for (var i = 0; i < lengths.Count; i++)
            {
                if (target <= lengths[i] || i == lengths.Count - 1)
                    return Coordinates.Lerp(line[i], line[i + 1], lengths[i] == 0 ? 0 : Math.Min(1, target / lengths[i]));
                target -= lengths[i];
            }
            return line[0];
        }
    }
}
