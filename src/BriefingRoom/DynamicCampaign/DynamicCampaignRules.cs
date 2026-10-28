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

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Tuning values of the dynamic campaign simulation, in one place.
    // Strength and supplies are on a 0-100 scale.
    internal static class DynamicCampaignRules
    {
        internal const double DESTROYED_THRESHOLD = 10;

        // Initial layout (per coalition)
        internal const int FACTORIES = 2;
        internal const int DEPOTS = 3;
        internal const int FRONT_UNITS = 5;
        internal const int AIR_DEFENSES = 4;
        internal const int AIRBASES = 4;
        internal const int AIR_ROUTES = 2;
        internal const double MIN_ASSET_SEPARATION_KM = 8;

        // Production and logistics
        internal const double FACTORY_OUTPUT = 30;          // per factory at full strength
        internal const double GROUND_ROUTE_CAPACITY = 25;   // per route at full strength
        internal const double AIR_ROUTE_CAPACITY = 20;
        internal const double DEPOT_MAX_STOCK = 100;
        internal const double AIRBASE_MAX_STOCK = 60;
        internal const double FRONT_UNIT_UPKEEP = 10;
        internal const double FRONT_UNIT_REINFORCE_COST = 8;
        internal const double FRONT_UNIT_REINFORCE = 8;
        internal const double FRONT_UNIT_ATTRITION = 7;     // per turn without any supplies
        internal const double AIR_DEFENSE_REPAIR_COST = 6;
        internal const double AIR_DEFENSE_REPAIR = 10;
        internal const double AIR_DEFENSE_REBUILD_COST = 35;
        internal const double AIRBASE_UPKEEP = 10;
        internal const double AIRBASE_RECOVERY = 10;
        internal const double AIRBASE_DECAY = 12;
        internal const double STRUCTURE_REPAIR = 5;         // depots and factories, per turn
        internal const double ROUTE_RECOVERY = 25;
        internal const int ROUTE_CUT_TURNS = 2;

        // Player losses
        internal const double PLAYER_AIRCRAFT_LOSS_READINESS = 15;

        // AI air operations
        internal const int MAX_AI_SORTIES = 4;
        internal const double AI_SORTIES_PER_FULL_AIRBASE = 0.6;
        internal const double AIR_DEFENSE_COVER_KM = 45;

        // Ground war
        internal const double FRONT_MAX_SHIFT_KM = 6;
        internal const double FRONT_SHIFT_PER_RATIO_KM = 10;
        internal const double FRONT_COMBAT_LOSS = 6;

        // Days between turns
        internal const int MIN_DAYS_PER_TURN = 1;
        internal const int MAX_DAYS_PER_TURN = 2;
    }
}
