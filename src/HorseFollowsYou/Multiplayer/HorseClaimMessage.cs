namespace HorseFollowsYou.Multiplayer;

// ----------------------------
// マルチプレイ中の同行馬占有状態を共有する
// ----------------------------
internal sealed class HorseClaimMessage
{
    public string HorseId { get; set; } = string.Empty;
    public long PlayerId { get; set; }
    public long StampUtcMs { get; set; }
    public bool IsClaimed { get; set; }
}
