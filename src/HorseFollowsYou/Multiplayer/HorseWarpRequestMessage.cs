using Microsoft.Xna.Framework;

namespace HorseFollowsYou.Multiplayer;

// ----------------------------
// クライアントからホストへ送る馬ワープ要求
// ----------------------------
internal sealed class HorseWarpRequestMessage
{
    public string HorseId { get; set; } = string.Empty;
    public long PlayerId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int PlayerTileX { get; set; }
    public int PlayerTileY { get; set; }
    public int FacingDirection { get; set; }

    public Point GetPlayerTile()
    {
        return new Point(this.PlayerTileX, this.PlayerTileY);
    }
}
