using System;
using HorseFollowsYou.Services;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services.Multiplayer;

// ----------------------------
// ホスト側で同じ HorseId の重複実体を整理する
// ----------------------------
internal static class HorseInstanceConsolidator
{
    public static void ConsolidateHorseInstanceOnHost(Horse horse, GameLocation targetLocation)
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame || horse.currentLocation is null)
        {
            return;
        }

        string horseId = HorseMountRegistry.NormalizeHorseIdKey(HorseMountRegistry.GetHorseIdKey(horse));

        Utility.ForEachLocation(
            location =>
            {
                for (int i = location.characters.Count - 1; i >= 0; i--)
                {
                    if (location.characters[i] is not Horse existingHorse)
                    {
                        continue;
                    }

                    if (ReferenceEquals(existingHorse, horse))
                    {
                        continue;
                    }

                    string existingHorseId = HorseMountRegistry.NormalizeHorseIdKey(HorseMountRegistry.GetHorseIdKey(existingHorse));
                    if (!string.Equals(existingHorseId, horseId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    location.characters.RemoveAt(i);
                }

                return true;
            },
            true,
            true
        );

        horse.currentLocation = targetLocation;
        if (!targetLocation.characters.Contains(horse))
        {
            targetLocation.characters.Add(horse);
        }
    }

    public static void ConsolidateAllOnHost(HorseMountRegistry registry)
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame)
        {
            return;
        }

        foreach (Horse horse in registry.GetWorldHorses())
        {
            if (horse.currentLocation is null)
            {
                continue;
            }

            ConsolidateHorseInstanceOnHost(horse, horse.currentLocation);
        }
    }
}
