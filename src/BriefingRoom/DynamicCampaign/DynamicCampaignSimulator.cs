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

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Resolves a campaign turn: applies the player's mission result, then lets
    // both coalitions move supplies, repair, re-route, fly AI air operations
    // and fight on the front line. Deterministic for a given campaign seed and turn.
    internal static class DynamicCampaignSimulator
    {
        private static readonly Coalition[] COALITIONS = { Coalition.Blue, Coalition.Red };

        internal static DCTurnReport ResolveTurn(DynamicCampaignState state, DCMissionResult result)
        {
            var rng = new Random(unchecked(state.Seed + state.Turn * 7919));
            var report = new DCTurnReport { Turn = state.Turn, Date = state.Date };

            if (state.Pending != null && result != null)
                ApplyMissionResult(state, state.Pending, result, report);
            else
                report.Events.Add(new DCEvent("DCEventMissionNotFlown", state.PlayerCoalition));
            state.Pending = null;

            foreach (var coalition in COALITIONS)
                RunLogistics(state, coalition, report);
            foreach (var coalition in COALITIONS)
                RunCommander(state, coalition, rng, report);
            foreach (var coalition in COALITIONS)
                RunAirOperations(state, coalition, rng, report);
            RunGroundWar(state, rng, report);
            CheckOutcome(state, report);

            state.Reports.Add(report);
            state.Turn++;
            state.Date = state.Date.AddDays(rng.Next(DynamicCampaignRules.MIN_DAYS_PER_TURN, DynamicCampaignRules.MAX_DAYS_PER_TURN + 1));
            return report;
        }

        // ---------------------------------------------------------------- Player mission

        internal static void ApplyMissionResult(DynamicCampaignState state, DCPendingMission pending, DCMissionResult result, DCTurnReport report)
        {
            var target = state.GetAsset(pending.TargetId);
            var objective = result.Objectives.FirstOrDefault();
            if (target != null && objective != null && objective.Total > 0)
            {
                var fraction = Math.Clamp(objective.Dead / (double)objective.Total, 0, 1);
                report.Events.Add(new DCEvent("DCEventMissionResult", state.PlayerCoalition, Ref(target), Percent(fraction)));
                switch (target.Kind)
                {
                    case DCAssetKind.GroundRoute:
                    case DCAssetKind.AirRoute:
                        Damage(state, target, fraction * 70, report);
                        if (fraction >= 0.5 || objective.Complete) CutRoute(target, report);
                        break;
                    case DCAssetKind.AirDefense:
                        // Radars down = site out of action until repaired, even if launchers survive
                        Damage(state, target, fraction * target.Strength, report);
                        if (objective.Complete && target.IsAlive)
                            target.Strength = Math.Min(target.Strength, 25);
                        break;
                    case DCAssetKind.Airbase:
                        DestroyParkedAircraft(target, objective.Dead, report);
                        Damage(state, target, fraction * 45, report);
                        target.Stock = Math.Max(0, target.Stock - fraction * 20);
                        break;
                    default:
                        Damage(state, target, fraction * target.Strength, report);
                        if (target.Kind == DCAssetKind.Depot) target.Stock *= 1 - fraction;
                        if (target.Kind == DCAssetKind.Depot || target.Kind == DCAssetKind.Factory)
                            foreach (var weapon in DynamicCampaignInventory.WEAPONS)
                                DynamicCampaignInventory.Take(target.Weapons, weapon, DynamicCampaignInventory.Get(target.Weapons, weapon) * fraction);
                        break;
                }
            }
            else
                report.Events.Add(new DCEvent("DCEventMissionNoTargets", state.PlayerCoalition));

            var departure = state.GetAsset(pending.DepartureId);
            if (departure != null && result.WeaponsUsed.Count > 0)
            {
                foreach (var (weapon, used) in result.WeaponsUsed)
                    DynamicCampaignInventory.Take(departure.Weapons, weapon, used);
                report.Events.Add(new DCEvent("DCEventWeaponsUsed", state.PlayerCoalition, Ref(departure),
                    "%" + string.Join(";", result.WeaponsUsed.Select(x => $"{x.Key}={x.Value}"))));
            }
            if (result.PlayerAircraftLost > 0)
            {
                if (departure != null)
                {
                    departure.Strength = Math.Max(0, departure.Strength - result.PlayerAircraftLost * DynamicCampaignRules.PLAYER_AIRCRAFT_LOSS_READINESS);
                    var lost = result.AircraftLost.Count > 0
                        ? result.AircraftLost
                        : state.PlayerAircraft.Keys.Take(1).ToDictionary(x => x, _ => result.PlayerAircraftLost); // Reports without aircraft types
                    foreach (var (type, count) in lost)
                        DynamicCampaignInventory.Take(departure.Aircraft, type, count);
                }
                report.Events.Add(new DCEvent("DCEventPlayerLosses", state.PlayerCoalition, result.PlayerAircraftLost));
            }
        }

        // ---------------------------------------------------------------- Logistics

        private static void RunLogistics(DynamicCampaignState state, Coalition coalition, DCTurnReport report)
        {
            var assets = state.Assets.Where(x => x.Coalition == coalition).ToList();
            var routes = assets.Where(x => x.IsRoute).ToList();

            // Cut routes come back, damaged ones get new trucks/aircraft
            foreach (var route in routes.Where(x => !x.Removed))
            {
                if (route.CutTurns > 0)
                {
                    route.CutTurns--;
                    if (route.CutTurns == 0)
                    {
                        route.Strength = Math.Max(route.Strength, 40);
                        report.Events.Add(new DCEvent("DCEventRouteRestored", coalition, Ref(route)));
                    }
                    continue;
                }
                route.Strength = Math.Min(100, route.Strength + DynamicCampaignRules.ROUTE_RECOVERY);
            }

            // Weapons and aircraft production
            DynamicCampaignInventory.Produce(state, coalition);

            // Factories > depots, by convoy
            var production = assets.Where(x => x.Kind == DCAssetKind.Factory && x.IsAlive).Sum(x => x.Strength / 100 * DynamicCampaignRules.FACTORY_OUTPUT);
            foreach (var route in routes.Where(x => x.Kind == DCAssetKind.GroundRoute && IsFrom(state, x, DCAssetKind.Factory))
                .OrderBy(x => state.GetAsset(x.ToId)?.Stock ?? 0))
            {
                var depot = state.GetAsset(route.ToId);
                if (depot == null || !depot.IsAlive || !Runs(state, route)) continue;
                var amount = Math.Min(production, Math.Min(Capacity(route, DynamicCampaignRules.GROUND_ROUTE_CAPACITY), DynamicCampaignRules.DEPOT_MAX_STOCK - depot.Stock));
                depot.Stock += Math.Max(0, amount);
                production -= Math.Max(0, amount);
                DynamicCampaignInventory.TransferWeapons(state.GetAsset(route.FromId), depot, 0.5 * route.Strength / 100);
            }

            // Rear airbases get supplies by road from their depot, forward ones only by cargo flights
            var airliftDestinations = routes.Where(x => x.Kind == DCAssetKind.AirRoute && !x.Removed).Select(x => x.ToId).ToHashSet();
            foreach (var airbase in assets.Where(x => x.Kind == DCAssetKind.Airbase && x.IsAlive && !airliftDestinations.Contains(x.Id)))
            {
                var depot = state.GetAsset(airbase.SupplierId);
                airbase.Stock += Take(depot, Math.Min(15, DynamicCampaignRules.AIRBASE_MAX_STOCK - airbase.Stock));
                if (depot != null && depot.IsAlive) DynamicCampaignInventory.TransferWeapons(depot, airbase, 0.3);
            }
            foreach (var route in routes.Where(x => x.Kind == DCAssetKind.AirRoute && Runs(state, x)))
            {
                var origin = state.GetAsset(route.FromId);
                var destination = state.GetAsset(route.ToId);
                if (origin == null || destination == null || !origin.IsAlive || !destination.IsAlive) continue;
                var amount = Math.Min(Capacity(route, DynamicCampaignRules.AIR_ROUTE_CAPACITY), DynamicCampaignRules.AIRBASE_MAX_STOCK - destination.Stock);
                var originDepot = state.GetAsset(origin.SupplierId);
                destination.Stock += Take(originDepot, amount);
                if (originDepot != null && originDepot.IsAlive)
                    DynamicCampaignInventory.TransferWeapons(originDepot, destination, 0.3 * route.Strength / 100);
            }

            // Air readiness depends on airbase supplies
            foreach (var airbase in assets.Where(x => x.Kind == DCAssetKind.Airbase && x.IsAlive))
            {
                if (airbase.Stock >= DynamicCampaignRules.AIRBASE_UPKEEP)
                {
                    airbase.Stock -= DynamicCampaignRules.AIRBASE_UPKEEP;
                    airbase.Strength = Math.Min(100, airbase.Strength + DynamicCampaignRules.AIRBASE_RECOVERY);
                }
                else
                {
                    airbase.Strength = Math.Max(DynamicCampaignRules.DESTROYED_THRESHOLD, airbase.Strength - DynamicCampaignRules.AIRBASE_DECAY * (1 - airbase.Stock / DynamicCampaignRules.AIRBASE_UPKEEP));
                    airbase.Stock = 0;
                    report.Events.Add(new DCEvent("DCEventAirbaseOutOfSupplies", coalition, Ref(airbase)));
                }
            }

            // Front units: upkeep and reinforcements come by convoy from their depot
            foreach (var unit in assets.Where(x => x.Kind == DCAssetKind.FrontUnit && x.IsAlive))
            {
                var route = routes.FirstOrDefault(x => x.Kind == DCAssetKind.GroundRoute && x.ToId == unit.Id && !x.Removed);
                var depot = state.GetAsset(unit.SupplierId);
                var need = DynamicCampaignRules.FRONT_UNIT_UPKEEP + (unit.Strength < 100 ? DynamicCampaignRules.FRONT_UNIT_REINFORCE_COST : 0);
                var received = route != null && Runs(state, route) ? Take(depot, Math.Min(need, Capacity(route, DynamicCampaignRules.GROUND_ROUTE_CAPACITY))) : 0;
                if (received >= DynamicCampaignRules.FRONT_UNIT_UPKEEP)
                    unit.Strength = Math.Min(100, unit.Strength + DynamicCampaignRules.FRONT_UNIT_REINFORCE * (received - DynamicCampaignRules.FRONT_UNIT_UPKEEP) / DynamicCampaignRules.FRONT_UNIT_REINFORCE_COST);
                else
                {
                    unit.Strength -= DynamicCampaignRules.FRONT_UNIT_ATTRITION * (1 - received / DynamicCampaignRules.FRONT_UNIT_UPKEEP);
                    report.Events.Add(new DCEvent("DCEventUnitOutOfSupplies", coalition, Ref(unit)));
                    if (!unit.IsAlive) report.Events.Add(new DCEvent("DCEventAssetDestroyed", coalition, Ref(unit)));
                }
            }

            // Air defenses repaired from their depot
            foreach (var site in assets.Where(x => x.Kind == DCAssetKind.AirDefense && x.IsAlive && x.Strength < 100))
            {
                if (Take(state.GetAsset(site.SupplierId), DynamicCampaignRules.AIR_DEFENSE_REPAIR_COST) >= DynamicCampaignRules.AIR_DEFENSE_REPAIR_COST)
                    site.Strength = Math.Min(100, site.Strength + DynamicCampaignRules.AIR_DEFENSE_REPAIR);
            }

            // Fixed installations slowly repair themselves
            foreach (var structure in assets.Where(x => (x.Kind == DCAssetKind.Depot || x.Kind == DCAssetKind.Factory) && x.IsAlive))
                structure.Strength = Math.Min(100, structure.Strength + DynamicCampaignRules.STRUCTURE_REPAIR);
        }

        // ---------------------------------------------------------------- Commander (both coalitions)

        private static void RunCommander(DynamicCampaignState state, Coalition coalition, Random rng, DCTurnReport report)
        {
            var depots = state.GetAssets(coalition, DCAssetKind.Depot).Where(x => x.IsAlive).ToList();
            if (depots.Count == 0) return;

            // Consumers of a lost depot are re-routed to the nearest standing one
            foreach (var consumer in state.Assets.Where(x => x.Coalition == coalition && x.IsAlive && x.SupplierId != null).ToList())
            {
                var supplier = state.GetAsset(consumer.SupplierId);
                if (supplier != null && supplier.IsAlive) continue;
                var newDepot = depots.MinBy(x => x.Coordinates.GetDistanceFrom(consumer.Coordinates));
                consumer.SupplierId = newDepot.Id;
                if (consumer.Kind == DCAssetKind.FrontUnit)
                {
                    foreach (var oldRoute in state.Assets.Where(x => x.Kind == DCAssetKind.GroundRoute && x.ToId == consumer.Id))
                        oldRoute.Removed = true;
                    AddRoute(state, DCAssetKind.GroundRoute, newDepot, consumer, 50);
                }
                report.Events.Add(new DCEvent("DCEventRerouted", coalition, Ref(consumer), Ref(newDepot)));
            }

            // Depots without any convoy from a factory get a new route if a factory still stands
            var factories = state.GetAssets(coalition, DCAssetKind.Factory).Where(x => x.IsAlive).ToList();
            foreach (var depot in depots)
            {
                var supplied = state.Assets.Any(x => x.Kind == DCAssetKind.GroundRoute && x.ToId == depot.Id && !x.Removed && state.GetAsset(x.FromId)?.IsAlive == true && IsFrom(state, x, DCAssetKind.Factory));
                if (supplied || factories.Count == 0) continue;
                foreach (var oldRoute in state.Assets.Where(x => x.Kind == DCAssetKind.GroundRoute && x.ToId == depot.Id))
                    oldRoute.Removed = true;
                var factory = factories.MinBy(x => x.Coordinates.GetDistanceFrom(depot.Coordinates));
                AddRoute(state, DCAssetKind.GroundRoute, factory, depot, 50);
                report.Events.Add(new DCEvent("DCEventRerouted", coalition, Ref(depot), Ref(factory)));
            }

            // Lost SAM sites are rebuilt when a depot can afford it
            foreach (var site in state.GetAssets(coalition, DCAssetKind.AirDefense).Where(x => !x.IsAlive).ToList())
            {
                var depot = depots.MinBy(x => x.Coordinates.GetDistanceFrom(site.Coordinates));
                if (depot.Stock < DynamicCampaignRules.AIR_DEFENSE_REBUILD_COST || rng.NextDouble() > 0.4) continue;
                depot.Stock -= DynamicCampaignRules.AIR_DEFENSE_REBUILD_COST;
                site.Strength = 50;
                site.SupplierId = depot.Id;
                report.Events.Add(new DCEvent("DCEventAirDefenseRebuilt", coalition, Ref(site)));
            }

            // Fighters are ferried from the best-equipped airbase to one left without any
            var airbases = state.GetAssets(coalition, DCAssetKind.Airbase).Where(x => x.IsAlive).ToList();
            var emptyBase = airbases.FirstOrDefault(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.FIGHTER) < 1);
            var fullBase = airbases.MaxBy(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.FIGHTER));
            if (emptyBase != null && fullBase != null && DynamicCampaignInventory.Get(fullBase.Aircraft, DynamicCampaignInventory.FIGHTER) >= 6)
                DynamicCampaignInventory.Add(emptyBase.Aircraft, DynamicCampaignInventory.FIGHTER, DynamicCampaignInventory.Take(fullBase.Aircraft, DynamicCampaignInventory.FIGHTER, 2));

            // Spare supplies go to the weakest front unit
            var richest = depots.MaxBy(x => x.Stock);
            var weakest = state.GetAssets(coalition, DCAssetKind.FrontUnit).Where(x => x.IsAlive && x.Strength < 60).MinBy(x => x.Strength);
            if (weakest != null && richest.Stock > 60)
            {
                richest.Stock -= 20;
                weakest.Strength = Math.Min(100, weakest.Strength + 15);
                report.Events.Add(new DCEvent("DCEventReinforced", coalition, Ref(weakest)));
            }
        }

        // ---------------------------------------------------------------- AI air operations

        private static readonly string[] AI_STRIKE_WEAPONS = { DynamicCampaignInventory.GUIDED_BOMB, DynamicCampaignInventory.AGM, DynamicCampaignInventory.BOMB, DynamicCampaignInventory.ROCKET };
        private const double AI_SORTIE_WEAPONS = 4;
        private const double AI_SORTIE_AIRCRAFT = 4;

        // Strike packages need attack aircraft and air-to-ground weapons at their airbase;
        // the defender's fighters (with missiles) and SAM cover decide how it goes
        private static void RunAirOperations(DynamicCampaignState state, Coalition coalition, Random rng, DCTurnReport report)
        {
            var enemy = coalition == Coalition.Blue ? Coalition.Red : Coalition.Blue;
            var airbases = state.GetAssets(coalition, DCAssetKind.Airbase).Where(x => x.IsAlive).ToList();
            var strikeBases = airbases.Where(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.ATTACK) >= 2 && AI_STRIKE_WEAPONS.Concat(new[] { DynamicCampaignInventory.ARM }).Sum(w => DynamicCampaignInventory.Get(x.Weapons, w)) >= AI_SORTIE_WEAPONS).ToList();
            if (strikeBases.Count == 0)
            {
                if (airbases.Count > 0) report.Events.Add(new DCEvent("DCEventNoAirOperations", coalition));
                return;
            }
            var sorties = Math.Min(DynamicCampaignRules.MAX_AI_SORTIES,
                (int)Math.Floor(strikeBases.Sum(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.ATTACK) * x.Strength / 100) / AI_SORTIE_AIRCRAFT));
            var ownFighters = Math.Min(1, DynamicCampaignInventory.GetFighterPower(state, coalition) / DynamicCampaignInventory.InitialFighters);

            for (var i = 0; i < sorties; i++)
            {
                var target = PickAITarget(state, enemy, rng);
                if (target == null) return;
                var cover = DynamicCampaignMissionMaker.GetAirDefenseCover(state, enemy, target.Center);

                // Heavily defended target: go after the SAM site first
                if (cover > 1.2 && target.Kind != DCAssetKind.AirDefense)
                {
                    var site = state.GetAssets(enemy, DCAssetKind.AirDefense)
                        .Where(x => x.IsAlive && x.Coordinates.GetDistanceFrom(target.Center) < DynamicCampaignRules.AIR_DEFENSE_COVER_KM * 1000)
                        .MaxBy(x => x.Strength);
                    if (site != null) { target = site; cover = DynamicCampaignMissionMaker.GetAirDefenseCover(state, enemy, site.Center); }
                }

                // Package from the airbase with the most attack aircraft that still has the weapons
                var weapons = target.Kind == DCAssetKind.AirDefense ? new[] { DynamicCampaignInventory.ARM }.Concat(AI_STRIKE_WEAPONS).ToArray() : AI_STRIKE_WEAPONS;
                var airbase = strikeBases
                    .Where(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.ATTACK) >= 2 && weapons.Any(w => DynamicCampaignInventory.Get(x.Weapons, w) >= 2))
                    .MaxBy(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.ATTACK));
                if (airbase == null) break;
                var weapon = weapons.First(w => DynamicCampaignInventory.Get(airbase.Weapons, w) >= 2);
                DynamicCampaignInventory.Take(airbase.Weapons, weapon, AI_SORTIE_WEAPONS);

                // The defender scrambles fighters, spending missiles
                var enemyFighters = Math.Min(1, DynamicCampaignInventory.GetFighterPower(state, enemy) / DynamicCampaignInventory.InitialFighters);
                var intercepted = rng.NextDouble() < enemyFighters;
                if (intercepted)
                {
                    var interceptorBase = state.GetAssets(enemy, DCAssetKind.Airbase).Where(x => x.IsAlive).MaxBy(x => DynamicCampaignInventory.Get(x.Aircraft, DynamicCampaignInventory.FIGHTER));
                    DynamicCampaignInventory.Take(interceptorBase?.Weapons, DynamicCampaignInventory.AAM, 2);
                }

                var successChance = Math.Clamp(0.65 - 0.22 * cover - (intercepted ? 0.3 : 0) + 0.15 * ownFighters, 0.1, 0.85);
                if (rng.NextDouble() < successChance)
                {
                    var damage = rng.Next(20, 46);
                    report.Events.Add(new DCEvent("DCEventAIStrikeSuccess", coalition, Ref(target), damage));
                    if (target.IsRoute) CutRoute(target, report);
                    if (target.Kind == DCAssetKind.Airbase) DestroyParkedAircraft(target, rng.Next(1, 4), report);
                    Damage(state, target, damage, report);
                }
                else
                    report.Events.Add(new DCEvent("DCEventAIStrikeFailed", coalition, Ref(target)));

                var lossChance = Math.Clamp(0.08 + 0.18 * cover + (intercepted ? 0.25 : 0), 0, 0.6);
                if (rng.NextDouble() < lossChance)
                {
                    DynamicCampaignInventory.Take(airbase.Aircraft, DynamicCampaignInventory.ATTACK, 1);
                    report.Events.Add(new DCEvent("DCEventAIAircraftLost", coalition, Ref(airbase)));
                }
            }
        }

        private static void DestroyParkedAircraft(DCAsset airbase, int count, DCTurnReport report)
        {
            var destroyed = 0.0;
            foreach (var fleet in new[] { DynamicCampaignInventory.ATTACK, DynamicCampaignInventory.FIGHTER })
                destroyed += DynamicCampaignInventory.Take(airbase.Aircraft, fleet, count - destroyed);
            if (destroyed >= 1)
                report.Events.Add(new DCEvent("DCEventAircraftDestroyedOnGround", airbase.Coalition, Ref(airbase), Math.Floor(destroyed)));
        }

        private static DCAsset PickAITarget(DynamicCampaignState state, Coalition targetCoalition, Random rng)
        {
            // Interdiction first: supply routes and depots, then the front and the defenses
            var candidates = state.Assets
                .Where(x => x.Coalition == targetCoalition && x.IsOperational)
                .Where(x => x.Kind != DCAssetKind.GroundRoute || DynamicCampaignMissionMaker.IsGroundRouteActive(state, x))
                .Select(x => (asset: x, weight: x.Kind switch
                {
                    DCAssetKind.GroundRoute => 3,
                    DCAssetKind.AirRoute => 2,
                    DCAssetKind.Depot => 3,
                    DCAssetKind.FrontUnit => 3,
                    DCAssetKind.AirDefense => 1,
                    DCAssetKind.Factory => 1,
                    DCAssetKind.Airbase => 1,
                    _ => 0
                }))
                .Where(x => x.weight > 0)
                .ToList();
            if (candidates.Count == 0) return null;
            var roll = rng.Next(candidates.Sum(x => x.weight));
            foreach (var (asset, weight) in candidates)
            {
                if (roll < weight) return asset;
                roll -= weight;
            }
            return candidates.Last().asset;
        }

        // ---------------------------------------------------------------- Ground war

        private static void RunGroundWar(DynamicCampaignState state, Random rng, DCTurnReport report)
        {
            var blue = state.GetAssets(Coalition.Blue, DCAssetKind.FrontUnit).Where(x => x.IsAlive).ToList();
            var red = state.GetAssets(Coalition.Red, DCAssetKind.FrontUnit).Where(x => x.IsAlive).ToList();

            // Each formation fights the nearest enemy formation
            foreach (var unit in blue.Concat(red).OrderBy(_ => rng.Next()).ToList())
            {
                var opponents = unit.Coalition == Coalition.Blue ? red : blue;
                var opponent = opponents.Where(x => x.IsAlive).MinBy(x => x.Coordinates.GetDistanceFrom(unit.Coordinates));
                if (opponent == null || !unit.IsAlive) continue;
                var share = opponent.Strength / (unit.Strength + opponent.Strength);
                unit.Strength -= DynamicCampaignRules.FRONT_COMBAT_LOSS * share * (0.5 + rng.NextDouble());
                if (!unit.IsAlive) report.Events.Add(new DCEvent("DCEventAssetDestroyed", unit.Coalition, Ref(unit)));
            }

            var blueStrength = blue.Where(x => x.IsAlive).Sum(x => x.Strength);
            var redStrength = red.Where(x => x.IsAlive).Sum(x => x.Strength);
            var ratio = (blueStrength + 1) / (redStrength + 1);
            var shiftKm = Math.Clamp((ratio >= 1 ? ratio - 1 : 1 - 1 / ratio) * DynamicCampaignRules.FRONT_SHIFT_PER_RATIO_KM,
                -DynamicCampaignRules.FRONT_MAX_SHIFT_KM, DynamicCampaignRules.FRONT_MAX_SHIFT_KM);
            if (Math.Abs(shiftKm) < 0.5)
            {
                report.Events.Add(new DCEvent("DCEventFrontStable", null));
                return;
            }

            // Push the front line (and the formations on it) toward the weaker side
            var blueCenter = Centroid(state, Coalition.Blue);
            var redCenter = Centroid(state, Coalition.Red);
            if (blueCenter.GetDistanceFrom(redCenter) < 1) return;
            var direction = (redCenter - blueCenter).Normalize();
            var offset = direction * (shiftKm * 1000);
            var blueSide = ShapeManager.GetSideOfLine(blueCenter, FrontLine(state));
            state.FrontLine = state.FrontLine.Select(x => new[] { x[0] + offset.X, x[1] + offset.Y }).ToList();
            foreach (var unit in state.Assets.Where(x => x.Kind == DCAssetKind.FrontUnit))
            {
                unit.X += offset.X;
                unit.Y += offset.Y;
            }
            foreach (var route in state.Assets.Where(x => x.Kind == DCAssetKind.GroundRoute))
            {
                var destination = state.GetAsset(route.ToId);
                if (destination?.Kind != DCAssetKind.FrontUnit) continue;
                route.X2 = destination.X;
                route.Y2 = destination.Y;
            }
            var winner = shiftKm > 0 ? Coalition.Blue : Coalition.Red;
            report.Events.Add(new DCEvent("DCEventFrontMoved", winner, Math.Round(Math.Abs(shiftKm), 1)));

            // Anything left on the wrong side of the line is overrun or captured
            var line = FrontLine(state);
            foreach (var asset in state.Assets.Where(x => !x.IsRoute && x.Kind != DCAssetKind.FrontUnit && x.IsAlive).ToList())
            {
                var onBlueSide = ShapeManager.GetSideOfLine(asset.Coordinates, line) == blueSide;
                var owner = onBlueSide ? Coalition.Blue : Coalition.Red;
                if (owner == asset.Coalition || DynamicCampaignBuilder.DistanceToLine(asset.Coordinates, line) > 30000) continue;
                if (asset.Kind == DCAssetKind.Airbase)
                {
                    DynamicCampaignInventory.Clear(asset);
                    asset.Coalition = owner;
                    asset.Strength = 20;
                    asset.Stock = 0;
                    asset.SupplierId = state.GetAssets(owner, DCAssetKind.Depot).Where(x => x.IsAlive).MinBy(x => x.Coordinates.GetDistanceFrom(asset.Coordinates))?.Id;
                    foreach (var route in state.Assets.Where(x => x.Kind == DCAssetKind.AirRoute && (x.FromId == asset.Id || x.ToId == asset.Id)))
                        route.Removed = true;
                    report.Events.Add(new DCEvent("DCEventAirbaseCaptured", owner, Ref(asset)));
                }
                else
                {
                    asset.Strength = 0;
                    DynamicCampaignInventory.Clear(asset);
                    report.Events.Add(new DCEvent("DCEventAssetOverrun", owner, Ref(asset)));
                }
            }
        }

        // ---------------------------------------------------------------- Outcome

        private static void CheckOutcome(DynamicCampaignState state, DCTurnReport report)
        {
            if (IsBeaten(state, state.EnemyCoalition))
            {
                state.Outcome = DCOutcome.Victory;
                report.Events.Add(new DCEvent("DCEventVictory", state.PlayerCoalition));
            }
            else if (IsBeaten(state, state.PlayerCoalition))
            {
                state.Outcome = DCOutcome.Defeat;
                report.Events.Add(new DCEvent("DCEventDefeat", state.EnemyCoalition));
            }
        }

        // No more war industry, or no more army on the front
        private static bool IsBeaten(DynamicCampaignState state, Coalition coalition) =>
            !state.Assets.Any(x => x.Coalition == coalition && (x.Kind == DCAssetKind.Factory || x.Kind == DCAssetKind.Depot) && x.IsAlive)
            || !state.GetAssets(coalition, DCAssetKind.FrontUnit).Any(x => x.IsAlive);

        // ---------------------------------------------------------------- Helpers

        private static string Ref(DCAsset asset) => $"@{asset.Id}";

        private static string Percent(double fraction) => Math.Round(fraction * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static void Damage(DynamicCampaignState state, DCAsset asset, double damage, DCTurnReport report)
        {
            var wasAlive = asset.IsAlive;
            asset.Strength = Math.Max(0, asset.Strength - damage);
            if (wasAlive && !asset.IsAlive && !asset.IsRoute) // Routes are never destroyed, only cut and rebuilt
            {
                asset.Strength = 0;
                if (asset.Kind == DCAssetKind.Depot) asset.Stock = 0;
                if (asset.Kind == DCAssetKind.Depot || asset.Kind == DCAssetKind.Factory) asset.Weapons?.Clear();
                report.Events.Add(new DCEvent("DCEventAssetDestroyed", asset.Coalition, Ref(asset)));
            }
        }

        private static void CutRoute(DCAsset route, DCTurnReport report)
        {
            if (route.CutTurns > 0) return;
            route.CutTurns = DynamicCampaignRules.ROUTE_CUT_TURNS;
            report.Events.Add(new DCEvent("DCEventRouteCut", route.Coalition, Ref(route)));
        }

        private static double Capacity(DCAsset route, double capacity) => capacity * Math.Max(route.Strength, 0) / 100;

        private static bool Runs(DynamicCampaignState state, DCAsset route) =>
            !route.Removed && route.CutTurns == 0 && route.Strength > 0 && (route.Kind != DCAssetKind.GroundRoute || DynamicCampaignMissionMaker.IsGroundRouteActive(state, route));

        private static bool IsFrom(DynamicCampaignState state, DCAsset route, DCAssetKind kind) => state.GetAsset(route.FromId)?.Kind == kind;

        private static double Take(DCAsset depot, double amount)
        {
            if (depot == null || !depot.IsAlive || amount <= 0) return 0;
            var taken = Math.Min(depot.Stock, amount);
            depot.Stock -= taken;
            return taken;
        }

        private static void AddRoute(DynamicCampaignState state, DCAssetKind kind, DCAsset from, DCAsset to, double strength)
        {
            var code = kind == DCAssetKind.AirRoute ? "AIR" : "MSR";
            var index = state.Assets.Count(x => x.Coalition == from.Coalition && x.Kind == kind) + 1;
            state.Assets.Add(new DCAsset
            {
                Id = $"{(from.Coalition == Coalition.Blue ? "B" : "R")}{state.Assets.Count + 1}",
                Coalition = from.Coalition,
                Kind = kind,
                Name = $"{code}-{index}",
                Near = $"{from.Name} > {to.Name}",
                X = from.X,
                Y = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                FromId = from.Id,
                ToId = to.Id,
                Strength = strength,
            });
        }

        private static Coordinates Centroid(DynamicCampaignState state, Coalition coalition)
        {
            var points = state.Assets.Where(x => x.Coalition == coalition && !x.IsRoute && x.Kind != DCAssetKind.FrontUnit).Select(x => x.Coordinates).ToList();
            return points.Count == 0 ? new Coordinates() : Coordinates.Sum(points) / points.Count;
        }

        private static List<Coordinates> FrontLine(DynamicCampaignState state) => state.FrontLine.Select(x => new Coordinates(x[0], x[1])).ToList();
    }
}
