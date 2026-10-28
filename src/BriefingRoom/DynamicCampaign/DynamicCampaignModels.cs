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

        public bool IsRoute => Kind == DCAssetKind.GroundRoute || Kind == DCAssetKind.AirRoute;
        public bool IsAlive => Strength >= DynamicCampaignRules.DESTROYED_THRESHOLD;
        public bool IsOperational => IsAlive && CutTurns == 0 && !Removed;

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
    }

    public class DCObjectiveResult
    {
        public int Dead { get; set; }
        public int Total { get; set; }
        public bool Complete { get; set; }
    }

    public class DynamicCampaignState
    {
        public const int FORMAT_VERSION = 1;

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
