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
        MovementSuppressed,
    }

    private readonly ITranslationHelper translation;
    private readonly System.Func<ModConfig> getConfig;
    private readonly IMonitor monitor;
    private readonly HorseWarpCoordinator warpCoordinator;
    private readonly HorseFollowTargetResolver targetResolver;
    private readonly HorsePathfinder pathfinder;
    private readonly HorseMovementService movementService;
    private readonly Func<string, bool> isHorseDisabled;
    private readonly Func<Horse?> getDefaultTrackedHorse;

    private Horse? trackedHorse;
    private FollowState state = FollowState.Idle;
    private bool followEnabled;
    private bool wasMountedLastTick;
    private long followPendingUntilMs;
    private Point? lastPlayerTile;
    private Point lastPlayerMoveDirection = Point.Zero;
    private long lastPathBuildTimeMs;
    private Point? lastTargetTile;
    private bool pauseFollowUntilWarp;
    private Point? noPathRetryPlayerTile;
    private string? noPathRetryLocationName;
    private int noPathFailureCount;
    private string? suspendedLocationName;
    private bool horseSummonedThisTick;
    private bool horseFluteWarpRequested;
    private long targetNotFoundRetryUntilMs;

    // ----------------------------
    // フルートワープ要求を受け取る
    // ----------------------------
    public void OnHorseFluteWarpRequested()
    {
        this.horseFluteWarpRequested = true;
    }

    // ----------------------------
    // デバッグ描画用に現在の経路を返す
    // ----------------------------
    public IReadOnlyList<Point>? GetDebugPath()
    {
        return this.movementService.GetCurrentPathForDebug();
    }

    // ----------------------------
    // デバッグ描画用に目標タイルを返す
    // ----------------------------
    public Point? GetDebugTargetTile()
    {
        return this.lastTargetTile;
    }

    // ----------------------------
    // 馬の対象外設定変更を反映する
    // ----------------------------
    public void OnMountFilterChanged()
    {
        if (this.trackedHorse is not null && this.IsHorseDisabled(this.trackedHorse))
        {
            this.ClearTrackedHorse();
        }

        if (this.trackedHorse is null)
        {
            this.SyncTrackedHorse();
        }

        if (this.trackedHorse is not null)
        {
            this.ApplyCollisionPreferences(this.trackedHorse);
        }
    }

    // ----------------------------
    // 管理クラスを初期化する
    // ----------------------------
    public HorseFollowManager(
        ITranslationHelper translation,
        System.Func<ModConfig> getConfig,
        IMonitor monitor,
        Func<string, bool> isHorseDisabled,
        Func<Horse?> getDefaultTrackedHorse)
    {
        this.translation = translation;
        this.getConfig = getConfig;
        this.monitor = monitor;
        this.isHorseDisabled = isHorseDisabled;
        this.getDefaultTrackedHorse = getDefaultTrackedHorse;
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
        this.ReleaseTrackedHorseCollision();
        this.trackedHorse = null;
        this.state = FollowState.Idle;
        this.followEnabled = true;
        this.wasMountedLastTick = false;
        this.followPendingUntilMs = 0;
        this.lastPlayerTile = null;
        this.lastPlayerMoveDirection = Point.Zero;
        this.lastPathBuildTimeMs = 0;
        this.lastTargetTile = null;
        this.pauseFollowUntilWarp = false;
        this.noPathRetryPlayerTile = null;
        this.noPathRetryLocationName = null;
        this.noPathFailureCount = 0;
        this.suspendedLocationName = null;
        this.horseSummonedThisTick = false;
        this.horseFluteWarpRequested = false;
        this.targetNotFoundRetryUntilMs = 0;
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
        this.RefreshLocationFollowPauseState();
        this.followEnabled = this.getConfig().EnableFollowOnLoad;
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

        this.RefreshLocationFollowPauseState();
        this.ResetPathFailureState();

        if (this.trackedHorse is not null && !this.IsTrackedHorseStillValid())
        {
            this.ClearTrackedHorse();
        }

        this.lastPlayerTile = Game1.player.TilePoint;
        this.lastPlayerMoveDirection = Point.Zero;
        this.InvalidatePath();

        if (this.GetEligibleMountedHorse() is not null)
        {
            this.state = FollowState.Mounted;
            return;
        }

        if (this.trackedHorse is null)
        {
            this.state = FollowState.Idle;
            return;
        }

        // ----------------------------
        // 屋内ではワープも追従も開始しない
        // ----------------------------
        if (this.pauseFollowUntilWarp)
        {
            this.warpCoordinator.ResetRetryState();
            this.state = FollowState.Paused;
            this.StopHorse(this.trackedHorse);
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

        // ----------------------------
        // 有効な騎乗中はその馬を追跡対象にする
        // ----------------------------
        Horse? eligibleMountedHorse = this.GetEligibleMountedHorse();
        if (eligibleMountedHorse is not null)
        {
            this.TrackHorse(eligibleMountedHorse);
        }
        else if (this.trackedHorse is not null && !this.IsTrackedHorseStillValid())
        {
            this.ClearTrackedHorse();
        }

        if (this.trackedHorse is not null)
        {
            this.ApplyCollisionPreferences(this.trackedHorse);
        }

        this.horseSummonedThisTick = this.horseFluteWarpRequested;
        this.horseFluteWarpRequested = false;
        this.TryAutoEnableFollow();
        this.UpdatePlayerMovementStamp();

        if (eligibleMountedHorse is not null)
        {
            this.HandleMountedState(eligibleMountedHorse);
            return;
        }

        if (this.wasMountedLastTick)
        {
            this.wasMountedLastTick = false;
            if (this.trackedHorse is not null)
            {
                this.movementService.SyncHorseFacingAfterDismount(this.trackedHorse, Game1.player.FacingDirection);
            }

            if (this.followEnabled && !this.pauseFollowUntilWarp)
            {
                this.EnterFollowPending();
            }
            else if (this.pauseFollowUntilWarp)
            {
                this.state = FollowState.Paused;
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

        // ----------------------------
        // 屋内など追従停止中のロケーションでは
        // ワープ処理も経路確認も行わない
        // ----------------------------
        if (this.pauseFollowUntilWarp)
        {
            this.warpCoordinator.ResetRetryState();

            if (this.state != FollowState.Paused || !this.movementService.HasNoPath())
            {
                this.InvalidatePath();
                this.StopHorse(this.trackedHorse);
            }

            this.state = FollowState.Paused;
            return;
        }

        this.UpdateHorseMovementStamp();

        if (!this.followEnabled)
        {
            this.warpCoordinator.ResetRetryState();

            if (this.state != FollowState.Idle || !this.movementService.HasNoPath())
            {
                this.InvalidatePath();
                this.StopHorse(this.trackedHorse);
            }

            this.state = FollowState.Idle;
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
            if (this.state != FollowState.Paused || !this.movementService.HasNoPath())
            {
                this.InvalidatePath();
                this.StopHorse(this.trackedHorse);
            }

            this.state = FollowState.Paused;
            return;
        }

        if (this.IsWaitingForSuspendedLocationChange() || this.IsWaitingForNoPathRetry())
        {
            if (this.state != FollowState.Paused || !this.movementService.HasNoPath())
            {
                this.InvalidatePath();
                this.StopHorse(this.trackedHorse);
            }

            this.state = FollowState.Paused;
            return;
        }

        if (this.state == FollowState.Paused || this.state == FollowState.WarpBlocked)
        {
            bool preservePathFailureState = this.noPathRetryPlayerTile is not null;
            this.EnterFollowPending(!preservePathFailureState);
        }

        if (this.state == FollowState.FollowPending && nowMs < this.followPendingUntilMs)
        {
            return;
        }

        float playerDistance = Vector2.Distance(this.trackedHorse.Tile, Game1.player.Tile);
        if (playerDistance <= this.getConfig().StopDistance && !this.HasActivePath())
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        bool suppressMovementByAbortDistance = false;
        int horseToPlayerDistance = this.GetManhattanDistance(this.trackedHorse.TilePoint, Game1.player.TilePoint);
        if (this.getConfig().PathAbortDistance > 0
            && horseToPlayerDistance >= this.getConfig().PathAbortDistance)
        {
            if (this.getConfig().PathFailureAction == 3)
            {
                suppressMovementByAbortDistance = true;
            }
            else
            {
                this.DebugLog($"[Follow] path_abort distance={horseToPlayerDistance} threshold={this.getConfig().PathAbortDistance} horse={this.trackedHorse.TilePoint} player={Game1.player.TilePoint}");
                this.HandlePathFailureAction();
                return;
            }
        }

        if (nowMs < this.targetNotFoundRetryUntilMs)
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        Point? targetTile = this.targetResolver.ResolveFollowTargetTile(this.trackedHorse, Game1.player, this.lastPlayerMoveDirection);
        if (targetTile is null)
        {
            this.targetNotFoundRetryUntilMs = nowMs + 500;
            this.DebugLog($"[PathDebug] target_not_found horse={this.trackedHorse.TilePoint} player={Game1.player.TilePoint} state={this.state}");
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        this.targetNotFoundRetryUntilMs = 0;
        this.lastTargetTile = targetTile;

        float targetDistance = Vector2.Distance(this.trackedHorse.Tile, new Vector2(targetTile.Value.X, targetTile.Value.Y));
        if (targetDistance <= this.getConfig().StopDistance && !this.HasActivePath())
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
            if (!this.RebuildPath(targetTile.Value, nowMs))
            {
                this.HandleNoPathFailure();
                return;
            }
        }

        if (this.movementService.IsPathFinished())
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (suppressMovementByAbortDistance)
        {
            if (this.state != FollowState.MovementSuppressed)
            {
                this.DebugLog($"[Follow] move_suppressed distance={horseToPlayerDistance} threshold={this.getConfig().PathAbortDistance} horse={this.trackedHorse.TilePoint} player={Game1.player.TilePoint}");
            }

            this.state = FollowState.MovementSuppressed;
            this.StopHorse(this.trackedHorse);
            return;
        }

        long lastHorseMoveTimeBeforeMove = this.movementService.GetLastHorseMoveTimeMs();
        Point horseTileBeforeMove = this.trackedHorse.TilePoint;

        this.state = FollowState.Following;
        this.movementService.MoveAlongPath(this.trackedHorse, nowMs, Game1.currentGameTime, this.lastPathBuildTimeMs, this.GetPathRebuildMilliseconds());

        bool horseMovedThisTick = this.trackedHorse.TilePoint != horseTileBeforeMove
            || this.movementService.GetLastHorseMoveTimeMs() != lastHorseMoveTimeBeforeMove;
        if (horseMovedThisTick)
        {
            if (this.noPathFailureCount > 0 || this.noPathRetryPlayerTile is not null)
            {
                this.DebugLog($"[Follow] move_resumed clear_no_path_retry horse={this.trackedHorse.TilePoint} player={Game1.player.TilePoint}");
            }

            this.noPathFailureCount = 0;
            this.ClearNoPathRetryWait();
            return;
        }

        long stalledMs = nowMs - this.movementService.GetLastHorseMoveTimeMs();
        long stallThresholdMs = System.Math.Max(this.GetPathRebuildMilliseconds(), 250L);
        if (this.movementService.HasNoPath() || stalledMs >= stallThresholdMs)
        {
            this.DebugLog($"[Follow] no_path_after_move horse={this.trackedHorse.TilePoint} player={Game1.player.TilePoint} state={this.state} hasNoPath={this.movementService.HasNoPath()} stalledMs={stalledMs}");
            this.HandleNoPathFailure();
            return;
        }
    }

    // ----------------------------
    // まだ移動中の経路が残っているかを返す
    // ----------------------------
    private bool HasActivePath()
    {
        return !this.movementService.HasNoPath() && !this.movementService.IsPathFinished();
    }

    // ----------------------------
    // 騎乗中の状態を処理する
    // ----------------------------
    private void HandleMountedState(Horse horse)
    {
        this.warpCoordinator.ResetRetryState();
        this.ResetPathFailureState();

        if (!this.followEnabled && this.getConfig().AutoEnableFollowOnMountOrFlute)
        {
            this.followEnabled = true;
            this.ShowMessage("message.follow_enabled");
        }

        this.state = FollowState.Mounted;
        this.wasMountedLastTick = true;
        this.lastTargetTile = null;
        this.TrackHorse(horse);
        this.movementService.PrepareMountedState(horse);
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

            if (this.pauseFollowUntilWarp)
            {
                this.state = FollowState.Paused;
                if (this.trackedHorse is not null)
                {
                    this.StopHorse(this.trackedHorse);
                }

                return;
            }

            if (this.GetEligibleMountedHorse() is null)
            {
                this.EnterFollowPending();
            }
        }
        else
        {
            this.warpCoordinator.ResetRetryState();
            this.DebugLog("[Follow] follow_disabled reason=toggle");
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
    private void EnterFollowPending(bool resetPathFailureState = true)
    {
        this.warpCoordinator.ResetRetryState();

        if (resetPathFailureState)
        {
            this.ResetPathFailureState();
        }

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
    private bool TryWarpHorseToPlayer(bool playSummonEffects = false)
    {
        if (this.pauseFollowUntilWarp)
        {
            this.warpCoordinator.ResetRetryState();
            return false;
        }

        if (this.trackedHorse is null || !this.getConfig().EnableWarpFollow)
        {
            this.warpCoordinator.ResetRetryState();
            return false;
        }

        HorseWarpCoordinator.WarpAttemptResult result = this.warpCoordinator.TryWarpHorseNearPlayer(this.trackedHorse, Game1.player, playSummonEffects);
        if (result == HorseWarpCoordinator.WarpAttemptResult.Warped)
        {
            this.EnterFollowPending();
            return true;
        }

        return false;
    }

    // ----------------------------
    // 対象の馬を同期する(初回ロード時のみ実行)
    // ----------------------------
    private void SyncTrackedHorse()
    {
        Horse? mountedHorse = this.GetEligibleMountedHorse();
        if (mountedHorse is not null)
        {
            this.TrackHorse(mountedHorse);
            return;
        }

        Horse? defaultHorse = this.getDefaultTrackedHorse();
        if (defaultHorse is not null)
        {
            this.TrackHorse(defaultHorse);
        }
    }

    // ----------------------------
    // 追跡中の馬がまだ有効に存在しているか確認する
    // ----------------------------
    private bool IsTrackedHorseStillValid()
    {
        if (this.trackedHorse is null)
        {
            return false;
        }

        if (Game1.player.mount is Horse mountedHorse)
        {
            if (this.IsHorseDisabled(mountedHorse))
            {
                GameLocation? trackedHorseLocation = this.trackedHorse.currentLocation;
                if (trackedHorseLocation is null)
                {
                    return false;
                }

                return trackedHorseLocation.characters.Contains(this.trackedHorse)
                    || ReferenceEquals(this.trackedHorse, mountedHorse);
            }

            return ReferenceEquals(this.trackedHorse, mountedHorse);
        }

        GameLocation? location = this.trackedHorse.currentLocation;
        if (location is null)
        {
            return false;
        }

        return location.characters.Contains(this.trackedHorse);
    }

    // ----------------------------
    // 追跡中の馬情報をクリアする
    // ----------------------------
    private void ClearTrackedHorse()
    {
        this.ReleaseTrackedHorseCollision();
        this.trackedHorse = null;
        this.state = FollowState.Idle;
        this.horseSummonedThisTick = false;
        this.horseFluteWarpRequested = false;
        this.InvalidatePath();
        this.warpCoordinator.ResetRetryState();
    }

    // ----------------------------
    // 現在騎乗中の有効な馬を返す
    // ----------------------------
    private Horse? GetEligibleMountedHorse()
    {
        return Game1.player.mount is Horse mountedHorse && !this.IsHorseDisabled(mountedHorse)
            ? mountedHorse
            : null;
    }

    // ----------------------------
    // 現在追跡中の馬の衝突設定を解除する
    // ----------------------------
    private void ReleaseTrackedHorseCollision()
    {
        if (this.trackedHorse is null)
        {
            return;
        }

        this.trackedHorse.farmerPassesThrough = false;
    }

    // ----------------------------
    // この馬が同行対象外か判定する
    // ----------------------------
    private bool IsHorseDisabled(Horse horse)
    {
        return this.isHorseDisabled(HorseMountRegistry.GetHorseIdKey(horse));
    }

    // ----------------------------
    // 馬を追跡対象にする
    // ----------------------------
    private void TrackHorse(Horse horse)
    {
        if (this.IsHorseDisabled(horse))
        {
            return;
        }

        if (ReferenceEquals(this.trackedHorse, horse))
        {
            this.ApplyCollisionPreferences(horse);
            return;
        }

        this.ReleaseTrackedHorseCollision();
        this.trackedHorse = horse;
        this.ApplyCollisionPreferences(horse);
        this.movementService.OnTrackedHorseChanged(horse);
        this.InvalidatePath();
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
    // 経路を作り直すか判定する
    // ----------------------------
    private bool ShouldRebuildPath(long nowMs, Point targetTile)
    {
        if (this.trackedHorse is null)
        {
            return false;
        }

        // ----------------------------
        // 設定時間以内は再探索しない
        // ----------------------------
        if (this.movementService.IsPathFinished())
        {
            return nowMs - this.lastPathBuildTimeMs >= this.GetPathRebuildMilliseconds();
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
            return nowMs - this.lastPathBuildTimeMs >= this.GetPathRebuildMilliseconds();
        }

        return false;
    }

    // ----------------------------
    // 経路を再計算する
    // ----------------------------
    private bool RebuildPath(Point targetTile, long nowMs)
    {
        if (this.trackedHorse is null)
        {
            return false;
        }

        Point startTile = this.trackedHorse.TilePoint;
        this.DebugLog($"[PathDebug] rebuild_start start={startTile} goal={targetTile}");

        List<Point>? path = this.pathfinder.BuildPath(this.trackedHorse.currentLocation, this.trackedHorse, startTile, targetTile, out int expanded);
        if (path is null || path.Count == 0)
        {
            this.DebugLog($"[PathDebug] rebuild_failed start={startTile} goal={targetTile} expanded={expanded}");
            this.movementService.SetPath(null);
            this.lastPathBuildTimeMs = nowMs;
            return false;
        }

        this.DebugLog($"[PathDebug] rebuild_success start={startTile} goal={targetTile} nodes={path.Count} expanded={expanded}");
        this.movementService.SetPath(path);
        this.lastPathBuildTimeMs = nowMs;
        this.lastTargetTile = targetTile;
        return true;
    }

    // ----------------------------
    // ホースフルートや騎乗後の自動同行ONを試す
    // ----------------------------
    private void TryAutoEnableFollow()
    {
        if (this.followEnabled || !this.getConfig().AutoEnableFollowOnMountOrFlute || this.trackedHorse is null)
        {
            return;
        }

        bool ridingEligibleHorse = this.GetEligibleMountedHorse() is not null;
        if (!ridingEligibleHorse && !this.horseSummonedThisTick)
        {
            return;
        }

        this.warpCoordinator.ResetRetryState();
        this.ResetPathFailureState();
        this.followEnabled = true;
        this.DebugLog(ridingEligibleHorse
            ? "[Follow] follow_enabled reason=mount"
            : "[Follow] follow_enabled reason=flute");
        this.ShowMessage("message.follow_enabled");
        if (!this.pauseFollowUntilWarp)
        {
            this.EnterFollowPending();
        }
    }

    // ----------------------------
    // 経路未発見の再試行待機を設定する
    // ----------------------------
    private void SetNoPathRetryWait()
    {
        this.noPathRetryPlayerTile = Game1.player.TilePoint;
        this.noPathRetryLocationName = Game1.player.currentLocation.NameOrUniqueName;
    }

    // ----------------------------
    // 経路未発見の再試行待機を解除する
    // ----------------------------
    private void ClearNoPathRetryWait()
    {
        this.noPathRetryPlayerTile = null;
        this.noPathRetryLocationName = null;
    }

    // ----------------------------
    // 経路未発見の再試行待機中か確認する
    // ----------------------------
    private bool IsWaitingForNoPathRetry()
    {
        if (this.noPathRetryPlayerTile is null || this.noPathRetryLocationName is null)
        {
            return false;
        }

        if (this.GetEligibleMountedHorse() is not null || this.horseSummonedThisTick || this.noPathRetryLocationName != Game1.player.currentLocation.NameOrUniqueName)
        {
            this.warpCoordinator.ResetRetryState();
            this.ResetPathFailureState();
            return false;
        }

        return this.GetManhattanDistance(this.noPathRetryPlayerTile.Value, Game1.player.TilePoint) < 8;
    }

    // ----------------------------
    // 打ち切り後のロケーション移動待ち中か確認する
    // ----------------------------
    private bool IsWaitingForSuspendedLocationChange()
    {
        if (this.suspendedLocationName is null)
        {
            return false;
        }

        if (this.GetEligibleMountedHorse() is not null || this.horseSummonedThisTick || this.suspendedLocationName != Game1.player.currentLocation.NameOrUniqueName)
        {
            this.warpCoordinator.ResetRetryState();
            this.ResetPathFailureState();
            return false;
        }

        return true;
    }

    // ----------------------------
    // 打ち切り / 経路なし時の処理を行う
    // ----------------------------
    private void HandlePathFailureAction()
    {
        this.ClearNoPathRetryWait();

        switch (this.getConfig().PathFailureAction)
        {
            case 1:
                this.DebugLog("[Follow] path_failure_action=warp");
                if (this.TryWarpHorseToPlayer(playSummonEffects: true))
                {
                    return;
                }

                this.DebugLog("[Follow] warp_retry_pending");
                this.state = FollowState.WarpBlocked;
                return;

            case 2:
                this.ResetPathFailureState();
                this.followEnabled = false;
                this.state = FollowState.Idle;
                this.InvalidatePath();
                if (this.trackedHorse is not null)
                {
                    this.StopHorse(this.trackedHorse);
                }

                this.DebugLog("[Follow] follow_disabled reason=path_failure_action");
                this.ShowMessage("message.follow_disabled");
                return;

            case 3:
                this.ClearNoPathRetryWait();
                this.suspendedLocationName = null;
                this.state = FollowState.MovementSuppressed;
                if (this.trackedHorse is not null)
                {
                    this.StopHorse(this.trackedHorse);
                }

                this.DebugLog("[Follow] movement_suppressed reason=path_failure_action");
                return;

            default:
                this.suspendedLocationName = Game1.player.currentLocation.NameOrUniqueName;
                this.state = FollowState.Paused;
                this.InvalidatePath();
                if (this.trackedHorse is not null)
                {
                    this.StopHorse(this.trackedHorse);
                }

                this.DebugLog($"[Follow] paused reason=wait_for_location_change location={this.suspendedLocationName}");
                return;
        }
    }

    // ----------------------------
    // 経路が見つからなかった時の再試行 / 打ち切りを処理する
    // ----------------------------
    private void HandleNoPathFailure()
    {
        this.noPathFailureCount++;
        if (this.noPathFailureCount < 2)
        {
            this.SetNoPathRetryWait();
            this.state = FollowState.Paused;
            this.InvalidatePath();
            if (this.trackedHorse is not null)
            {
                this.StopHorse(this.trackedHorse);
            }

            this.DebugLog($"[Follow] paused reason=no_path_retry_wait count={this.noPathFailureCount} playerTile={Game1.player.TilePoint}");
            return;
        }

        this.DebugLog($"[Follow] no_path_retry_exhausted count={this.noPathFailureCount}");
        this.HandlePathFailureAction();
    }

    // ----------------------------
    // 打ち切り / 再試行状態を初期化する
    // ----------------------------
    private void ResetPathFailureState()
    {
        this.ClearNoPathRetryWait();
        this.noPathFailureCount = 0;
        this.suspendedLocationName = null;
    }

    // ----------------------------
    // 2点のマンハッタン距離を返す
    // ----------------------------
    private int GetManhattanDistance(Point a, Point b)
    {
        return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
    }

    // ----------------------------
    // 現在ロケーションで追従を停止するか更新する
    // ----------------------------
    private void RefreshLocationFollowPauseState()
    {
        GameLocation? currentLocation = Game1.player.currentLocation;
        this.pauseFollowUntilWarp = currentLocation is null || !currentLocation.IsOutdoors;

        if (!this.pauseFollowUntilWarp)
        {
            return;
        }

        this.warpCoordinator.ResetRetryState();
        this.InvalidatePath();

        if (this.trackedHorse is not null)
        {
            this.StopHorse(this.trackedHorse);
        }
    }

    // ----------------------------
    // デバッグログを出力する
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
    // 衝突設定を反映する
    // ----------------------------
    private void ApplyCollisionPreferences(Horse horse)
    {
        horse.farmerPassesThrough = this.getConfig().IgnorePlayerCollision;
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