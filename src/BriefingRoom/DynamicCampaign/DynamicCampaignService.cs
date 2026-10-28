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
using BriefingRoom4DCS.Mission;
using BriefingRoom4DCS.Template;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Entry point of the dynamic campaign for the frontends.
    // Turn loop: GetSorties > GenerateMission (sets Pending) > fly it in DCS >
    // ParseLog(dcs.log) > ResolveTurn (or SkipTurn) > next turn.
    public static class DynamicCampaignService
    {
        public static DynamicCampaignState Create(IBriefingRoom briefingRoom, CampaignTemplate template, string name) =>
            DynamicCampaignBuilder.Create(briefingRoom, template, name);

        public static List<DCSortie> GetSorties(IBriefingRoom briefingRoom, DynamicCampaignState state) =>
            DynamicCampaignMissionMaker.GetSorties(briefingRoom, state);

        public static DCSMission GenerateMission(IBriefingRoom briefingRoom, DynamicCampaignState state, DCSortie sortie) =>
            DynamicCampaignMissionMaker.Generate(briefingRoom, state, sortie);

        public static DCMissionResult ParseLog(DynamicCampaignState state, string dcsLog) =>
            state.Pending == null ? null : DynamicCampaignLogParser.Parse(dcsLog, state.Pending.Token);

        public static DCTurnReport ResolveTurn(DynamicCampaignState state, DCMissionResult result) =>
            DynamicCampaignSimulator.ResolveTurn(state, result);

        // The mission was not flown: the war goes on without the player
        public static DCTurnReport SkipTurn(DynamicCampaignState state) =>
            DynamicCampaignSimulator.ResolveTurn(state, null);

        public static void CancelMission(DynamicCampaignState state) => state.Pending = null;

        public static Dictionary<string, List<double[]>> GetMapData(IBriefingRoom briefingRoom, DynamicCampaignState state)
        {
            var data = new Dictionary<string, List<double[]>>
            {
                { "FRONTLINE", state.FrontLine.ToList() }
            };
            var frontLine = state.FrontLine.Select(x => new Coordinates(x[0], x[1])).ToList();
            var homeAirbase = state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase)
                .Where(x => x.IsOperational)
                .MinBy(x => DynamicCampaignBuilder.DistanceToLine(x.Coordinates, frontLine));
            foreach (var asset in state.Assets.Where(x => x.IsOperational))
            {
                var side = asset.Coalition == state.PlayerCoalition ? "Ally" : "Enemy";
                var label = DynamicCampaignText.AssetLabel(briefingRoom, state, asset).Replace("_", " ");
                switch (asset.Kind)
                {
                    case DCAssetKind.GroundRoute:
                        if (DynamicCampaignMissionMaker.IsGroundRouteActive(state, asset))
                            data.Add($"ROUTE_{asset.Id}", new() { asset.Coordinates.ToArray(), asset.Coordinates2.ToArray() });
                        break;
                    case DCAssetKind.AirRoute:
                        data.Add($"ROUTE_{asset.Id}", new() { asset.Coordinates.ToArray(), asset.Coordinates2.ToArray() });
                        break;
                    case DCAssetKind.Airbase:
                        // The map centers on AIRBASE_HOME: our airbase closest to the front
                        data.Add(asset == homeAirbase ? "AIRBASE_HOME" : $"AIRBASE_{side}_{asset.Id}_NAME_{asset.Name}", new() { asset.Coordinates.ToArray() });
                        break;
                    case DCAssetKind.AirDefense:
                        data.Add($"UNIT-{side}-SAM Medium-{asset.Id} {label}", new() { asset.Coordinates.ToArray() });
                        break;
                    case DCAssetKind.FrontUnit:
                        data.Add($"UNIT-{side}-Vehicle MBT-{asset.Id} {label}", new() { asset.Coordinates.ToArray() });
                        break;
                    case DCAssetKind.Depot:
                    case DCAssetKind.Factory:
                        data.Add($"SUPPLY_{side}_{asset.Id}_NAME_{label}", new() { asset.Coordinates.ToArray() });
                        break;
                }
            }
            if (state.Pending != null && state.GetAsset(state.Pending.TargetId) is DCAsset target)
                data.Add("OBJECTIVE_AREA", new() { target.Center.ToArray() });
            return data;
        }

        // Summary per coalition, for display
        public static DCForces GetForces(DynamicCampaignState state, Coalition coalition)
        {
            var assets = state.Assets.Where(x => x.Coalition == coalition && !x.Removed).ToList();
            double Average(DCAssetKind kind) => assets.Where(x => x.Kind == kind).Select(x => x.IsAlive ? x.Strength : 0).DefaultIfEmpty(0).Average();
            return new DCForces
            {
                Factories = assets.Count(x => x.Kind == DCAssetKind.Factory && x.IsAlive),
                Depots = assets.Count(x => x.Kind == DCAssetKind.Depot && x.IsAlive),
                Supplies = assets.Where(x => x.Kind == DCAssetKind.Depot && x.IsAlive).Sum(x => x.Stock),
                FrontStrength = Average(DCAssetKind.FrontUnit),
                AirDefenses = assets.Count(x => x.Kind == DCAssetKind.AirDefense && x.IsAlive),
                AirReadiness = Average(DCAssetKind.Airbase),
                Airbases = assets.Count(x => x.Kind == DCAssetKind.Airbase && x.IsAlive),
                RoutesCut = assets.Count(x => x.IsRoute && x.CutTurns > 0),
                Fighters = SumAircraft(assets, DynamicCampaignInventory.FIGHTER),
                AttackAircraft = SumAircraft(assets, DynamicCampaignInventory.ATTACK),
                PlayerAircraft = coalition == state.PlayerCoalition ? state.PlayerAircraft.Keys.ToDictionary(x => x, x => SumAircraft(assets, x)) : new(),
                AirToAirMissiles = SumWeapons(assets, DynamicCampaignInventory.AAM),
                GuidedWeapons = SumWeapons(assets, DynamicCampaignInventory.AGM) + SumWeapons(assets, DynamicCampaignInventory.GUIDED_BOMB) + SumWeapons(assets, DynamicCampaignInventory.ARM),
                UnguidedWeapons = SumWeapons(assets, DynamicCampaignInventory.BOMB) + SumWeapons(assets, DynamicCampaignInventory.ROCKET),
            };
        }

        private static int SumAircraft(List<DCAsset> assets, string type) =>
            (int)assets.Where(x => x.Kind == DCAssetKind.Airbase && x.IsAlive).Sum(x => Math.Floor(DynamicCampaignInventory.Get(x.Aircraft, type)));

        // Weapons at airbases and depots (what is available to fly), not still at the factories
        private static int SumWeapons(List<DCAsset> assets, string weapon) =>
            (int)assets.Where(x => (x.Kind == DCAssetKind.Airbase || x.Kind == DCAssetKind.Depot) && x.IsAlive).Sum(x => Math.Floor(DynamicCampaignInventory.Get(x.Weapons, weapon)));
    }

    public class DCForces
    {
        public int Factories { get; set; }
        public int Depots { get; set; }
        public double Supplies { get; set; }
        public double FrontStrength { get; set; }
        public int AirDefenses { get; set; }
        public double AirReadiness { get; set; }
        public int Airbases { get; set; }
        public int RoutesCut { get; set; }
        public int Fighters { get; set; }
        public int AttackAircraft { get; set; }
        public Dictionary<string, int> PlayerAircraft { get; set; } = new();
        public int AirToAirMissiles { get; set; }
        public int GuidedWeapons { get; set; }
        public int UnguidedWeapons { get; set; }
    }
}
