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

using System.Linq;

namespace BriefingRoom4DCS.DynamicCampaign
{
    // User-facing texts of the dynamic campaign (keys in Database/Language/DynamicCampaign)
    public static class DynamicCampaignText
    {
        public static string AssetKind(IBriefingRoom briefingRoom, DCAssetKind kind) => briefingRoom.Translate($"DCAsset{kind}");

        public static string AssetLabel(IBriefingRoom briefingRoom, DynamicCampaignState state, DCAsset asset)
        {
            if (asset.Kind == DCAssetKind.Airbase)
                return $"{AssetKind(briefingRoom, asset.Kind)} {asset.Name}";
            var label = $"{AssetKind(briefingRoom, asset.Kind)} {asset.Name}";
            if (asset.IsRoute)
            {
                var from = state.GetAsset(asset.FromId);
                var to = state.GetAsset(asset.ToId);
                return from != null && to != null ? $"{label} ({from.Name} > {to.Name})" : label;
            }
            return string.IsNullOrEmpty(asset.Near) ? label : $"{label} ({briefingRoom.Translate("DCNear", asset.Near)})";
        }

        public static string SortieKind(IBriefingRoom briefingRoom, DCSortieKind kind) => briefingRoom.Translate($"DCSortie{kind}");

        public static string SortieImpact(IBriefingRoom briefingRoom, DynamicCampaignState state, DCSortie sortie) =>
            briefingRoom.Translate($"DCBriefingImpact{sortie.Kind}", AssetLabel(briefingRoom, state, state.GetAsset(sortie.TargetId)));

        // Arguments starting with "@" are asset ids, shown with the current language
        public static string Event(IBriefingRoom briefingRoom, DynamicCampaignState state, DCEvent campaignEvent)
        {
            var args = campaignEvent.Args.Select(x =>
            {
                if (x != null && x.StartsWith('%')) // Weapon counts: "%AAM=2;AGM=4"
                    return (object)string.Join(", ", x[1..].Split(';', System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(pair => pair.Split('='))
                        .Where(pair => pair.Length == 2)
                        .Select(pair => $"{briefingRoom.Translate($"DCWeapon{pair[0]}")} {pair[1]}"));
                if (x == null || !x.StartsWith('@')) return (object)x;
                var asset = state.GetAsset(x[1..]);
                return asset == null ? x : AssetLabel(briefingRoom, state, asset);
            }).ToArray();
            return briefingRoom.Translate(campaignEvent.Key, args);
        }

        public static string Stock(IBriefingRoom briefingRoom, DCAsset asset) => DynamicCampaignInventory.Describe(asset, briefingRoom.Translate);

        public static string Side(IBriefingRoom briefingRoom, DynamicCampaignState state, Coalition? coalition) =>
            coalition == null ? "" : briefingRoom.Translate(coalition == state.PlayerCoalition ? "DCSideAlly" : "DCSideEnemy");
    }
}
