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
    // Pilot careers: the debriefs of every dynamic campaign, summed by player name
    // (AI wingmen are only in the mission debriefs).
    public static class DynamicCampaignPilots
    {
        public static List<DCPilotRecord> GetPilots(IEnumerable<DynamicCampaignState> campaigns)
        {
            var pilots = new Dictionary<string, DCPilotRecord>();
            foreach (var campaign in campaigns)
                foreach (var report in campaign.Reports.Where(x => x.Debrief != null))
                    foreach (var debrief in report.Debrief.Pilots.Where(x => !x.Ai && !string.IsNullOrEmpty(x.Name)))
                    {
                        if (!pilots.TryGetValue(debrief.Name, out var pilot))
                            pilots[debrief.Name] = pilot = new DCPilotRecord { Name = debrief.Name };
                        pilot.Add(campaign, report, debrief);
                    }
            foreach (var pilot in pilots.Values)
                pilot.History = pilot.History.OrderByDescending(x => x.Date).ThenByDescending(x => x.Turn).ToList();
            return pilots.Values.OrderByDescending(x => x.Missions).ThenBy(x => x.Name).ToList();
        }

        public static DCPilotRecord GetPilot(IEnumerable<DynamicCampaignState> campaigns, string name) =>
            GetPilots(campaigns).FirstOrDefault(x => x.Name == name);
    }

    public class DCPilotRecord
    {
        public string Name { get; set; }
        public int Missions { get; set; }
        public double FlightTime { get; set; }
        public int Takeoffs { get; set; }
        public int Landings { get; set; }
        public int Shots { get; set; }
        public int Hits { get; set; }
        public DCKillCount Kills { get; set; } = new();
        public int FriendlyKills { get; set; }
        public int HitsTaken { get; set; }
        public int Ejections { get; set; }
        public int Deaths { get; set; }
        public int AircraftLost { get; set; }
        public Dictionary<string, int> Aircraft { get; set; } = new(); // missions by aircraft type
        public List<DCWeaponDebrief> Weapons { get; set; } = new();
        public List<DCPilotMission> History { get; set; } = new();

        public double Accuracy => Shots == 0 ? 0 : 100.0 * Hits / Shots;

        internal void Add(DynamicCampaignState campaign, DCTurnReport report, DCPilotDebrief debrief)
        {
            Missions++;
            FlightTime += debrief.FlightTime;
            Takeoffs += debrief.Takeoffs;
            Landings += debrief.Landings;
            Shots += debrief.Shots();
            Hits += debrief.Hits();
            Kills.Add(debrief.Kills);
            FriendlyKills += debrief.FriendlyKills;
            HitsTaken += debrief.HitsTaken;
            if (debrief.Status == "Ejected") Ejections++;
            if (debrief.Status == "Killed") Deaths++;
            if (debrief.AircraftLost()) AircraftLost++;
            if (!string.IsNullOrEmpty(debrief.Aircraft))
                Aircraft[debrief.Aircraft] = (Aircraft.TryGetValue(debrief.Aircraft, out var count) ? count : 0) + 1;
            foreach (var weapon in debrief.Weapons)
            {
                var total = Weapons.FirstOrDefault(x => x.Name == weapon.Name);
                if (total == null)
                    Weapons.Add(total = new DCWeaponDebrief { Name = weapon.Name, Category = weapon.Category, Gun = weapon.Gun });
                total.Shots += weapon.Shots;
                total.Hits += weapon.Hits;
                total.Kills += weapon.Kills;
            }
            Weapons = Weapons.OrderByDescending(x => x.Kills).ThenByDescending(x => x.Shots).ToList();
            History.Add(new DCPilotMission
            {
                CampaignId = campaign.Id,
                CampaignName = campaign.Name,
                Turn = report.Turn,
                Date = report.Date,
                MissionName = report.Debrief.MissionName,
                Kind = report.Debrief.Kind,
                TargetId = report.Debrief.TargetId,
                ObjectivePercent = report.Debrief.ObjectivePercent,
                Aircraft = debrief.Aircraft,
                Status = debrief.Status,
                Kills = debrief.Kills?.Total() ?? 0,
                FlightTime = debrief.FlightTime,
            });
        }
    }

    public class DCPilotMission
    {
        public string CampaignId { get; set; }
        public string CampaignName { get; set; }
        public int Turn { get; set; }
        public DateTime Date { get; set; }
        public string MissionName { get; set; }
        public DCSortieKind Kind { get; set; }
        public string TargetId { get; set; }
        public double ObjectivePercent { get; set; }
        public string Aircraft { get; set; }
        public string Status { get; set; }
        public int Kills { get; set; }
        public double FlightTime { get; set; }
    }
}
