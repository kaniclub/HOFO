using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬の移動を管理する
// - 経路に沿った移動
// - 歩行アニメの更新
// - 停止と向きの維持
// ----------------------------
internal sealed class HorseMovementService
{
    private readonly System.Func<ModConfig> getConfig;
    private readonly HorseFollowTargetResolver targetResolver;

    private List<Point>? currentPath;
    private int currentPathIndex;
    private int baseHorseSpeed = 4;
    private int lastFacingDirection = 2;
    private int currentMoveDirection = -1;
    private int currentAnimationDirection = -1;
    private int currentAnimationIntervalMs = -1;
    private long lastHorseMoveTimeMs;
    private Point? lastHorseTile;

    // ----------------------------
    // 移動管理クラスを初期化する
    // ----------------------------
    public HorseMovementService(System.Func<ModConfig> getConfig, HorseFollowTargetResolver targetResolver)
    {
        this.getConfig = getConfig;
        this.targetResolver = targetResolver;
        this.Reset();
    }

    // ----------------------------
    // 状態を初期化する
    // ----------------------------
    public void Reset()
    {
        this.currentPath = null;
        this.currentPathIndex = 0;
        this.baseHorseSpeed = 4;
        this.lastFacingDirection = 2;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
        this.lastHorseMoveTimeMs = 0;
        this.lastHorseTile = null;
    }

    // ----------------------------
    // 経路がないか確認する
    // ----------------------------
    public bool HasNoPath()
    {
        return this.currentPath is null;
    }

    // ----------------------------
    // 経路の終端に達したか確認する
    // ----------------------------
    public bool IsPathFinished()
    {
        return this.currentPath is null || this.currentPathIndex >= this.currentPath.Count;
    }

    // ----------------------------
    // 最終移動時刻を返す
    // ----------------------------
    public long GetLastHorseMoveTimeMs()
    {
        return this.lastHorseMoveTimeMs;
    }

    // ----------------------------
    // 経路を設定する
    // ----------------------------
    public void SetPath(List<Point>? path)
    {
        this.currentPath = path;
        this.currentPathIndex = 0;
    }

    // ----------------------------
    // 経路情報を消す
    // ----------------------------
    public void InvalidatePath()
    {
        this.currentPath = null;
        this.currentPathIndex = 0;
    }

    // ----------------------------
    // 追跡対象の馬が切り替わったときの状態を反映する
    // ----------------------------
    public void OnTrackedHorseChanged(Horse horse)
    {
        this.baseHorseSpeed = System.Math.Max(1, horse.speed);
        this.lastHorseTile = horse.TilePoint;
        this.lastHorseMoveTimeMs = System.Environment.TickCount64;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
    }

    // ----------------------------
    // 騎乗中の移動状態を整える
    // ----------------------------
    public void PrepareMountedState(Horse horse)
    {
        this.currentPath = null;
        this.currentPathIndex = 0;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
        this.lastFacingDirection = horse.FacingDirection;
    }

    // ----------------------------
    // 降馬直後の向きを同期する
    // ----------------------------
    public void SyncHorseFacingAfterDismount(Horse horse, int facingDirection)
    {
        horse.faceDirection(facingDirection);
        this.lastFacingDirection = facingDirection;
        this.currentMoveDirection = -1;
        this.currentAnimationDirection = -1;
        this.currentAnimationIntervalMs = -1;
    }

    // ----------------------------
    // 馬の移動時刻を更新する
    // ----------------------------
    public void UpdateHorseMovementStamp(Horse horse)
    {
        Point currentTile = horse.TilePoint;
        if (this.lastHorseTile is null || this.lastHorseTile.Value != currentTile)
        {
            this.lastHorseTile = currentTile;
            this.lastHorseMoveTimeMs = System.Environment.TickCount64;
        }
    }

    // ----------------------------
    // 経路に沿って移動する
    // ----------------------------
    public void MoveAlongPath(Horse horse, long nowMs, GameTime time, long lastPathBuildTimeMs, long pathRebuildMilliseconds)
    {
        if (this.currentPath is null || this.currentPathIndex >= this.currentPath.Count)
        {
            return;
        }

        Point waypointTile = this.currentPath[this.currentPathIndex];
        Vector2 waypointPosition = this.targetResolver.GetRawPositionForTile(horse, waypointTile);
        Vector2 currentPosition = horse.Position;
        Vector2 delta = waypointPosition - currentPosition;
        float step = this.GetMovementStep(Vector2.Distance(horse.Tile, Game1.player.Tile));

        if (System.Math.Abs(delta.X) <= step && System.Math.Abs(delta.Y) <= step)
        {
            horse.Position = waypointPosition;
            this.currentPathIndex++;
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        bool prioritizeHorizontal = System.Math.Abs(delta.X) >= System.Math.Abs(delta.Y);
        if (this.TryMoveStep(horse, delta, step, prioritizeHorizontal, time, out bool movedPrimary) && movedPrimary)
        {
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        if (this.TryMoveStep(horse, delta, step, !prioritizeHorizontal, time, out bool movedSecondary) && movedSecondary)
        {
            this.lastHorseMoveTimeMs = nowMs;
            return;
        }

        this.StopHorse(horse);
        if (nowMs - lastPathBuildTimeMs >= pathRebuildMilliseconds)
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
                if (this.targetResolver.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
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
                if (this.targetResolver.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
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
                if (this.targetResolver.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
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
                if (this.targetResolver.CanOccupyRawPosition(horse.currentLocation, horse, candidate))
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
    // 馬を停止させる
    // ----------------------------
    public void StopHorse(Horse horse)
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
}
