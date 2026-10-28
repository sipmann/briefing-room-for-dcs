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
using BriefingRoom4DCS.Template;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Weapons (by category) and aircraft (AI fleets by role, plus the player's
    // aircraft types) held by factories, depots and airbases. Factories produce
    // them, the supply network moves them, missions and AI sorties consume them.
    internal static class DynamicCampaignInventory
    {
        internal const string AAM = "AAM";
        internal const string ARM = "ARM";
        internal const string AGM = "AGM";
        internal const string GUIDED_BOMB = "GuidedBomb";
        internal const string BOMB = "Bomb";
        internal const string ROCKET = "Rocket";
        internal static readonly string[] WEAPONS = { AAM, ARM, AGM, GUIDED_BOMB, BOMB, ROCKET };

        internal const string FIGHTER = "Fighter";
        internal const string ATTACK = "Attack";
        internal static readonly string[] AI_FLEETS = { FIGHTER, ATTACK };

        // Initial stock of an airbase (depots hold twice as much, caps are twice the initial stock)
        private static readonly Dictionary<string, double> AIRBASE_WEAPONS = new()
        {
            { AAM, 40 }, { ARM, 12 }, { AGM, 24 }, { GUIDED_BOMB, 24 }, { BOMB, 60 }, { ROCKET, 80 }
        };
        private static readonly Dictionary<string, double> AIRBASE_AIRCRAFT = new() { { FIGHTER, 8 }, { ATTACK, 8 } };
        private const double PLAYER_TYPE_AIRCRAFT = 6;

        // Production of a factory at full strength, per turn
        private static readonly Dictionary<string, double> FACTORY_WEAPONS = new()
        {
            { AAM, 6 }, { ARM, 2 }, { AGM, 4 }, { GUIDED_BOMB, 4 }, { BOMB, 10 }, { ROCKET, 12 }
        };
        private const double FACTORY_AI_AIRCRAFT = 0.5;
        private const double FACTORY_PLAYER_AIRCRAFT = 0.35;

        // Weapons a sortie can use (any of them), and how many it needs at the departure airbase
        internal const double SORTIE_MIN_WEAPONS = 2;
        internal static readonly Dictionary<DCSortieKind, string[]> SORTIE_WEAPONS = new()
        {
            { DCSortieKind.StrikeFactory, new[] { GUIDED_BOMB, BOMB, AGM } },
            { DCSortieKind.StrikeDepot, new[] { GUIDED_BOMB, BOMB, AGM } },
            { DCSortieKind.CloseAirSupport, new[] { AGM, ROCKET, BOMB, GUIDED_BOMB } },
            { DCSortieKind.SEAD, new[] { ARM, AGM } },
            { DCSortieKind.InterdictConvoy, new[] { AGM, ROCKET, BOMB, GUIDED_BOMB } },
            { DCSortieKind.InterceptCargo, new[] { AAM } },
            { DCSortieKind.AirbaseStrike, new[] { BOMB, GUIDED_BOMB, ROCKET, AGM } },
        };

        // ---------------------------------------------------------------- Setup

        // Player aircraft types and how many of each a mission needs
        internal static Dictionary<string, int> GetPlayerAircraft(CampaignTemplate template) =>
            template.PlayerFlightGroups
                .Where(x => !x.Hostile)
                .GroupBy(x => x.Aircraft)
                .ToDictionary(x => x.Key, x => x.Sum(g => g.Count));

        // Gives a stock to campaigns created before inventories existed, and adds new player aircraft types
        internal static void EnsureInitialized(DynamicCampaignState state, Dictionary<string, int> playerAircraft)
        {
            state.PlayerAircraft = playerAircraft;
            var playerAircraftTypes = playerAircraft.Keys;
            foreach (var asset in state.Assets.Where(x => !x.IsRoute))
            {
                if (asset.Weapons == null)
                {
                    asset.Weapons = new();
                    var multiplier = asset.Kind switch { DCAssetKind.Airbase => 1, DCAssetKind.Depot => 2, _ => 0 };
                    foreach (var weapon in WEAPONS)
                        asset.Weapons[weapon] = asset.IsAlive ? AIRBASE_WEAPONS[weapon] * multiplier : 0;
                }
                if (asset.Aircraft == null)
                {
                    asset.Aircraft = new();
                    if (asset.Kind == DCAssetKind.Airbase && asset.IsAlive)
                        foreach (var fleet in AI_FLEETS) asset.Aircraft[fleet] = AIRBASE_AIRCRAFT[fleet];
                }
            }

            // Player aircraft are spread over the player's airbases
            foreach (var type in playerAircraftTypes)
            {
                var airbases = state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase).Where(x => x.IsAlive).ToList();
                if (airbases.Any(x => x.Aircraft.ContainsKey(type))) continue;
                foreach (var airbase in airbases) airbase.Aircraft[type] = PLAYER_TYPE_AIRCRAFT;
            }
            state.FormatVersion = DynamicCampaignState.FORMAT_VERSION;
        }

        // ---------------------------------------------------------------- Stock helpers

        internal static double Get(Dictionary<string, double> stock, string key) =>
            stock != null && stock.TryGetValue(key, out var value) ? value : 0;

        internal static void Add(Dictionary<string, double> stock, string key, double amount)
        {
            if (stock == null) return;
            stock[key] = Math.Max(0, Get(stock, key) + amount);
        }

        internal static double Take(Dictionary<string, double> stock, string key, double amount)
        {
            var taken = Math.Min(Get(stock, key), Math.Max(0, amount));
            if (taken > 0) stock[key] -= taken;
            return taken;
        }

        internal static void Clear(DCAsset asset)
        {
            asset.Weapons?.Clear();
            asset.Aircraft?.Clear();
        }

        internal static double Cap(DCAsset asset, string weapon) =>
            AIRBASE_WEAPONS[weapon] * (asset.Kind == DCAssetKind.Depot ? 4 : asset.Kind == DCAssetKind.Factory ? 6 : 2);

        // Moves a share of every weapon from one asset to another, within the destination capacity
        internal static void TransferWeapons(DCAsset from, DCAsset to, double share)
        {
            if (from?.Weapons == null || to?.Weapons == null || share <= 0) return;
            foreach (var weapon in WEAPONS)
            {
                var room = Math.Max(0, Cap(to, weapon) - Get(to.Weapons, weapon));
                Add(to.Weapons, weapon, Take(from.Weapons, weapon, Math.Min(Get(from.Weapons, weapon) * share, room)));
            }
        }

        internal static double TotalAircraft(DCAsset airbase, string type) => Get(airbase.Aircraft, type);

        // ---------------------------------------------------------------- Production

        internal static void Produce(DynamicCampaignState state, Coalition coalition)
        {
            var playerAircraftTypes = state.PlayerAircraft.Keys;
            var factories = state.GetAssets(coalition, DCAssetKind.Factory).Where(x => x.IsAlive).ToList();
            var airbases = state.GetAssets(coalition, DCAssetKind.Airbase).Where(x => x.IsAlive).ToList();
            foreach (var factory in factories)
            {
                var output = factory.Strength / 100;
                foreach (var weapon in WEAPONS)
                    Add(factory.Weapons, weapon, Math.Min(FACTORY_WEAPONS[weapon] * output, Cap(factory, weapon) - Get(factory.Weapons, weapon)));
                foreach (var fleet in AI_FLEETS)
                    Add(factory.Aircraft, fleet, FACTORY_AI_AIRCRAFT * output);
                if (coalition == state.PlayerCoalition)
                    foreach (var type in playerAircraftTypes)
                        Add(factory.Aircraft, type, FACTORY_PLAYER_AIRCRAFT * output);
            }

            // New aircraft are ferried (whole aircraft only) to the airbase that needs them most
            if (airbases.Count == 0) return;
            foreach (var factory in factories)
                foreach (var type in factory.Aircraft.Keys.ToList())
                {
                    var whole = Math.Floor(Get(factory.Aircraft, type));
                    if (whole < 1) continue;
                    factory.Aircraft[type] -= whole;
                    Add(airbases.MinBy(x => Get(x.Aircraft, type)).Aircraft, type, whole);
                }
        }

        // ---------------------------------------------------------------- Missions

        // The player's airbase closest to the target with enough aircraft for every player flight group
        internal static DCAsset ChooseDeparture(DynamicCampaignState state, Dictionary<string, int> playerAircraft, Coordinates target) =>
            state.GetAssets(state.PlayerCoalition, DCAssetKind.Airbase)
                .Where(x => x.IsAlive && playerAircraft.All(p => TotalAircraft(x, p.Key) >= p.Value))
                .MinBy(x => x.Coordinates.GetDistanceFrom(target));

        internal static bool HasWeaponsFor(DCAsset airbase, DCSortieKind kind) =>
            SORTIE_WEAPONS[kind].Sum(x => Get(airbase.Weapons, x)) >= SORTIE_MIN_WEAPONS;

        // Effective fighters of a coalition: fighters at each airbase, scaled by its readiness, without missiles they don't count
        internal static double GetFighterPower(DynamicCampaignState state, Coalition coalition) =>
            state.GetAssets(coalition, DCAssetKind.Airbase)
                .Where(x => x.IsAlive && Get(x.Weapons, AAM) >= 2)
                .Sum(x => Get(x.Aircraft, FIGHTER) * x.Strength / 100);

        internal static double InitialFighters => AIRBASE_AIRCRAFT[FIGHTER] * DynamicCampaignRules.AIRBASES;

        internal static string Describe(DCAsset asset, Func<string, string> translate)
        {
            var parts = new List<string>();
            if (asset.Aircraft != null && asset.Aircraft.Values.Any(x => x >= 1))
                parts.Add(string.Join(", ", asset.Aircraft.Where(x => x.Value >= 1).Select(x => $"{(AI_FLEETS.Contains(x.Key) ? translate($"DCFleet{x.Key}") : x.Key)} {Math.Floor(x.Value)}")));
            if (asset.Weapons != null && asset.Weapons.Values.Any(x => x >= 1))
                parts.Add(string.Join(", ", WEAPONS.Where(x => Get(asset.Weapons, x) >= 1).Select(x => $"{translate($"DCWeapon{x}")} {Math.Floor(Get(asset.Weapons, x))}")));
            return string.Join(" · ", parts);
        }
    }
}
