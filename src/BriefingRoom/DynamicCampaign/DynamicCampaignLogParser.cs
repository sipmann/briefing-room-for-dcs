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

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Reads the lines written by Include/Lua/DynamicCampaign/Reporter.lua from a
    // dcs.log (Saved Games\DCS\Logs\dcs.log) and keeps the latest report of a mission.
    public static class DynamicCampaignLogParser
    {
        private static readonly Regex LINE_REGEX = new(
            @"BRDC\|(?<token>[0-9a-f]{12})\|(?<seq>\d+)\|(?<time>\d+)\|(?<complete>[01])\|(?<lost>\d+)\|(?<objectives>[0-9/,]*)(\|W:(?<weapons>[A-Za-z0-9=;.]*))?(\|L:(?<types>[^|\s]*))?",
            RegexOptions.Compiled);
        private static readonly Regex DEBRIEF_REGEX = new(
            @"BRDCS\|(?<token>[0-9a-f]{12})\|(?<seq>\d+)\|(?<json>\{.*\})\s*$",
            RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JSON_OPTIONS = new() { PropertyNameCaseInsensitive = true };

        // Pilots, events and SAM units destroyed, written as JSON after each report line
        private sealed class DebriefLine
        {
            public List<DCPilotDebrief> Pilots { get; set; } = new();
            public List<DCDebriefEvent> Events { get; set; } = new();
            public List<DCAirDefenseKill> AirDefenseKills { get; set; } = new();
        }

        public static DCMissionResult Parse(TextReader reader, string token)
        {
            DCMissionResult latest = null;
            DebriefLine debrief = null;
            var debriefSequence = -1;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("BRDCS|"))
                {
                    var debriefMatch = DEBRIEF_REGEX.Match(line);
                    if (!debriefMatch.Success || debriefMatch.Groups["token"].Value != token) continue;
                    var debriefSeq = int.Parse(debriefMatch.Groups["seq"].Value, CultureInfo.InvariantCulture);
                    if (debriefSeq < debriefSequence) continue;
                    try
                    {
                        debrief = JsonSerializer.Deserialize<DebriefLine>(debriefMatch.Groups["json"].Value, JSON_OPTIONS);
                        debriefSequence = debriefSeq;
                    }
                    catch (JsonException ex)
                    {
                        BriefingRoom.PrintToLog($"Skipping unreadable dynamic campaign debrief: {ex.Message}", LogMessageErrorLevel.Warning);
                    }
                    continue;
                }
                if (!line.Contains("BRDC|")) continue;
                var match = LINE_REGEX.Match(line);
                if (!match.Success || match.Groups["token"].Value != token) continue;

                var sequence = int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture);
                if (latest != null && sequence < latest.Sequence) continue;

                latest = new DCMissionResult
                {
                    Token = token,
                    Sequence = sequence,
                    MissionTime = double.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture),
                    MissionComplete = match.Groups["complete"].Value == "1",
                    PlayerAircraftLost = int.Parse(match.Groups["lost"].Value, CultureInfo.InvariantCulture),
                    Objectives = match.Groups["objectives"].Value
                        .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split('/'))
                        .Where(x => x.Length == 3 || x.Length == 5)
                        .Select(x => new DCObjectiveResult
                        {
                            Dead = int.Parse(x[0], CultureInfo.InvariantCulture),
                            Total = int.Parse(x[1], CultureInfo.InvariantCulture),
                            Complete = x[2] == "1",
                            SiteDead = int.Parse(x[x.Length == 5 ? 3 : 0], CultureInfo.InvariantCulture),
                            SiteTotal = int.Parse(x[x.Length == 5 ? 4 : 1], CultureInfo.InvariantCulture),
                        })
                        .ToList(),
                    WeaponsUsed = ParsePairs(match.Groups["weapons"].Value)
                        .Where(x => DynamicCampaignInventory.WEAPONS.Contains(x.Key))
                        .ToDictionary(x => x.Key, x => x.Value),
                    AircraftLost = ParsePairs(match.Groups["types"].Value).ToDictionary(x => x.Key, x => (int)x.Value),
                };
            }
            if (latest != null && debrief != null)
            {
                latest.Pilots = debrief.Pilots ?? new();
                latest.Events = debrief.Events ?? new();
                latest.AirDefenseKills = debrief.AirDefenseKills ?? new();
            }
            return latest;
        }

        private static Dictionary<string, double> ParsePairs(string text)
        {
            var pairs = new Dictionary<string, double>();
            foreach (var pair in text.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=');
                if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    pairs[parts[0]] = (pairs.TryGetValue(parts[0], out var existing) ? existing : 0) + value;
            }
            return pairs;
        }

        public static DCMissionResult Parse(string text, string token) => Parse(new StringReader(text), token);

        // A dcs.log may belong to another mission: tell the user which campaign missions it has
        public static bool ContainsAnyReport(string text) => text.Contains("BRDC|");
    }
}
