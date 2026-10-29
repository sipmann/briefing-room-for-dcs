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
    // A turn-based dynamic campaign: a persistent theater state (assets of both
    // coalitions and the supply network linking them) that every generated
    // mission reads from and every mission result (parsed from dcs.log) writes to.
    // Serialized as JSON by DynamicCampaignStore.

    public enum DCAssetKind
    {
        Factory,     // Produces supplies, deep in the rear
        Depot,       // Stores supplies, feeds front units and air defenses
        FrontUnit,   // Ground formation on the front line, consumes supplies
        AirDefense,  // SAM site, protects nearby assets
        Airbase,     // Air power: readiness = strength, fed by cargo flights
        GroundRoute, // Truck convoys between two assets (factory > depot > front)
        AirRoute,    // Cargo flights between a rear and a forward airbase
    }

    public enum DCSortieKind
    {
        StrikeFactory,
        StrikeDepot,
        CloseAirSupport,
        SEAD,
        InterdictConvoy,
        InterceptCargo,
        AirbaseStrike,
    }

    public enum DCOutcome
    {
        Ongoing,
        Victory,
        Defeat,
    }

    public class DCAsset
    {
        public string Id { get; set; }
        public Coalition Coalition { get; set; }
        public DCAssetKind Kind { get; set; }
        public string Name { get; set; }
        public string Near { get; set; } // Nearest airbase name, for display
        public double X { get; set; }
        public double Y { get; set; }

        // Routes: from/to asset ids and end coordinates (X/Y is the start)
        public string FromId { get; set; }
        public string ToId { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }

        // Airbases: database id of the airbase
        public string AirbaseId { get; set; }

        // 0 (destroyed) to 100 (full strength). Airbases: air readiness.
        public double Strength { get; set; } = 100;

        // Supplies held (depots, airbases)
        public double Stock { get; set; }

        // Routes: turns left before convoys/cargo flights run again
        public int CutTurns { get; set; }

        // Depot feeding this asset (front units, air defenses, airbases)
        public string SupplierId { get; set; }

        // Routes replaced by another one (re-routing, captured airbase)
        public bool Removed { get; set; }

        // Weapons by category and aircraft by type/role held here (factories, depots, airbases)
        public Dictionary<string, double> Weapons { get; set; }
        public Dictionary<string, double> Aircraft { get; set; }

        // SAM sites: turns before destroyed radars (search or tracking) are replaced.
        // Meanwhile the site covers nothing and is spawned without radars.
        public int RadarRepairTurns { get; set; }

        public bool IsRoute => Kind == DCAssetKind.GroundRoute || Kind == DCAssetKind.AirRoute;
        public bool IsAlive => Strength >= DynamicCampaignRules.DESTROYED_THRESHOLD;
        public bool IsOperational => IsAlive && CutTurns == 0 && !Removed;
        public bool RadarsDown => RadarRepairTurns > 0;

        internal Coordinates Coordinates => new(X, Y);
        internal Coordinates Coordinates2 => new(X2, Y2);
        internal Coordinates Center => IsRoute ? Coordinates.Lerp(Coordinates, Coordinates2, 0.5) : Coordinates;
    }

    public class DCSortie
    {
        public DCSortieKind Kind { get; set; }
        public string TargetId { get; set; }
        public string PresetId { get; set; }
        public bool Suitable { get; set; } = true;
        public string DepartureId { get; set; }
        // Translation key of why it cannot be flown (no aircraft or weapons at the departure airbase), null if it can
        public string Unavailable { get; set; }
        // Enemy air defenses the mission will have around the target
        public AmountNR Threat { get; set; }
    }

    public class DCPendingMission
    {
        public string Token { get; set; }
        public int Turn { get; set; }
        public DCSortieKind Kind { get; set; }
        public string TargetId { get; set; }
        public string DepartureId { get; set; }
        public string MissionName { get; set; }
        public DateTime GeneratedUtc { get; set; }
        // SAM site target spawned without its radars: the objective is the rest of the site
        public bool RadarsDown { get; set; }
    }

    // A narrative event, translated when displayed (key from Database/Language/DynamicCampaign)
    public class DCEvent
    {
        public string Key { get; set; }
        public List<string> Args { get; set; } = new();
        public Coalition? Coalition { get; set; }

        public DCEvent() { }
        public DCEvent(string key, Coalition? coalition, params object[] args)
        {
            Key = key;
            Coalition = coalition;
            Args = args.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        }
    }

    public class DCTurnReport
    {
        public int Turn { get; set; }
        public DateTime Date { get; set; }
        public List<DCEvent> Events { get; set; } = new();
        // What the pilots did in the player mission of this turn (null if not flown or no debrief in the log)
        public DCDebrief Debrief { get; set; }
    }

    // What came back from DCS for one mission
    public class DCMissionResult
    {
        public string Token { get; set; }
        public int Sequence { get; set; }
        public double MissionTime { get; set; }
        public bool MissionComplete { get; set; }
        public int PlayerAircraftLost { get; set; }
        public List<DCObjectiveResult> Objectives { get; set; } = new();
        public Dictionary<string, double> WeaponsUsed { get; set; } = new();
        public Dictionary<string, int> AircraftLost { get; set; } = new();
        public List<DCPilotDebrief> Pilots { get; set; } = new();
        public List<DCDebriefEvent> Events { get; set; } = new();
        // Enemy medium/long range SAM units destroyed outside the objective
        public List<DCAirDefenseKill> AirDefenseKills { get; set; } = new();
    }

    public class DCObjectiveResult
    {
        public int Dead { get; set; }
        public int Total { get; set; }
        public bool Complete { get; set; }
        // Every unit of the target groups (SAM site: launchers and radars), same as Dead/Total in older reports
        public int SiteDead { get; set; }
        public int SiteTotal { get; set; }
    }

    public class DCAirDefenseKill
    {
        public double X { get; set; }
        public double Y { get; set; }
        public bool Radar { get; set; }
    }

    // ---------------------------------------------------------------- Debrief

    public class DCDebrief
    {
        public string MissionName { get; set; }
        public DCSortieKind Kind { get; set; }
        public string TargetId { get; set; }
        public double ObjectivePercent { get; set; }
        public double MissionTime { get; set; }
        public List<DCPilotDebrief> Pilots { get; set; } = new();
        public List<DCDebriefEvent> Events { get; set; } = new();
    }

    public class DCPilotDebrief
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public bool Ai { get; set; }
        public string Aircraft { get; set; }
        public int Takeoffs { get; set; }
        public int Landings { get; set; }
        public double FlightTime { get; set; } // seconds
        public string Status { get; set; } // OnGround, Airborne, Landed, Ejected, Killed, Lost
        public int HitsTaken { get; set; }
        public int FriendlyKills { get; set; }
        public string ShotDownBy { get; set; }
        public DCKillCount Kills { get; set; } = new();
        public List<DCWeaponDebrief> Weapons { get; set; } = new();

        // Accuracy is counted on missiles, bombs and rockets: every shell is a hit, bursts are not shots
        public int Shots() => Weapons.Where(x => !x.Gun).Sum(x => x.Shots);
        public int Hits() => Weapons.Where(x => !x.Gun).Sum(x => Math.Min(x.Hits, x.Shots));
        public bool AircraftLost() => Status == "Ejected" || Status == "Killed" || Status == "Lost";
    }

    public class DCKillCount
    {
        public int Air { get; set; }
        public int Helicopter { get; set; }
        public int Ground { get; set; }
        public int AirDefense { get; set; }
        public int Ship { get; set; }
        public int Structure { get; set; }

        public int Total() => Air + Helicopter + Ground + AirDefense + Ship + Structure;

        public void Add(DCKillCount other)
        {
            if (other == null) return;
            Air += other.Air;
            Helicopter += other.Helicopter;
            Ground += other.Ground;
            AirDefense += other.AirDefense;
            Ship += other.Ship;
            Structure += other.Structure;
        }
    }

    public class DCWeaponDebrief
    {
        public string Name { get; set; }
        public string Category { get; set; } // DynamicCampaignInventory.WEAPONS, "Gun" or empty
        public bool Gun { get; set; }
        public int Shots { get; set; }
        public int Hits { get; set; }
        public int Kills { get; set; }
    }

    public class DCDebriefEvent
    {
        public double Time { get; set; } // DCS absolute time, seconds since midnight
        public string Pilot { get; set; } // DCPilotDebrief.Key
        public string Kind { get; set; } // Takeoff, Landing, Shot, Kill, FriendlyKill, ShotDown, Ejected, Killed, Lost
        public string Weapon { get; set; }
        public string Target { get; set; }
    }

    public class DynamicCampaignState
    {
        public const int FORMAT_VERSION = 2;

        public int FormatVersion { get; set; } = FORMAT_VERSION;
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int Turn { get; set; } = 1;
        public DateTime Date { get; set; }
        public int Seed { get; set; }
        public string Theater { get; set; }
        public string Situation { get; set; }
        public bool InvertCoalitions { get; set; }
        public Coalition PlayerCoalition { get; set; }

        // The campaign template (.cbrt INI) the player set up: coalitions,
        // aircraft, options... reused for every generated mission.
        public string TemplateIni { get; set; }

        // Player aircraft types (from the template) and how many a mission needs of each
        public Dictionary<string, int> PlayerAircraft { get; set; } = new();

        public List<double[]> FrontLine { get; set; } = new();
        public List<DCAsset> Assets { get; set; } = new();
        public DCPendingMission Pending { get; set; }
        public List<DCTurnReport> Reports { get; set; } = new();
        public DCOutcome Outcome { get; set; } = DCOutcome.Ongoing;

        public DCAsset GetAsset(string id) => Assets.FirstOrDefault(x => x.Id == id);
        public IEnumerable<DCAsset> GetAssets(Coalition coalition, DCAssetKind kind) => Assets.Where(x => x.Coalition == coalition && x.Kind == kind);
        public Coalition EnemyCoalition => PlayerCoalition == Coalition.Blue ? Coalition.Red : Coalition.Blue;
    }
}
