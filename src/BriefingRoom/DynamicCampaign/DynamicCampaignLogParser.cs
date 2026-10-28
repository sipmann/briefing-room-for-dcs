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

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Reads the lines written by Include/Lua/DynamicCampaign/Reporter.lua from a
    // dcs.log (Saved Games\DCS\Logs\dcs.log) and keeps the latest report of a mission.
    public static class DynamicCampaignLogParser
    {
        private static readonly Regex LINE_REGEX = new(
            @"BRDC\|(?<token>[0-9a-f]{12})\|(?<seq>\d+)\|(?<time>\d+)\|(?<complete>[01])\|(?<lost>\d+)\|(?<objectives>[0-9/,]*)",
            RegexOptions.Compiled);

        public static DCMissionResult Parse(TextReader reader, string token)
        {
            DCMissionResult latest = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
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
                        .Where(x => x.Length == 3)
                        .Select(x => new DCObjectiveResult
                        {
                            Dead = int.Parse(x[0], CultureInfo.InvariantCulture),
                            Total = int.Parse(x[1], CultureInfo.InvariantCulture),
                            Complete = x[2] == "1",
                        })
                        .ToList()
                };
            }
            return latest;
        }

        public static DCMissionResult Parse(string text, string token) => Parse(new StringReader(text), token);

        // A dcs.log may belong to another mission: tell the user which campaign missions it has
        public static bool ContainsAnyReport(string text) => text.Contains("BRDC|");
    }
}
