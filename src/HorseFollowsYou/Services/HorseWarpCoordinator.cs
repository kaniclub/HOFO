using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬のマップ跨ぎ同行を管理する
// - ワープ可否の判定
// - 空きタイル探索
// - 再試行状態の管理
// ----------------------------
internal sealed class HorseWarpCoordinator
{
    internal enum WarpAttemptResult
    {
        Warped,
        RetryLater,
        Blocked,
    }

    private enum RetryBlockReason
    {
        NoRoom,
        NoOpenTile,
    }

    private readonly System.Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    private Point? retryOriginPlayerTile;
    private string? retryPlayerLocationName;
    private RetryBlockReason? retryBlockReason;

    private string? hardBlockedPlayerLocationName;
    private Utility.HorseWarpRestrictions? hardBlockedRestriction;

    // ----------------------------
    // ワープ管理クラスを初期化する
    // ----------------------------
    public HorseWarpCoordinator(System.Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    // ----------------------------
    // 再試行状態を初期化する
    // ----------------------------
    public void ResetRetryState()
    {
        this.ClearRetryBlock();
        this.ClearHardBlock();
    }

    // ----------------------------
    // プレイヤー近くへ馬をワープさせる
    // ----------------------------
    public WarpAttemptResult TryWarpHorseNearPlayer(Horse horse, Farmer player)
    {
        string fromLocation = horse.currentLocation.NameOrUniqueName;
        string toLocation = player.currentLocation.NameOrUniqueName;
        Point horseTileBefore = horse.TilePoint;
        Point playerTile = player.TilePoint;

        // ----------------------------
        // 同じロケーションで hard block 済みなら
        // ワープ処理自体を行わない
        // ----------------------------
        if (this.ShouldSkipByHardBlock(toLocation))
        {
            return WarpAttemptResult.Blocked;
        }

        Utility.HorseWarpRestrictions restrictions = Utility.GetHorseWarpRestrictionsForFarmer(player);
        Utility.HorseWarpRestrictions hardBlock = this.NormalizeHardBlockRestriction(restrictions);

        // ----------------------------
        // NoRoom 以外の制限は同ロケーション中スキップ対象にする
        // ----------------------------
        if (hardBlock != Utility.HorseWarpRestrictions.None)
        {
            this.ClearRetryBlock();
            this.SetHardBlock(toLocation, hardBlock);

            this.DebugLog(
                $"Horse warp skipped: reason=Restriction, restriction={hardBlock}, playerLocation={toLocation}, playerTile={this.FormatTile(playerTile)}, horseLocation={fromLocation}, horseTile={this.FormatTile(horseTileBefore)}"
            );

            return WarpAttemptResult.Blocked;
        }

        // ----------------------------
        // hard block ではないので解除する
        // ----------------------------
        this.ClearHardBlock();

        // ----------------------------
        // 再試行待ち中なら、8タイル以上動くまで探索しない
        // ロケーションが変わった場合は再試行を許可する
        // ----------------------------
        if (!this.CanRetryNow(player))
        {
            return WarpAttemptResult.RetryLater;
        }

        Vector2 openTile = this.FindOpenTileAroundPlayer(horse, player);
        if (openTile == Vector2.Zero)
        {
            RetryBlockReason reason = restrictions.HasFlag(Utility.HorseWarpRestrictions.NoRoom)
                ? RetryBlockReason.NoRoom
                : RetryBlockReason.NoOpenTile;

            this.SetRetryBlock(player, reason);

            this.DebugLog(
                $"Horse warp skipped: reason={reason}, playerLocation={toLocation}, playerTile={this.FormatTile(playerTile)}, horseLocation={fromLocation}, horseTile={this.FormatTile(horseTileBefore)}"
            );

            return WarpAttemptResult.RetryLater;
        }

        Game1.warpCharacter(horse, player.currentLocation, openTile);
        horse.Halt();
        horse.controller = null;

        Point horseTileAfter = horse.TilePoint;
        int manhattanDistance = this.GetManhattanDistance(playerTile, horseTileAfter);

        this.ClearRetryBlock();

        this.DebugLog(
            $"Horse warped: fromLocation={fromLocation}, toLocation={toLocation}, playerTile={this.FormatTile(playerTile)}, horseTileBefore={this.FormatTile(horseTileBefore)}, horseTileAfter={this.FormatTile(horseTileAfter)}, manhattanDistance={manhattanDistance}"
        );

        return WarpAttemptResult.Warped;
    }

    // ----------------------------
    // 同ロケーションの hard block でスキップするか判定する
    // ----------------------------
    private bool ShouldSkipByHardBlock(string playerLocationName)
    {
        return this.hardBlockedRestriction is not null
            && this.hardBlockedPlayerLocationName == playerLocationName;
    }

    // ----------------------------
    // hard block 状態を設定する
    // ----------------------------
    private void SetHardBlock(string playerLocationName, Utility.HorseWarpRestrictions restriction)
    {
        this.hardBlockedPlayerLocationName = playerLocationName;
        this.hardBlockedRestriction = restriction;
    }

    // ----------------------------
    // hard block 状態を解除する
    // ----------------------------
    private void ClearHardBlock()
    {
        this.hardBlockedPlayerLocationName = null;
        this.hardBlockedRestriction = null;
    }

    // ----------------------------
    // 再試行状態を設定する
    // ----------------------------
    private void SetRetryBlock(Farmer player, RetryBlockReason reason)
    {
        this.retryOriginPlayerTile = player.TilePoint;
        this.retryPlayerLocationName = player.currentLocation.NameOrUniqueName;
        this.retryBlockReason = reason;
    }

    // ----------------------------
    // 再試行状態を解除する
    // ----------------------------
    private void ClearRetryBlock()
    {
        this.retryOriginPlayerTile = null;
        this.retryPlayerLocationName = null;
        this.retryBlockReason = null;
    }

    // ----------------------------
    // 今再試行できるか判定する
    // ----------------------------
    private bool CanRetryNow(Farmer player)
    {
        if (this.retryOriginPlayerTile is null || this.retryPlayerLocationName is null || this.retryBlockReason is null)
        {
            return true;
        }

        if (this.retryPlayerLocationName != player.currentLocation.NameOrUniqueName)
        {
            this.ClearRetryBlock();
            return true;
        }

        return this.GetManhattanDistance(this.retryOriginPlayerTile.Value, player.TilePoint) >= 8;
    }

    // ----------------------------
    // 再試行不要の制限へ正規化する
    // ----------------------------
    private Utility.HorseWarpRestrictions NormalizeHardBlockRestriction(Utility.HorseWarpRestrictions restrictions)
    {
        if ((restrictions & Utility.HorseWarpRestrictions.NoOwnedHorse) != 0)
        {
            return Utility.HorseWarpRestrictions.NoOwnedHorse;
        }

        if ((restrictions & Utility.HorseWarpRestrictions.Indoors) != 0)
        {
            return Utility.HorseWarpRestrictions.Indoors;
        }

        if ((restrictions & Utility.HorseWarpRestrictions.InUse) != 0)
        {
            return Utility.HorseWarpRestrictions.InUse;
        }

        return Utility.HorseWarpRestrictions.None;
    }

    // ----------------------------
    // プレイヤー周囲の空きタイルを探す
    // - 半径1〜8は正面1列と後方1列を除外
    // - 半径9〜12は正面1列と後方1列も許可
    // ----------------------------
    private Vector2 FindOpenTileAroundPlayer(Horse horse, Farmer player)
    {
        Vector2 nearTile = this.FindOpenTileInRings(
            horse,
            player,
            minRadius: 1,
            maxRadius: 8,
            includeFrontLine: false,
            includeBackLine: false
        );

        if (nearTile != Vector2.Zero)
        {
            return nearTile;
        }

        return this.FindOpenTileInRings(
            horse,
            player,
            minRadius: 9,
            maxRadius: 12,
            includeFrontLine: true,
            includeBackLine: true
        );
    }

    // ----------------------------
    // 指定半径の候補を近い順で探す
    // ----------------------------
    private Vector2 FindOpenTileInRings(Horse horse, Farmer player, int minRadius, int maxRadius, bool includeFrontLine, bool includeBackLine)
    {
        Point playerTile = player.TilePoint;
        Point forward = this.GetDirectionPoint(player.FacingDirection);
        Point left = new(-forward.Y, forward.X);
        Point right = new(forward.Y, -forward.X);
        Point back = new(-forward.X, -forward.Y);

        for (int radius = minRadius; radius <= maxRadius; radius++)
        {
            foreach (Point candidate in this.EnumerateCandidatesForRadius(playerTile, radius, left, right, forward, back, includeFrontLine, includeBackLine))
            {
                if (this.CanPlaceHorseAtTile(horse, player.currentLocation, candidate))
                {
                    return new Vector2(candidate.X, candidate.Y);
                }
            }
        }

        return Vector2.Zero;
    }

    // ----------------------------
    // 半径の候補を優先順で返す
    // - 横
    // - 前方（正面1列を除く）
    // - 後方（後方1列を除く）
    // - 正面1列
    // - 後方1列
    // ----------------------------
    private IEnumerable<Point> EnumerateCandidatesForRadius(Point playerTile, int radius, Point left, Point right, Point forward, Point back, bool includeFrontLine, bool includeBackLine)
    {
        // 横
        yield return this.Offset(playerTile, left, radius);
        yield return this.Offset(playerTile, right, radius);

        // 前方（正面1列を除く）
        for (int lateral = radius - 1; lateral >= 1; lateral--)
        {
            int forwardDistance = radius - lateral;
            yield return this.Offset(this.Offset(playerTile, left, lateral), forward, forwardDistance);
            yield return this.Offset(this.Offset(playerTile, right, lateral), forward, forwardDistance);
        }

        // 後方（後方1列を除く）
        for (int lateral = radius - 1; lateral >= 1; lateral--)
        {
            int backDistance = radius - lateral;
            yield return this.Offset(this.Offset(playerTile, left, lateral), back, backDistance);
            yield return this.Offset(this.Offset(playerTile, right, lateral), back, backDistance);
        }

        // 正面1列
        if (includeFrontLine)
        {
            yield return this.Offset(playerTile, forward, radius);
        }

        // 後方1列
        if (includeBackLine)
        {
            yield return this.Offset(playerTile, back, radius);
        }
    }

    // ----------------------------
    // そのタイルへ馬を置けるか判定する
    // ----------------------------
    private bool CanPlaceHorseAtTile(Horse horse, GameLocation location, Point tile)
    {
        Vector2 tileVector = new(tile.X, tile.Y);

        if (!location.isTileOnMap(tileVector))
        {
            return false;
        }

        if (!location.isTilePlaceable(tileVector))
        {
            return false;
        }

        if (location is DecoratableLocation decoratableLocation
            && decoratableLocation.isTileOnWall(tile.X, tile.Y))
        {
            return false;
        }

        Vector2 originalPosition = horse.Position;
        int width = horse.GetBoundingBox().Width;

        horse.Position = new Vector2(tile.X * 64f + 32f - (width / 2f), tile.Y * 64f + 4f);
        Rectangle boundingBox = horse.GetBoundingBox();
        horse.Position = originalPosition;

        return !location.isCollidingPosition(
            boundingBox,
            Game1.viewport,
            horse is Farmer,
            0,
            glider: false,
            horse,
            pathfinding: false,
            projectile: false,
            ignoreCharacterRequirement: false,
            skipCollisionEffects: true
        );
    }

    // ----------------------------
    // デバッグログを出す
    // ----------------------------
    private void DebugLog(string message)
    {
        if (!this.getConfig().DebugMode)
        {
            return;
        }

        this.monitor.Log(message, LogLevel.Debug);
    }

    // ----------------------------
    // 向き番号から方向ベクトルを返す
    // ----------------------------
    private Point GetDirectionPoint(int facingDirection)
    {
        return facingDirection switch
        {
            0 => new Point(0, -1),
            1 => new Point(1, 0),
            2 => new Point(0, 1),
            3 => new Point(-1, 0),
            _ => new Point(0, 1),
        };
    }

    // ----------------------------
    // 指定方向へ座標をずらす
    // ----------------------------
    private Point Offset(Point origin, Point direction, int distance)
    {
        return new Point(origin.X + direction.X * distance, origin.Y + direction.Y * distance);
    }

    // ----------------------------
    // タイル座標を文字列へ変換する
    // ----------------------------
    private string FormatTile(Point tile)
    {
        return $"({tile.X}, {tile.Y})";
    }

    // ----------------------------
    // 2点のマンハッタン距離を返す
    // ----------------------------
    private int GetManhattanDistance(Point a, Point b)
    {
        return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
    }
}