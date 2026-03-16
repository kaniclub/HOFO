using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬のマップ跨ぎ同行を管理する
// - ワープ可否の判定
// - 空きタイル探索
// - 馬のワープ実行
// ----------------------------
internal sealed class HorseWarpCoordinator
{
    internal enum WarpAttemptResult
    {
        Warped,
        RetryLaterNoRoom,
        Blocked,
    }

    // ----------------------------
    // ワープ管理クラスを初期化する
    // ----------------------------
    public HorseWarpCoordinator()
    {
    }

    // ----------------------------
    // プレイヤー近くへ馬をワープさせる
    // ----------------------------
    public WarpAttemptResult TryWarpHorseNearPlayer(Horse horse, Farmer player)
    {
        Utility.HorseWarpRestrictions restrictions = Utility.GetHorseWarpRestrictionsForFarmer(player);

        if (restrictions != Utility.HorseWarpRestrictions.None
            && restrictions != Utility.HorseWarpRestrictions.NoRoom)
        {
            return WarpAttemptResult.Blocked;
        }

        bool usedWideSearch = false;

        // ----------------------------
        // まずは近場を探す
        // ----------------------------
        Vector2 openTile = this.FindOpenTileNearby(horse, player);

        // ----------------------------
        // 近場で見つからなければ、広域探索へ切り替える
        // ----------------------------
        if (openTile == Vector2.Zero)
        {
            openTile = this.FindOpenTileWithWideSearch(horse, player);
            usedWideSearch = openTile != Vector2.Zero;
        }

        if (openTile == Vector2.Zero)
        {
            if (restrictions == Utility.HorseWarpRestrictions.NoRoom)
            {
                return WarpAttemptResult.RetryLaterNoRoom;
            }

            return WarpAttemptResult.Blocked;
        }

        Game1.warpCharacter(horse, player.currentLocation, openTile);
        horse.Halt();
        horse.controller = null;

        return WarpAttemptResult.Warped;
    }

    // ----------------------------
    // 近場の空きタイルを探す
    // ----------------------------
    private Vector2 FindOpenTileNearby(Horse horse, Farmer player)
    {
        return Utility.recursiveFindOpenTileForCharacter(
            horse,
            player.currentLocation,
            player.Tile,
            8
        );
    }

    // ----------------------------
    // 少し広めに空きタイルを探す
    // ----------------------------
    private Vector2 FindOpenTileWithWideSearch(Horse horse, Farmer player)
    {
        int[] searchRanges = new[] { 8, 12, 16, 20, 24, 32, 40, 56 };

        foreach (int range in searchRanges)
        {
            Vector2 tile = Utility.recursiveFindOpenTileForCharacter(
                horse,
                player.currentLocation,
                player.Tile,
                range
            );

            if (tile != Vector2.Zero)
            {
                return tile;
            }
        }

        return Vector2.Zero;
    }
}