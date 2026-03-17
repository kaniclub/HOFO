using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬の同行の管理
// - 対象馬の取得
// - 同行トグルの管理
// - 経路保持
// - 自前移動
// ----------------------------
internal sealed class HorseFollowManager
{
    private enum FollowState
    {
        Idle,
        Mounted,
        FollowPending,
        Following,
        ArrivedIdle,
        WarpBlocked,
        Paused,
    }

    private readonly ITranslationHelper translation;
    private readonly System.Func<ModConfig> getConfig;
    private readonly HorseWarpCoordinator warpCoordinator;
    private readonly HorseFollowTargetResolver targetResolver;
    private readonly HorsePathfinder pathfinder;
    private readonly HorseMovementService movementService;

    private Horse? trackedHorse;
    private FollowState state = FollowState.Idle;
    private bool followEnabled;
    private bool wasMountedLastTick;
    private long followPendingUntilMs;
    private Point? lastPlayerTile;
    private Point lastPlayerMoveDirection = Point.Zero;
    private long lastPathBuildTimeMs;
    private Point? lastTargetTile;

    // ----------------------------
    // 管理クラスを初期化する
    // ----------------------------
    public HorseFollowManager(ITranslationHelper translation, System.Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.translation = translation;
        this.getConfig = getConfig;
        this.warpCoordinator = new HorseWarpCoordinator(getConfig, monitor);
        this.targetResolver = new HorseFollowTargetResolver(getConfig);
        this.pathfinder = new HorsePathfinder(this.targetResolver);
        this.movementService = new HorseMovementService(getConfig, this.targetResolver);
        this.Reset();
    }

    // ----------------------------
    // 状態を初期化する
    // ----------------------------
    public void Reset()
    {
        this.trackedHorse = null;
        this.state = FollowState.Idle;
        this.followEnabled = true;
        this.wasMountedLastTick = false;
        this.followPendingUntilMs = 0;
        this.lastPlayerTile = null;
        this.lastPlayerMoveDirection = Point.Zero;
        this.lastPathBuildTimeMs = 0;
        this.lastTargetTile = null;
        this.movementService.Reset();
        this.warpCoordinator.ResetRetryState();
    }

    // ----------------------------
    // セーブ読込後の状態を整える
    // ----------------------------
    public void OnSaveLoaded()
    {
        this.Reset();
        this.SyncTrackedHorse();
    }

    // ----------------------------
    // 画面移動後の状態を整える
    // ----------------------------
    public void OnWarped()
    {
        if (!this.IsEnabled())
        {
            return;
        }

        this.SyncTrackedHorse();
        this.lastPlayerTile = Game1.player.TilePoint;
        this.lastPlayerMoveDirection = Point.Zero;
        this.InvalidatePath();

        if (Game1.player.isRidingHorse())
        {
            this.state = FollowState.Mounted;
            return;
        }

        if (this.trackedHorse is null)
        {
            this.state = FollowState.Idle;
            return;
        }

        if (!this.followEnabled)
        {
            this.state = FollowState.Idle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (this.trackedHorse.currentLocation == Game1.player.currentLocation)
        {
            this.warpCoordinator.ResetRetryState();
            this.EnterFollowPending();
            return;
        }

        if (this.TryWarpHorseToPlayer())
        {
            return;
        }

        this.state = FollowState.WarpBlocked;
    }

    // ----------------------------
    // 毎 tick の更新を行う
    // ----------------------------
    public void OnUpdateTicked(uint ticks)
    {
        if (!this.IsEnabled())
        {
            return;
        }

        this.ProcessToggleKey();
        this.SyncTrackedHorse();
        this.UpdatePlayerMovementStamp();

        if (Game1.player.isRidingHorse())
        {
            this.HandleMountedState();
            return;
        }

        if (this.wasMountedLastTick)
        {
            this.wasMountedLastTick = false;
            this.SyncHorseFacingAfterDismount();
            if (this.followEnabled)
            {
                this.EnterFollowPending();
            }
            else
            {
                this.state = FollowState.Idle;
            }
        }

        if (this.trackedHorse is null)
        {
            this.state = FollowState.Idle;
            return;
        }

        this.ApplyCollisionPreferences(this.trackedHorse);
        this.UpdateHorseMovementStamp();

        if (!this.followEnabled)
        {
            this.warpCoordinator.ResetRetryState();
            this.state = FollowState.Idle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        long nowMs = System.Environment.TickCount64;

        // ----------------------------
        // 馬が別マップにいるとき
        // ----------------------------
        if (this.trackedHorse.currentLocation != Game1.player.currentLocation)
        {
            if (this.TryWarpHorseToPlayer())
            {
                return;
            }

            this.state = FollowState.WarpBlocked;
            return;
        }

        this.warpCoordinator.ResetRetryState();

        if (Game1.activeClickableMenu is not null || Game1.eventUp || !Game1.player.canMove)
        {
            this.state = FollowState.Paused;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (this.state == FollowState.Paused || this.state == FollowState.WarpBlocked)
        {
            this.EnterFollowPending();
        }

        if (this.state == FollowState.FollowPending && nowMs < this.followPendingUntilMs)
        {
            return;
        }

        Point? targetTile = this.ResolveFollowTargetTile(this.trackedHorse, Game1.player);
        if (targetTile is null)
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        this.lastTargetTile = targetTile;

        float targetDistance = Vector2.Distance(this.trackedHorse.Tile, new Vector2(targetTile.Value.X, targetTile.Value.Y));
        float playerDistance = Vector2.Distance(this.trackedHorse.Tile, Game1.player.Tile);
        if (targetDistance <= this.getConfig().StopDistance || playerDistance <= this.getConfig().StopDistance)
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (playerDistance < this.getConfig().FollowStartDistance && this.movementService.HasNoPath())
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (this.ShouldRebuildPath(nowMs, targetTile.Value))
        {
            this.RebuildPath(targetTile.Value, nowMs);
        }

        if (this.movementService.IsPathFinished())
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        this.state = FollowState.Following;
        this.MoveAlongPath(nowMs, Game1.currentGameTime);
    }

    // ----------------------------
    // 騎乗中の状態を処理する
    // ----------------------------
    private void HandleMountedState()
    {
        this.warpCoordinator.ResetRetryState();
        this.state = FollowState.Mounted;
        this.wasMountedLastTick = true;
        this.lastTargetTile = null;

        if (Game1.player.mount is Horse horse)
        {
            this.TrackHorse(horse);
            this.movementService.PrepareMountedState(horse);
        }
        else
        {
            this.movementService.InvalidatePath();
        }
    }

    // ----------------------------
    // 降馬直後の向きを同期する
    // ----------------------------
    private void SyncHorseFacingAfterDismount()
    {
        if (this.trackedHorse is null)
        {
            return;
        }

        int facingDirection = Game1.player.FacingDirection;
        this.movementService.SyncHorseFacingAfterDismount(this.trackedHorse, facingDirection);
    }

    // ----------------------------
    // トグルキー入力を処理する
    // ----------------------------
    private void ProcessToggleKey()
    {
        if (!this.IsKeybindPressed(this.getConfig().ToggleFollowKey))
        {
            return;
        }

        this.followEnabled = !this.followEnabled;
        this.InvalidatePath();

        if (this.followEnabled)
        {
            this.ShowMessage("message.follow_enabled");
            if (!Game1.player.isRidingHorse())
            {
                this.EnterFollowPending();
            }
        }
        else
        {
            this.warpCoordinator.ResetRetryState();
            this.ShowMessage("message.follow_disabled");
            if (this.trackedHorse is not null)
            {
                this.StopHorse(this.trackedHorse);
            }
        }
    }

    // ----------------------------
    // キーが押されたか確認する
    // ----------------------------
    private bool IsKeybindPressed(KeybindList? keybind)
    {
        if (keybind is null)
        {
            return false;
        }

        try
        {
            return keybind.IsBound && keybind.JustPressed();
        }
        catch (System.NullReferenceException)
        {
            return false;
        }
    }

    // ----------------------------
    // 同行開始待機へ入る
    // ----------------------------
    private void EnterFollowPending()
    {
        this.warpCoordinator.ResetRetryState();
        this.state = FollowState.FollowPending;
        this.followPendingUntilMs = System.Environment.TickCount64 + this.getConfig().DismountDelayMilliseconds;
        this.InvalidatePath();
        if (this.trackedHorse is not null)
        {
            this.StopHorse(this.trackedHorse);
        }
    }

    // ----------------------------
    // 別マップの馬をプレイヤー側へワープさせる
    // ----------------------------
    private bool TryWarpHorseToPlayer()
    {
        if (this.trackedHorse is null || !this.getConfig().EnableWarpFollow)
        {
            this.warpCoordinator.ResetRetryState();
            return false;
        }

        HorseWarpCoordinator.WarpAttemptResult result = this.warpCoordinator.TryWarpHorseNearPlayer(this.trackedHorse, Game1.player);
        if (result == HorseWarpCoordinator.WarpAttemptResult.Warped)
        {
            this.EnterFollowPending();
            return true;
        }

        return false;
    }

    // ----------------------------
    // 対象の馬を同期する
    // ----------------------------
    private void SyncTrackedHorse()
    {
        if (Game1.player.mount is Horse mountedHorse)
        {
            this.TrackHorse(mountedHorse);
            return;
        }

        Horse? ownedHorse = Utility.findHorseForPlayer(Game1.player.UniqueMultiplayerID);
        if (ownedHorse is not null)
        {
            this.TrackHorse(ownedHorse);
        }
    }

    // ----------------------------
    // 馬を追跡対象にする
    // ----------------------------
    private void TrackHorse(Horse horse)
    {
        if (!ReferenceEquals(this.trackedHorse, horse))
        {
            this.trackedHorse = horse;
            this.movementService.OnTrackedHorseChanged(horse);
            this.ApplyCollisionPreferences(horse);
            this.InvalidatePath();
            return;
        }

        this.trackedHorse = horse;
        this.ApplyCollisionPreferences(horse);
    }

    // ----------------------------
    // プレイヤーの移動方向を記録する
    // ----------------------------
    private void UpdatePlayerMovementStamp()
    {
        Point currentTile = Game1.player.TilePoint;
        if (this.lastPlayerTile is Point last && last != currentTile)
        {
            Point delta = new(currentTile.X - last.X, currentTile.Y - last.Y);
            this.lastPlayerMoveDirection = new Point(System.Math.Sign(delta.X), System.Math.Sign(delta.Y));
        }

        this.lastPlayerTile = currentTile;
    }

    // ----------------------------
    // 馬の移動時刻を更新する
    // ----------------------------
    private void UpdateHorseMovementStamp()
    {
        if (this.trackedHorse is null)
        {
            return;
        }

        this.movementService.UpdateHorseMovementStamp(this.trackedHorse);
    }

    // ----------------------------
    // 馬の目標タイルを決める
    // ----------------------------
    private Point? ResolveFollowTargetTile(Horse horse, Farmer player)
    {
        return this.targetResolver.ResolveFollowTargetTile(horse, player, this.lastPlayerMoveDirection);
    }

    // ----------------------------
    // 経路を作り直すか判定する
    // ----------------------------
    private bool ShouldRebuildPath(long nowMs, Point targetTile)
    {
        if (this.trackedHorse is null)
        {
            return false;
        }

        if (this.movementService.IsPathFinished())
        {
            return true;
        }

        if (this.lastTargetTile is null || this.lastTargetTile.Value != targetTile)
        {
            return nowMs - this.lastPathBuildTimeMs >= this.GetPathRebuildMilliseconds();
        }

        if (this.lastPlayerTile is null)
        {
            return false;
        }

        if (nowMs - this.movementService.GetLastHorseMoveTimeMs() >= this.GetPathRebuildMilliseconds() * 2)
        {
            return true;
        }

        return false;
    }

    // ----------------------------
    // 経路を再計算する
    // ----------------------------
    private void RebuildPath(Point targetTile, long nowMs)
    {
        if (this.trackedHorse is null)
        {
            return;
        }

        List<Point>? path = this.pathfinder.BuildPath(this.trackedHorse.currentLocation, this.trackedHorse, this.trackedHorse.TilePoint, targetTile);
        if (path is null || path.Count == 0)
        {
            this.movementService.SetPath(null);
            this.lastPathBuildTimeMs = nowMs;
            return;
        }

        this.movementService.SetPath(path);
        this.lastPathBuildTimeMs = nowMs;
        this.lastTargetTile = targetTile;
    }

    // ----------------------------
    // 衝突設定を反映する
    // ----------------------------
    private void ApplyCollisionPreferences(Horse horse)
    {
        horse.farmerPassesThrough = this.getConfig().IgnorePlayerCollision;
    }

    // ----------------------------
    // 経路に沿って移動する
    // ----------------------------
    private void MoveAlongPath(long nowMs, GameTime time)
    {
        if (this.trackedHorse is null)
        {
            return;
        }

        this.movementService.MoveAlongPath(this.trackedHorse, nowMs, time, this.lastPathBuildTimeMs, this.GetPathRebuildMilliseconds());
    }

    // ----------------------------
    // 経路再計算の間隔を返す
    // ----------------------------
    private long GetPathRebuildMilliseconds()
    {
        return (long)System.Math.Round(this.getConfig().PathRebuildSeconds * 1000f);
    }

    // ----------------------------
    // 馬を停止させる
    // ----------------------------
    private void StopHorse(Horse horse)
    {
        this.movementService.StopHorse(horse);
    }

    // ----------------------------
    // 経路情報を消す
    // ----------------------------
    private void InvalidatePath()
    {
        this.movementService.InvalidatePath();
        this.lastTargetTile = null;
        this.lastPathBuildTimeMs = 0;
    }

    // ----------------------------
    // HUD メッセージを表示する
    // ----------------------------
    private void ShowMessage(string key)
    {
        if (!this.getConfig().EnableHudMessages)
        {
            return;
        }

        string text = this.translation.Get(key).ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
        }
    }

    // ----------------------------
    // Mod が有効か確認する
    // ----------------------------
    private bool IsEnabled()
    {
        return Context.IsWorldReady && this.getConfig().ModEnabled;
    }
}