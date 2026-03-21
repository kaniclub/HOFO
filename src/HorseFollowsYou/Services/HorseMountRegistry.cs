using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// 設定表示用の馬情報
// ----------------------------
internal sealed class HorseMountOption
{
    public string HorseId { get; }
    public string DisplayName { get; }

    public HorseMountOption(string horseId, string displayName)
    {
        this.HorseId = horseId;
        this.DisplayName = displayName;
    }
}

// ----------------------------
// ワールド内の Horse 系一覧を解決する
// - ownerId は見ない
// - 現在騎乗中の Horse も拾う
// ----------------------------
internal sealed class HorseMountRegistry
{
    // ----------------------------
    // ワールド内の Horse 一覧を返す
    // ----------------------------
    public IReadOnlyList<Horse> GetWorldHorses()
    {
        List<Horse> horses = new();
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        if (!Context.IsWorldReady)
        {
            return horses;
        }

        Utility.ForEachLocation(
            location =>
            {
                foreach (NPC character in location.characters)
                {
                    if (character is Horse horse)
                    {
                        this.TryAddHorse(horses, seenIds, horse);
                    }
                }

                return true;
            },
            true,
            true
        );

        if (Game1.player.mount is Horse mountedHorse)
        {
            this.TryAddHorse(horses, seenIds, mountedHorse);
        }

        return horses;
    }

    // ----------------------------
    // 設定画面用の馬一覧を返す
    // ----------------------------
    public IReadOnlyList<HorseMountOption> GetMountOptions()
    {
        return this.GetWorldHorses()
            .Select(horse => new HorseMountOption(
                GetHorseIdKey(horse),
                this.GetHorseName(horse)
            ))
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ----------------------------
    // 最初に使える馬を返す
    // ----------------------------
    public Horse? FindFirstEligibleHorse(Func<string, bool> isHorseDisabled)
    {
        return this.GetWorldHorses().FirstOrDefault(horse => !isHorseDisabled(GetHorseIdKey(horse)));
    }

    // ----------------------------
    // HorseId を設定保存用の文字列へ変換する
    // ----------------------------
    public static string GetHorseIdKey(Horse horse)
    {
        return horse.HorseId.ToString("D");
    }

    // ----------------------------
    // HorseId 文字列を正規化する
    // ----------------------------
    public static string NormalizeHorseIdKey(string horseId)
    {
        return Guid.TryParse(horseId, out Guid parsed)
            ? parsed.ToString("D")
            : horseId.Trim();
    }

    // ----------------------------
    // 一覧へ馬を追加する
    // ----------------------------
    private void TryAddHorse(List<Horse> horses, HashSet<string> seenIds, Horse horse)
    {
        string horseId = GetHorseIdKey(horse);
        if (!seenIds.Add(horseId))
        {
            return;
        }

        horses.Add(horse);
    }

    // ----------------------------
    // 馬の表示名を返す
    // ----------------------------
    private string GetHorseName(Horse horse)
    {
        if (!string.IsNullOrWhiteSpace(horse.displayName))
        {
            return horse.displayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(horse.Name))
        {
            return horse.Name.Trim();
        }

        return "Horse";
    }

}
