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
using System.IO;
using System.Linq;
using BriefingRoom4DCS.Data;
using BriefingRoom4DCS.Generator;
using BriefingRoom4DCS.Generator.Mission;
using BriefingRoom4DCS.Mission;
using BriefingRoom4DCS.Template;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Turns a sortie picked from the campaign state into a regular BriefingRoom
    // mission: the target asset becomes the objective (at its campaign
    // position, sized by its strength), the enemy/friendly air defense and air
    // force levels come from the campaign, and a reporter script is added.
    internal static class DynamicCampaignMissionMaker
    {
        private static readonly string REPORTER_LUA = Path.Combine(BRPaths.INCLUDE_LUA, "DynamicCampaign", "Reporter.lua");

        private sealed record SortieDefinition(string Preset, string Target, string Behavior, string Task);

        private static readonly Dictionary<DCSortieKind, SortieDefinition> SORTIES = new()
        {
            { DCSortieKind.StrikeFactory, new("DeepStrike", "StructureProduction", "Idle", "DestroyAll") },
            { DCSortieKind.StrikeDepot, new("DeepStrike", "StructureMilitary", "Idle", "DestroyAll") },
            { DCSortieKind.CloseAirSupport, new("CloseAirSupport", null, "Idle", null) },
            { DCSortieKind.SEAD, new("DEAD", "AirDefenseSAMMediumLong", "Idle", "DestroyRadars") },
            { DCSortieKind.InterdictConvoy, new("Interdiction", "VehicleTransport", "PatrollingOnRoads", "DestroyAll") },
            { DCSortieKind.InterceptCargo, new("Interception", "PlaneTransport", "Patrolling", "DestroyAll") },
            { DCSortieKind.AirbaseStrike, new("OffensiveCounterAir", null, null, null) },
        };

        internal static DCSortieKind? GetSortieKind(DCAsset asset) => asset.Kind switch
        {
            DCAssetKind.Factory => DCSortieKind.StrikeFactory,
            DCAssetKind.Depot => DCSortieKind.StrikeDepot,
            DCAssetKind.FrontUnit => DCSortieKind.CloseAirSupport,
            DCAssetKind.AirDefense => DCSortieKind.SEAD,
            DCAssetKind.GroundRoute => DCSortieKind.InterdictConvoy,
            DCAssetKind.AirRoute => DCSortieKind.InterceptCargo,
            DCAssetKind.Airbase => DCSortieKind.AirbaseStrike,
            _ => null
        };

        internal static List<DCSortie> GetSorties(IBriefingRoom briefingRoom, DynamicCampaignState state)
        {
            if (state.Outcome != DCOutcome.Ongoing) return new();
            var template = LoadTemplate(briefingRoom, state);
            DynamicCampaignInventory.EnsureInitialized(state, DynamicCampaignInventory.GetPlayerAircraft(template));
            var profile = ObjectivePresetSuitabilityProfile.FromPlayerFlightGroups(briefingRoom.Database, template.PlayerFlightGroups);
            return state.Assets
                .Where(x => x.Coalition == state.EnemyCoalition && x.IsOperational)
                .Where(x => x.Kind != DCAssetKind.GroundRoute || IsGroundRouteActive(state, x))
                .Select(x => (asset: x, kind: GetSortieKind(x)))
                .Where(x => x.kind.HasValue)
                .Select(x =>
                {
                    var preset = SORTIES[x.kind.Value].Preset;
                    var departure = DynamicCampaignInventory.ChooseDeparture(state, state.PlayerAircraft, x.asset.Center);
                    return new DCSortie
                    {
                        Kind = x.kind.Value,
                        TargetId = x.asset.Id,
                        PresetId = preset,
                        Suitable = ObjectivePresetSuitability.GetUnsuitabilityReason(briefingRoom.Database, preset, profile) == ObjectivePresetUnsuitabilityReason.None,
                        DepartureId = departure?.Id,
                        Unavailable = GetUnavailableReason(departure, x.kind.Value),
                        Threat = GetEnemyAirDefenseLevel(state, x.asset.Center),
                    };
                })
                .OrderBy(x => x.Kind)
                .ThenBy(x => state.GetAsset(x.TargetId).Name)
                .ToList();
        }

        // A convoy only runs if both ends are standing
        internal static bool IsGroundRouteActive(DynamicCampaignState state, DCAsset route)
        {
            var from = state.GetAsset(route.FromId);
            var to = state.GetAsset(route.ToId);
            return from != null && to != null && from.IsAlive && to.IsAlive;
        }

        internal static CampaignTemplate LoadTemplate(IBriefingRoom briefingRoom, DynamicCampaignState state)
        {
            var template = new CampaignTemplate(briefingRoom);
            template.LoadFromString(state.TemplateIni);
            return template;
        }

        internal static DCSMission Generate(IBriefingRoom briefingRoom, DynamicCampaignState state, DCSortie sortie)
        {
            var database = briefingRoom.Database;
            var target = state.GetAsset(sortie.TargetId);
            if (target == null || !target.IsOperational || target.Coalition != state.EnemyCoalition)
                throw new BriefingRoomException(database, briefingRoom.LanguageKey, "DynamicCampaignInvalidTarget");

            var campaignTemplate = LoadTemplate(briefingRoom, state);
            DynamicCampaignInventory.EnsureInitialized(state, DynamicCampaignInventory.GetPlayerAircraft(campaignTemplate));
            var departure = DynamicCampaignInventory.ChooseDeparture(state, state.PlayerAircraft, target.Center);
            var unavailable = GetUnavailableReason(departure, sortie.Kind);
            if (unavailable != null)
                throw new BriefingRoomException(database, briefingRoom.LanguageKey, unavailable);

            DCSMission mission = null;
            Exception lastError = null;
            for (var attempt = 0; attempt < 3 && mission == null; attempt++)
            {
                // Last attempt: ignore coalition borders, the front may have moved past the situation zones
                var template = CreateMissionTemplate(database, state, campaignTemplate, target, sortie.Kind, departure, ignoreBorders: attempt == 2);
                try
                {
                    mission = MissionGenerator.GenerateRetryable(briefingRoom, template);
                }
                catch (Exception ex) when (ex is BriefingRoomException || ex is BriefingRoomRawException)
                {
                    lastError = ex;
                }
            }
            if (mission == null)
                throw new BriefingRoomException(database, briefingRoom.LanguageKey, "DynamicCampaignMissionFailed", lastError);

            // The spawn point actually used becomes the asset position, so it stays put from now on
            if (!target.IsRoute && target.Kind != DCAssetKind.Airbase && mission.ObjectiveCoordinates.Count > 0)
            {
                target.X = mission.ObjectiveCoordinates[0].X;
                target.Y = mission.ObjectiveCoordinates[0].Y;
            }

            mission.SetValue("DateDay", state.Date.Day);
            mission.SetValue("DateMonth", state.Date.Month);
            mission.SetValue("DateYear", state.Date.Year);
            mission.SetValue("BriefingDate", $"{state.Date.Day:00}/{state.Date.Month:00}/{state.Date.Year:0000}");

            var targetWaypoint = MarkTargetWaypoint(briefingRoom, mission);
            AddBriefingRemarks(briefingRoom, state, target, departure, sortie.Kind, targetWaypoint, ref mission);

            var token = Guid.NewGuid().ToString("N")[..12];
            var reporterLua = File.ReadAllText(REPORTER_LUA);
            GeneratorTools.ReplaceKey(ref reporterLua, "Token", token);
            GeneratorTools.ReplaceKey(ref reporterLua, "RadarsDown", target.RadarsDown ? "true" : "false");
            GeneratorTools.ReplaceKey(ref reporterLua, "Stock", string.Join(", ", DynamicCampaignInventory.WEAPONS.Select(x =>
                $"{x} = {Math.Floor(DynamicCampaignInventory.Get(departure.Weapons, x))}")));
            GeneratorTools.ReplaceKey(ref reporterLua, "StockLabels", string.Join(", ", DynamicCampaignInventory.WEAPONS.Select(x =>
                $"{x} = \"{LuaText(briefingRoom.Translate($"DCWeapon{x}"))}\"")));
            GeneratorTools.ReplaceKey(ref reporterLua, "StockWarning", LuaText(briefingRoom.Translate("DCStockWarning", departure.Name)));
            GeneratorTools.ReplaceKey(ref reporterLua, "EndMissionLabel", LuaText(briefingRoom.Translate("EndMission")));
            mission.AppendValue("ScriptMissionFeatures", reporterLua);

            state.Pending = new DCPendingMission
            {
                Token = token,
                Turn = state.Turn,
                Kind = sortie.Kind,
                TargetId = target.Id,
                DepartureId = departure?.Id,
                MissionName = mission.Briefing.Name,
                GeneratedUtc = DateTime.UtcNow,
                RadarsDown = target.Kind == DCAssetKind.AirDefense && target.RadarsDown,
            };
            return mission;
        }

        private static MissionTemplate CreateMissionTemplate(IDatabase database, DynamicCampaignState state, CampaignTemplate campaignTemplate, DCAsset target, DCSortieKind kind, DCAsset departure, bool ignoreBorders)
        {
            var template = CampaignGenerator.CreateBaseMissionTemplate(database, campaignTemplate, state.Turn - 1, state.Turn);
            template.ContextTheater = state.Theater;
            template.ContextSituation = state.Situation;
            template.ContextCustomFrontLine = state.FrontLine.Select(x => x.ToList()).ToList();
            template.EnvironmentSeason = GetSeason(state.Date.Month);
            template.FlightPlanTheaterStartingAirbase = departure?.AirbaseId ?? campaignTemplate.FlightPlanTheaterStartingAirbase;
            template.FlightPlanTheaterDestinationAirbase = "home"; // Aircraft and weapons return to the departure stock
            if (!template.OptionsMission.Contains("EndMissionOnCommand"))
                template.OptionsMission.Add("EndMissionOnCommand");
            if (ignoreBorders && !template.OptionsMission.Contains("SpawnAnywhere"))
                template.OptionsMission.Add("SpawnAnywhere");

            // Opposition near the target and overall air power come from the campaign
            template.SituationEnemyAirDefense = GetEnemyAirDefenseLevel(state, target.Center);
            template.SituationEnemyAirForce = GetAirForceLevel(state, state.EnemyCoalition);
            template.SituationFriendlyAirDefense = GetAirDefenseLevel(state, state.PlayerCoalition, departure?.Coordinates ?? target.Center);
            template.SituationFriendlyAirForce = GetAirForceLevel(state, state.PlayerCoalition);

            var definition = SORTIES[kind];
            var objective = new MissionTemplateObjective(database, definition.Preset, GetTargetCount(target));
            if (definition.Target != null) objective.Target = definition.Target;
            if (definition.Behavior != null) objective.TargetBehavior = definition.Behavior;
            if (definition.Task != null) objective.Task = definition.Task;
            // Radars already destroyed: the reporter removes them at start, the rest of the site is the target
            if (kind == DCSortieKind.SEAD && target.RadarsDown) objective.Task = "DestroyAll";
            objective.CoordinateHint_ = target.IsRoute
                ? Coordinates.Lerp(target.Coordinates, target.Coordinates2, 0.35 + Toolbox.RandomDouble(0.3))
                : target.Coordinates;
            template.Objectives = new() { objective };
            return template;
        }

        private static Amount GetTargetCount(DCAsset target) => target.Strength switch
        {
            >= 80 => Amount.High,
            >= 55 => Amount.Average,
            >= 30 => Amount.Low,
            _ => Amount.VeryLow
        };

        // Sum of the strength of the SAM sites covering a point (blind sites cover nothing)
        internal static double GetAirDefenseCover(DynamicCampaignState state, Coalition coalition, Coordinates coordinates) =>
            state.GetAssets(coalition, DCAssetKind.AirDefense)
                .Where(x => x.IsAlive && !x.RadarsDown && x.Coordinates.GetDistanceFrom(coordinates) < DynamicCampaignRules.AIR_DEFENSE_COVER_KM * 1000)
                .Sum(x => x.Strength / 100);

        internal static double GetAirReadiness(DynamicCampaignState state, Coalition coalition)
        {
            var airbases = state.GetAssets(coalition, DCAssetKind.Airbase).ToList();
            return airbases.Count == 0 ? 0 : airbases.Average(x => x.IsAlive ? x.Strength : 0) / 100;
        }

        internal static AmountNR GetAirDefenseLevel(DynamicCampaignState state, Coalition coalition, Coordinates coordinates)
        {
            var cover = GetAirDefenseCover(state, coalition, coordinates);
            var overall = state.GetAssets(coalition, DCAssetKind.AirDefense).Count(x => x.IsAlive);
            if (cover <= 0) return overall == 0 ? AmountNR.None : AmountNR.VeryLow;
            return cover switch
            {
                < 0.6 => AmountNR.Low,
                < 1.4 => AmountNR.Average,
                < 2.2 => AmountNR.High,
                _ => AmountNR.VeryHigh
            };
        }

        // Enemy air defenses around a target, no more than the player's flight can face
        internal static AmountNR GetEnemyAirDefenseLevel(DynamicCampaignState state, Coordinates coordinates)
        {
            var level = GetAirDefenseLevel(state, state.EnemyCoalition, coordinates);
            var flightSize = state.PlayerAircraft.Values.Sum();
            var maxLevel = flightSize <= DynamicCampaignRules.SMALL_FLIGHT ? AmountNR.Average
                : flightSize <= DynamicCampaignRules.MEDIUM_FLIGHT ? AmountNR.High
                : AmountNR.VeryHigh;
            return level > maxLevel ? maxLevel : level;
        }

        // Fighters actually available (fleet x readiness, with missiles), relative to the initial fleet
        private static AmountNR GetAirForceLevel(DynamicCampaignState state, Coalition coalition) =>
            (DynamicCampaignInventory.GetFighterPower(state, coalition) / DynamicCampaignInventory.InitialFighters) switch
        {
            < 0.1 => AmountNR.None,
            < 0.3 => AmountNR.VeryLow,
            < 0.55 => AmountNR.Low,
            < 0.8 => AmountNR.Average,
            _ => AmountNR.High
        };

        internal static string GetUnavailableReason(DCAsset departure, DCSortieKind kind)
        {
            if (departure == null) return "DCUnavailableNoAircraft";
            if (!DynamicCampaignInventory.HasWeaponsFor(departure, kind)) return "DCUnavailableNoWeapons";
            return null;
        }

        private static string LuaText(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");

        private static Season GetSeason(int month) => month switch
        {
            3 or 4 or 5 => Season.Spring,
            6 or 7 or 8 => Season.Summer,
            9 or 10 or 11 => Season.Fall,
            _ => Season.Winter
        };

        // Flags the objective waypoint in the flight plans (kneeboard pages); returns "number: name"
        private static string MarkTargetWaypoint(IBriefingRoom briefingRoom, DCSMission mission)
        {
            // Script waypoints are the objective waypoints, in objective order
            var objectiveWaypoint = mission.Waypoints.FirstOrDefault(x => !x.ScriptIgnore);
            if (string.IsNullOrEmpty(objectiveWaypoint.Name)) return null;

            var marker = briefingRoom.Translate("DCTargetWaypointMarker");
            string found = null;
            foreach (var flight in mission.Briefing.FlightBriefings)
                for (var i = 0; i < flight.Waypoints.Count; i++)
                {
                    var columns = flight.Waypoints[i].Split('\t');
                    if (columns[0] != $"{i}: {objectiveWaypoint.Name}") continue;
                    found ??= columns[0];
                    columns[0] = $"{columns[0]} {marker}";
                    flight.Waypoints[i] = string.Join('\t', columns);
                }
            return found;
        }

        private static void AddBriefingRemarks(IBriefingRoom briefingRoom, DynamicCampaignState state, DCAsset target, DCAsset departure, DCSortieKind kind, string targetWaypoint, ref DCSMission mission)
        {
            var remarks = new List<string>
            {
                briefingRoom.Translate("DCBriefingDay", state.Turn, state.Name),
                briefingRoom.Translate($"DCBriefingImpact{kind}", DynamicCampaignText.AssetLabel(briefingRoom, state, target)),
            };
            if (targetWaypoint != null)
                remarks.Add(briefingRoom.Translate("DCBriefingTargetWaypoint", targetWaypoint));
            if (kind == DCSortieKind.SEAD && target.RadarsDown)
                remarks.Add(briefingRoom.Translate("DCBriefingRadarsDown", target.RadarRepairTurns));
            remarks.Add(briefingRoom.Translate("DCBriefingStock", departure.Name, DynamicCampaignInventory.Describe(departure, briefingRoom.Translate)));
            remarks.Reverse();
            foreach (var remark in remarks)
                mission.Briefing.AddItem(DCSMissionBriefingItemType.Remark, remark, true);
            mission.SetValue("DescriptionText", mission.Briefing.GetBriefingAsRawText(briefingRoom.Database, ref mission, "\\\n"));
        }
    }
}
