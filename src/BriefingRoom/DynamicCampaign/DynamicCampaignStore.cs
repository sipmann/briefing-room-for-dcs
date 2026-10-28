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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // Saves dynamic campaigns as one JSON file each. The directory comes from
    // the BR_CAMPAIGNS_DIR environment variable (Docker volume), defaulting to
    // "DynamicCampaigns" next to the BriefingRoom data folders.
    public class DynamicCampaignStore
    {
        private static readonly Regex ID_REGEX = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JSON_OPTIONS = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        private static readonly object FILE_LOCK = new();

        public string Directory { get; }

        public DynamicCampaignStore(string directory = null)
        {
            Directory = directory ?? DefaultDirectory();
        }

        public static string DefaultDirectory()
        {
            var fromEnv = Environment.GetEnvironmentVariable("BR_CAMPAIGNS_DIR");
            return string.IsNullOrWhiteSpace(fromEnv) ? Path.Combine(BRPaths.ROOT, "DynamicCampaigns") : fromEnv;
        }

        public List<DynamicCampaignState> List()
        {
            if (!System.IO.Directory.Exists(Directory)) return new();
            var campaigns = new List<DynamicCampaignState>();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    campaigns.Add(Deserialize(File.ReadAllText(file)));
                }
                catch (Exception ex)
                {
                    BriefingRoom.PrintToLog($"Skipping unreadable dynamic campaign {file}: {ex.Message}", LogMessageErrorLevel.Warning);
                }
            }
            return campaigns.OrderByDescending(x => x.UpdatedUtc).ToList();
        }

        public DynamicCampaignState Load(string id)
        {
            var path = GetPath(id);
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }

        public void Save(DynamicCampaignState campaign)
        {
            campaign.UpdatedUtc = DateTime.UtcNow;
            var path = GetPath(campaign.Id);
            lock (FILE_LOCK)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var tempPath = $"{path}.tmp";
                File.WriteAllText(tempPath, Serialize(campaign));
                File.Move(tempPath, path, true);
            }
        }

        public void Delete(string id)
        {
            var path = GetPath(id);
            lock (FILE_LOCK)
            {
                if (File.Exists(path)) File.Delete(path);
                DeleteMission(id);
            }
        }

        // The .miz of the mission in progress, so it can be downloaded again
        public void SaveMission(string id, byte[] mizBytes)
        {
            lock (FILE_LOCK)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(GetMissionPath(id), mizBytes);
            }
        }

        public byte[] LoadMission(string id)
        {
            var path = GetMissionPath(id);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void DeleteMission(string id)
        {
            var path = GetMissionPath(id);
            if (File.Exists(path)) File.Delete(path);
        }

        public static string Serialize(DynamicCampaignState campaign) => JsonSerializer.Serialize(campaign, JSON_OPTIONS);

        public static DynamicCampaignState Deserialize(string json) => JsonSerializer.Deserialize<DynamicCampaignState>(json, JSON_OPTIONS);

        private string GetMissionPath(string id) => Path.ChangeExtension(GetPath(id), ".miz");

        private string GetPath(string id)
        {
            if (string.IsNullOrEmpty(id) || !ID_REGEX.IsMatch(id))
                throw new ArgumentException($"Invalid dynamic campaign id: {id}");
            return Path.Combine(Directory, $"{id}.json");
        }
    }
}
