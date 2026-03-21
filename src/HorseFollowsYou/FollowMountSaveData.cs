namespace HorseFollowsYou;

// ----------------------------
// セーブデータごとの同行対象外設定
// ----------------------------
internal sealed class FollowMountSaveData
{
    public List<string> DisabledHorseIds { get; set; } = new();
}
