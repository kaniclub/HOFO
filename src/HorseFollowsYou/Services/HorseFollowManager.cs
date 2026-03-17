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

    private Horse? trackedHorse;
    private FollowState state = FollowState.Idle;
    private bool followEnabled;
    private bool wasMountedLastTick;
    private long followPendingUntilMs;
    private Point? lastPlayerTile;
    private Point lastPlayerMoveDirection = Point.Zero;
    private long lastPathBuildTimeMs;
    private long lastHorseMoveTimeMs;
    private Point? lastHorseTile;
    private Point? lastTargetTile;
    private List<Point>? currentPath;
    private int currentPathIndex;
    private int baseHorseSpeed = 4;
    private int lastFacingDirection = 2;
    private int currentMoveDirection = -1;
    private int currentAnimationDirection = -1;
    private int currentAnimationIntervalMs = -1;

    // ----------------------------
    // 管理クラスを初期化する
    // ----------------------------
    public HorseFollowManager(ITranslationHelper translation, System.Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.translation = translation;
        this.getConfig = getConfig;
        this.warpCoordinator = new HorseWarpCoordinator(getConfig, monitor);
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
        this.lastHorseMoveTimeMs = 0;
        this.lastHorseTile = null;
        this.lastTargetTile = null;
        this.currentPath = null;
        this.currentPathIndex = 0;
        this.baseHorseSpeed = 4;
        this.lastFacingDirection = 2;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
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

        if (playerDistance < this.getConfig().FollowStartDistance && this.currentPath is null)
        {
            this.state = FollowState.ArrivedIdle;
            this.StopHorse(this.trackedHorse);
            return;
        }

        if (this.ShouldRebuildPath(nowMs, targetTile.Value))
        {
            this.RebuildPath(targetTile.Value, nowMs);
        }

        if (this.currentPath is null || this.currentPathIndex >= this.currentPath.Count)
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
        this.currentPath = null;
        this.currentPathIndex = 0;
        this.lastTargetTile = null;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;

        if (Game1.player.mount is Horse horse)
        {
            this.TrackHorse(horse);
            this.lastFacingDirection = horse.FacingDirection;
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
        this.trackedHorse.faceDirection(facingDirection);
        this.lastFacingDirection = facingDirection;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
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
            this.baseHorseSpeed = System.Math.Max(1, horse.speed);
            this.lastHorseTile = horse.TilePoint;
            this.lastHorseMoveTimeMs = System.Environment.TickCount64;
            this.currentMoveDirection = -1;
            this.currentAnimationDirection = -1;
            this.currentAnimationIntervalMs = -1;
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

        Point currentTile = this.trackedHorse.TilePoint;
        if (this.lastHorseTile is null || this.lastHorseTile.Value != currentTile)
        {
            this.lastHorseTile = currentTile;
            this.lastHorseMoveTimeMs = System.Environment.TickCount64;
        }
    }

    // ----------------------------
    // 馬の目標タイルを決める
    // ----------------------------
    private Point? ResolveFollowTargetTile(Horse horse, Farmer player)
    {
        Point playerTile = player.TilePoint;
        Point back = this.GetPreferredBackDirection(player);
        Point left = new(-back.Y, back.X);
        Point right = new(back.Y, -back.X);

        foreach (Point candidate in this.GetTargetCandidates(playerTile, back, left, right))
        {
            if (candidate == playerTile)
            {
                continue;
            }

            if (!this.IsInsideMap(player.currentLocation, candidate))
            {
                continue;
            }

            if (this.CanOccupyTile(player.currentLocation, horse, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ----------------------------
    // 候補タイルを順番に返す
    // ----------------------------
    private IEnumerable<Point> GetTargetCandidates(Point playerTile, Point back, Point left, Point right)
    {
        yield return this.Offset(playerTile, back, 2);
        yield return this.Offset(playerTile, back, 1);
        yield return this.Offset(this.Offset(playerTile, back, 2), left, 1);
        yield return this.Offset(this.Offset(playerTile, back, 2), right, 1);
        yield return this.Offset(this.Offset(playerTile, back, 1), left, 1);
        yield return this.Offset(this.Offset(playerTile, back, 1), right, 1);
        yield return this.Offset(playerTile, left, 1);
        yield return this.Offset(playerTile, right, 1);
    }

    // ----------------------------
    // プレイヤー後方の向きを返す
    // ----------------------------
    private Point GetPreferredBackDirection(Farmer player)
    {
        if (this.lastPlayerMoveDirection != Point.Zero)
        {
            return new Point(-this.lastPlayerMoveDirection.X, -this.lastPlayerMoveDirection.Y);
        }

        return player.FacingDirection switch
        {
            0 => new Point(0, 1),
            1 => new Point(-1, 0),
            2 => new Point(0, -1),
            3 => new Point(1, 0),
            _ => new Point(0, 1),
        };
    }

    // ----------------------------
    // 座標をずらした位置を返す
    // ----------------------------
    private Point Offset(Point origin, Point direction, int distance)
    {
        return new Point(origin.X + direction.X * distance, origin.Y + direction.Y * distance);
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

        if (this.currentPath is null || this.currentPathIndex >= this.currentPath.Count)
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

        if (nowMs - this.lastHorseMoveTimeMs >= this.GetPathRebuildMilliseconds() * 2)
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

        List<Point>? path = this.BuildPath(this.trackedHorse.currentLocation, this.trackedHorse, this.trackedHorse.TilePoint, targetTile);
        if (path is null || path.Count == 0)
        {
            this.currentPath = null;
            this.currentPathIndex = 0;
            this.lastPathBuildTimeMs = nowMs;
            return;
        }

        this.currentPath = path;
        this.currentPathIndex = 0;
        this.lastPathBuildTimeMs = nowMs;
        this.lastTargetTile = targetTile;
    }

    // ----------------------------
    // 経路を探索する
    // ----------------------------
    private List<Point>? BuildPath(GameLocation location, Horse horse, Point start, Point goal)
    {
        if (start == goal)
        {
            return new List<Point>();
        }

        Queue<Point> open = new();
        Dictionary<Point, Point?> cameFrom = new();
        open.Enqueue(start);
        cameFrom[start] = null;

        int expanded = 0;
        const int maxExpanded = 2500;

        while (open.Count > 0 && expanded < maxExpanded)
        {
            Point current = open.Dequeue();
            expanded++;

            foreach (Point next in this.GetNeighbors(current))
            {
                if (cameFrom.ContainsKey(next))
                {
                    continue;
                }

                if (!this.IsInsideMap(location, next))
                {
                    continue;
                }

                if (next != goal && !this.CanOccupyTile(location, horse, next))
                {
                    continue;
                }

                cameFrom[next] = current;
                if (next == goal)
                {
                    return this.ReconstructPath(cameFrom, goal);
                }

                open.Enqueue(next);
            }
        }

        return null;
    }

    // ----------------------------
    // 隣接4方向を返す
    // ----------------------------
    private IEnumerable<Point> GetNeighbors(Point point)
    {
        yield return new Point(point.X, point.Y - 1);
        yield return new Point(point.X + 1, point.Y);
        yield return new Point(point.X, point.Y + 1);
        yield return new Point(point.X - 1, point.Y);
    }

    // ----------------------------
    // 探索結果から経路を作る
    // ----------------------------
    private List<Point> ReconstructPath(Dictionary<Point, Point?> cameFrom, Point end)
    {
        List<Point> reversed = new();
        Point? current = end;
        while (current is not null && cameFrom.TryGetValue(current.Value, out Point? previous))
        {
            reversed.Add(current.Value);
            current = previous;
        }

        reversed.Reverse();
        if (reversed.Count > 0)
        {
            reversed.RemoveAt(0);
        }

        return reversed;
    }

    // ----------------------------
    // 経路に沿って移動する
    // ----------------------------
    private void MoveAlongPath(long nowMs, GameTime time)
    {
        if (this.trackedHorse is null || this.currentPath is null || this.currentPathIndex >= this.currentPath.Count)
        {
            return;
        }

        Point waypointTile = this.currentPath[this.currentPathIndex];
        Vector2 waypointPosition = this.GetRawPositionForTile(this.trackedHorse, waypointTile);
        Vector2 currentPosition = this.trackedHorse.Position;
        Vector2 delta = waypointPosition - currentPosition;
        float step = this.GetMovementStep(Vector2.Distance(this.trackedHorse.Tile, Game1.player.Tile));

        if (System.Math.Abs(delta.X) <= step && System.Math.Abs(delta.Y) <= step)
        {
            this.trackedHorse.Position = waypointPosition;
            this.currentPathIndex++;
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        bool prioritizeHorizontal = System.Math.Abs(delta.X) >= System.Math.Abs(delta.Y);
        if (this.TryMoveStep(this.trackedHorse, delta, step, prioritizeHorizontal, time, out bool movedPrimary) && movedPrimary)
        {
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        if (this.TryMoveStep(this.trackedHorse, delta, step, !prioritizeHorizontal, time, out bool movedSecondary) && movedSecondary)
        {
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        this.StopHorse(this.trackedHorse);
        if (nowMs - this.lastPathBuildTimeMs >= this.GetPathRebuildMilliseconds())
        {
            this.InvalidatePath();
        }
    }

    // ----------------------------
    // 1歩だけ移動できるか試す
    // ----------------------------
    private bool TryMoveStep(Horse horse, Vector2 delta, float step, bool horizontalFirst, GameTime time, out bool moved)
    {
        moved = false;

        if (horizontalFirst)
        {
            if (System.Math.Abs(delta.X) > 0.5f)
            {
                int dir = delta.X >= 0 ? 1 : 3;
                Vector2 candidate = horse.Position + new Vector2(System.Math.Sign(delta.X) * step, 0f);
                if (this.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
                {
                    this.ApplyManualMove(horse, candidate, dir, time, step);
                    moved = true;
                    return true;
                }
            }

            if (System.Math.Abs(delta.Y) > 0.5f)
            {
                int dir = delta.Y >= 0 ? 2 : 0;
                Vector2 candidate = horse.Position + new Vector2(0f, System.Math.Sign(delta.Y) * step);
                if (this.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
                {
                    this.ApplyManualMove(horse, candidate, dir, time, step);
                    moved = true;
                    return true;
                }
            }
        }
        else
        {
            if (System.Math.Abs(delta.Y) > 0.5f)
            {
                int dir = delta.Y >= 0 ? 2 : 0;
                Vector2 candidate = horse.Position + new Vector2(0f, System.Math.Sign(delta.Y) * step);
                if (this.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
                {
                    this.ApplyManualMove(horse, candidate, dir, time, step);
                    moved = true;
                    return true;
                }
            }

            if (System.Math.Abs(delta.X) > 0.5f)
            {
                int dir = delta.X >= 0 ? 1 : 3;
                Vector2 candidate = horse.Position + new Vector2(System.Math.Sign(delta.X) * step, 0f);
                if (this.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
                {
                    this.ApplyManualMove(horse, candidate, dir, time, step);
                    moved = true;
                    return true;
                }
            }
        }

        return false;
    }

    // ----------------------------
    // 馬を手動で1歩動かす
    // ----------------------------
    private void ApplyManualMove(Horse horse, Vector2 newPosition, int facingDirection, GameTime time, float step)
    {
        this.SetMoveDirection(horse, facingDirection);

        if (this.lastFacingDirection != facingDirection)
        {
            horse.faceDirection(facingDirection);
            this.lastFacingDirection = facingDirection;
        }

        horse.Position = newPosition;
        this.AdvanceWalkAnimation(horse, facingDirection, time, step);
    }

    // ----------------------------
    // 移動方向を切り替える
    // ----------------------------
    private void SetMoveDirection(Horse horse, int facingDirection)
    {
        if (this.currentMoveDirection == facingDirection)
        {
            return;
        }

        // ----------------------------
        // 向き変更時は前の移動状態を完全に止めてから切り替える
        // ----------------------------
        horse.Halt();
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;

        switch (facingDirection)
        {
            case 0:
                horse.SetMovingUp(true);
                break;
            case 1:
                horse.SetMovingRight(true);
                break;
            case 2:
                horse.SetMovingDown(true);
                break;
            case 3:
                horse.SetMovingLeft(true);
                break;
        }

        this.currentMoveDirection = facingDirection;
    }

    // ----------------------------
    // 歩きアニメを進める
    // ----------------------------
    private void AdvanceWalkAnimation(Horse horse, int facingDirection, GameTime time, float step)
    {
        int frameDurationMs = this.GetWalkFrameDuration(step);
        this.EnsureWalkAnimation(horse, facingDirection, frameDurationMs);
        horse.Sprite.animateOnce(time);
    }

    // ----------------------------
    // 歩きフレーム時間を返す
    // ----------------------------
    private int GetWalkFrameDuration(float step)
    {
        return System.Math.Clamp(150 - (int)System.Math.Round(step * 8f), 70, 150);
    }

    // ----------------------------
    // 歩きアニメを必要なら作り直す
    // ----------------------------
    private void EnsureWalkAnimation(Horse horse, int facingDirection, int frameDurationMs)
    {
        if (horse.Sprite.CurrentAnimation is not null
            && this.currentAnimationDirection == facingDirection
            && this.currentAnimationIntervalMs == frameDurationMs)
        {
            return;
        }

        horse.Sprite.loop = true;
        horse.Sprite.setCurrentAnimation(this.CreateWalkAnimation(facingDirection, frameDurationMs));
        this.currentAnimationDirection = facingDirection;
        this.currentAnimationIntervalMs = frameDurationMs;
    }

    // ----------------------------
    // 歩きアニメを作る
    // ----------------------------
    private List<FarmerSprite.AnimationFrame> CreateWalkAnimation(int facingDirection, int frameDurationMs)
    {
        int[] frames = facingDirection switch
        {
            0 => new[] { 15, 16, 17, 18, 19, 20 },
            1 => new[] { 8, 9, 10, 11, 12, 13 },
            2 => new[] { 1, 2, 3, 4, 5, 6 },
            3 => new[] { 8, 9, 10, 11, 12, 13 },
            _ => new[] { 1, 2, 3, 4, 5, 6 },
        };

        List<FarmerSprite.AnimationFrame> animation = new(frames.Length);
        foreach (int frame in frames)
        {
            animation.Add(new FarmerSprite.AnimationFrame(frame, frameDurationMs));
        }

        return animation;
    }

    // ----------------------------
    // そのタイルに入れるか確認する
    // ----------------------------
    private bool CanOccupyTile(GameLocation location, Horse horse, Point tile)
    {
        return this.CanOccupyRawPosition(location, horse, this.GetRawPositionForTile(horse, tile));
    }

    // ----------------------------
    // その位置に入れるか確認する
    // ----------------------------
    private bool CanOccupyRawPosition(GameLocation location, Horse horse, Vector2 rawPosition)
    {
        Rectangle rect = this.GetCollisionRect(rawPosition);

        Rectangle bbox = this.ClampRectToMap(location, rect);
        if (bbox.Width <= 0 || bbox.Height <= 0)
        {
            return false;
        }

        foreach (Point tile in this.GetOccupiedTiles(bbox))
        {
            if (!this.IsInsideMap(location, tile))
            {
                return false;
            }

            Vector2 tileVector = new(tile.X, tile.Y);
            if (!location.isTilePassable(tileVector))
            {
                return false;
            }

            if (location.objects.TryGetValue(tileVector, out StardewValley.Object? obj) && !obj.isPassable())
            {
                return false;
            }

            if (location.terrainFeatures.TryGetValue(tileVector, out StardewValley.TerrainFeatures.TerrainFeature? feature) && !feature.isPassable())
            {
                return false;
            }

            if (location.getBuildingAt(tileVector) is not null)
            {
                return false;
            }
        }

        foreach (var character in location.characters)
        {
            if (ReferenceEquals(character, horse))
            {
                continue;
            }

            if (rect.Intersects(character.GetBoundingBox()))
            {
                return false;
            }
        }

        if (!this.getConfig().IgnorePlayerCollision)
        {
            foreach (Farmer farmer in location.farmers)
            {
                if (rect.Intersects(farmer.GetBoundingBox()))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ----------------------------
    // 衝突設定を反映する
    // ----------------------------
    private void ApplyCollisionPreferences(Horse horse)
    {
        horse.farmerPassesThrough = this.getConfig().IgnorePlayerCollision;
    }

    // ----------------------------
    // 当たり判定を作る
    // ----------------------------
    private Rectangle GetCollisionRect(Vector2 rawPosition)
    {
        int width = this.GetCollisionWidth();
        int offsetX = this.GetCollisionOffsetX(width);
        return new Rectangle((int)rawPosition.X + offsetX, (int)rawPosition.Y + 16, width, 32);
    }

    // ----------------------------
    // 経路計算用の当たり判定幅を返す
    // ----------------------------
    private int GetCollisionWidth()
    {
        return this.getConfig().UseNarrowHitbox ? 32 : 48;
    }

    // ----------------------------
    // 当たり判定の X オフセットを返す
    // ----------------------------
    private int GetCollisionOffsetX(int width)
    {
        return 8 + ((48 - width) / 2);
    }

    // ----------------------------
    // マップ内に収まる矩形へ変換する
    // ----------------------------
    private Rectangle ClampRectToMap(GameLocation location, Rectangle rect)
    {
        int mapWidth = location.Map.Layers[0].LayerWidth * 64;
        int mapHeight = location.Map.Layers[0].LayerHeight * 64;
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > mapWidth || rect.Bottom > mapHeight)
        {
            return Rectangle.Empty;
        }

        return rect;
    }

    // ----------------------------
    // 矩形が触れるタイルを列挙する
    // ----------------------------
    private IEnumerable<Point> GetOccupiedTiles(Rectangle rect)
    {
        int left = rect.Left / 64;
        int right = (rect.Right - 1) / 64;
        int top = rect.Top / 64;
        int bottom = (rect.Bottom - 1) / 64;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                yield return new Point(x, y);
            }
        }
    }

    // ----------------------------
    // タイルの生座標を返す
    // ----------------------------
    private Vector2 GetRawPositionForTile(Horse horse, Point tile)
    {
        int visualWidth = horse.GetBoundingBox().Width;
        return new Vector2(tile.X * 64f + 32f - visualWidth / 2f, tile.Y * 64f + 4f);
    }

    // ----------------------------
    // 距離に応じた移動量を返す
    // ----------------------------
    private float GetMovementStep(float distanceToPlayer)
    {
        float multiplier = distanceToPlayer switch
        {
            >= 8f => this.getConfig().FarSpeedMultiplier,
            >= 4f => this.getConfig().MidSpeedMultiplier,
            _ => this.getConfig().NearSpeedMultiplier,
        };

        return System.Math.Max(1f, this.baseHorseSpeed * multiplier);
    }

    // ----------------------------
    // 経路再計算の間隔を返す
    // ----------------------------
    private long GetPathRebuildMilliseconds()
    {
        return (long)System.Math.Round(this.getConfig().PathRebuildSeconds * 1000f);
    }

    // ----------------------------
    // タイルがマップ内か確認する
    // ----------------------------
    private bool IsInsideMap(GameLocation location, Point tile)
    {
        return tile.X >= 0
            && tile.Y >= 0
            && tile.X < location.Map.Layers[0].LayerWidth
            && tile.Y < location.Map.Layers[0].LayerHeight;
    }

    // ----------------------------
    // 馬を停止させる
    // ----------------------------
    private void StopHorse(Horse horse)
    {
        int facingDirection = this.currentMoveDirection >= 0
            ? this.currentMoveDirection
            : this.lastFacingDirection;

        if (this.currentMoveDirection == -1
            && this.currentAnimationDirection == -1
            && horse.controller is null)
        {
            horse.faceDirection(facingDirection);
            this.ApplyIdleFrame(horse, facingDirection);
            this.lastFacingDirection = facingDirection;
            return;
        }

        horse.controller = null;
        horse.Halt();
        horse.faceDirection(facingDirection);
        this.ApplyIdleFrame(horse, facingDirection);
        this.lastFacingDirection = facingDirection;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
    }

    // ----------------------------
    // 停止時に待機フレームへそろえる
    // ----------------------------
    private void ApplyIdleFrame(Horse horse, int facingDirection)
    {
        int frame = this.GetIdleFrame(facingDirection);

        horse.Sprite.StopAnimation();
        horse.Sprite.loop = false;
        horse.Sprite.CurrentFrame = frame;
    }

    // ----------------------------
    // 向きごとの待機フレームを返す
    // ----------------------------
    private int GetIdleFrame(int facingDirection)
    {
        return facingDirection switch
        {
            0 => 14,
            1 => 7,
            2 => 0,
            3 => 7,
            _ => 0,
        };
    }

    // ----------------------------
    // 経路情報を消す
    // ----------------------------
    private void InvalidatePath()
    {
        this.currentPath = null;
        this.currentPathIndex = 0;
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