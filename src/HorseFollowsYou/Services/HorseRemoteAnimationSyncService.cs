using StardewModdingAPI;
using HorseFollowsYou;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// マルチプレイで同期された馬の見た目を補正する
// - 位置同期済みの馬から移動方向を推定
// - バニラ騎乗時と同じフレーム列で歩行アニメを再生
// ----------------------------
internal sealed class HorseRemoteAnimationSyncService
{
    private sealed class ObservedHorseState
    {
        public Vector2 LastPosition { get; set; }
        public int LastFacingDirection { get; set; }
        public bool WasMoving { get; set; }
        public long LastMovementSeenAtMs { get; set; }
        public bool IsInWalkLinger { get; set; }
        public string? LastLocationName { get; set; }
    }

    private readonly HorseMountRegistry mountRegistry;
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> getConfig;
    private readonly Dictionary<string, ObservedHorseState> observedStates = new(StringComparer.OrdinalIgnoreCase);

    public HorseRemoteAnimationSyncService(HorseMountRegistry mountRegistry, IMonitor monitor, Func<ModConfig> getConfig)
    {
        this.mountRegistry = mountRegistry;
        this.monitor = monitor;
        this.getConfig = getConfig;
    }

    public void Reset()
    {
        this.observedStates.Clear();
    }

    public void Update(Func<string, bool> isClaimedHorse, Func<string, bool> isLocallyControlledHorse)
    {
        if (!Context.IsWorldReady)
        {
            this.Reset();
            return;
        }

        long nowMs = Environment.TickCount64;
        HashSet<string> activeHorseIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (Horse horse in this.mountRegistry.GetWorldHorses())
        {
            string horseId = HorseMountRegistry.GetHorseIdKey(horse);
            activeHorseIds.Add(horseId);

            if (!this.observedStates.TryGetValue(horseId, out ObservedHorseState? state))
            {
                state = new ObservedHorseState
                {
                    LastPosition = horse.Position,
                    LastFacingDirection = horse.FacingDirection,
                    WasMoving = false,
                    LastMovementSeenAtMs = nowMs,
                    IsInWalkLinger = false,
                    LastLocationName = horse.currentLocation?.NameOrUniqueName,
                };
                this.observedStates[horseId] = state;
            }

            bool claimed = isClaimedHorse(horseId);
            bool isLocal = isLocallyControlledHorse(horseId);
            if (horse.rider is not null || isLocal)
            {
                if (state.WasMoving || state.IsInWalkLinger)
                {
                }

                state.LastPosition = horse.Position;
                state.LastFacingDirection = horse.FacingDirection;
                state.WasMoving = false;
                state.LastMovementSeenAtMs = nowMs;
                state.IsInWalkLinger = false;
                state.LastLocationName = horse.currentLocation?.NameOrUniqueName;
                continue;
            }

            string? currentLocationName = horse.currentLocation?.NameOrUniqueName;
            if (!string.Equals(state.LastLocationName, currentLocationName, StringComparison.OrdinalIgnoreCase))
            {
                state.LastPosition = horse.Position;
                state.LastFacingDirection = horse.FacingDirection;
                state.WasMoving = false;
                state.LastMovementSeenAtMs = nowMs;
                state.IsInWalkLinger = false;
                state.LastLocationName = currentLocationName;
                continue;
            }

            Vector2 delta = horse.Position - state.LastPosition;
            float movedDistanceSq = delta.LengthSquared();
            if (movedDistanceSq >= 4096f)
            {
                this.ApplyIdleFrame(horse, horse.FacingDirection);
                state.LastPosition = horse.Position;
                state.LastFacingDirection = horse.FacingDirection;
                state.WasMoving = false;
                state.LastMovementSeenAtMs = nowMs;
                state.IsInWalkLinger = false;
                state.LastLocationName = currentLocationName;
                continue;
            }

            bool isMoving = movedDistanceSq >= 4f;

            if (isMoving)
            {
                int facingDirection = this.ResolveFacingDirection(delta, state.LastFacingDirection);
                if (horse.FacingDirection != facingDirection)
                {
                    horse.faceDirection(facingDirection);
                }

                this.EnsureWalkAnimation(horse, facingDirection, state.LastFacingDirection);
                horse.Sprite.animateOnce(Game1.currentGameTime);

                if (!state.WasMoving || state.LastFacingDirection != facingDirection)
                {
                }

                state.LastPosition = horse.Position;
                state.LastFacingDirection = facingDirection;
                state.WasMoving = true;
                state.LastMovementSeenAtMs = nowMs;
                state.IsInWalkLinger = false;
                state.LastLocationName = currentLocationName;
                continue;
            }

            long sinceLastMovementMs = nowMs - state.LastMovementSeenAtMs;
            if (state.WasMoving && sinceLastMovementMs <= 320)
            {
                this.EnsureWalkAnimation(horse, state.LastFacingDirection, state.LastFacingDirection);
                horse.Sprite.animateOnce(Game1.currentGameTime);

                if (!state.IsInWalkLinger)
                {
                }

                state.LastPosition = horse.Position;
                state.IsInWalkLinger = true;
                continue;
            }

            if (state.WasMoving || state.IsInWalkLinger)
            {
                this.ApplyIdleFrame(horse, state.LastFacingDirection);
            }

            state.LastPosition = horse.Position;
            state.LastFacingDirection = horse.FacingDirection;
            state.WasMoving = false;
            state.IsInWalkLinger = false;
            state.LastLocationName = currentLocationName;
        }

        foreach (string staleHorseId in this.observedStates.Keys.Except(activeHorseIds, StringComparer.OrdinalIgnoreCase).ToList())
        {
            this.observedStates.Remove(staleHorseId);
        }
    }

    private int ResolveFacingDirection(Vector2 delta, int fallbackDirection)
    {
        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            if (delta.X > 0.5f)
            {
                return 1;
            }

            if (delta.X < -0.5f)
            {
                return 3;
            }
        }
        else
        {
            if (delta.Y > 0.5f)
            {
                return 2;
            }

            if (delta.Y < -0.5f)
            {
                return 0;
            }
        }

        return fallbackDirection;
    }

    private void EnsureWalkAnimation(Horse horse, int facingDirection, int previousFacingDirection)
    {
        if (horse.Sprite.CurrentAnimation is not null && previousFacingDirection == facingDirection)
        {
            return;
        }

        horse.Sprite.loop = true;
        horse.Sprite.setCurrentAnimation(this.CreateVanillaMountedWalkAnimation(facingDirection));
    }

    private List<FarmerSprite.AnimationFrame> CreateVanillaMountedWalkAnimation(int facingDirection)
    {
        return facingDirection switch
        {
            1 => new List<FarmerSprite.AnimationFrame>
            {
                new(8, 70),
                new(9, 70),
                new(10, 70),
                new(11, 70),
                new(12, 70),
                new(13, 70),
            },
            3 => new List<FarmerSprite.AnimationFrame>
            {
                new(8, 70, secondaryArm: false, flip: true),
                new(9, 70, secondaryArm: false, flip: true),
                new(10, 70, secondaryArm: false, flip: true),
                new(11, 70, secondaryArm: false, flip: true),
                new(12, 70, secondaryArm: false, flip: true),
                new(13, 70, secondaryArm: false, flip: true),
            },
            0 => new List<FarmerSprite.AnimationFrame>
            {
                new(15, 70),
                new(16, 70),
                new(17, 70),
                new(18, 70),
                new(19, 70),
                new(20, 70),
            },
            _ => new List<FarmerSprite.AnimationFrame>
            {
                new(1, 70),
                new(2, 70),
                new(3, 70),
                new(4, 70),
                new(5, 70),
                new(6, 70),
            },
        };
    }

    private void ApplyIdleFrame(Horse horse, int facingDirection)
    {
        int frame = facingDirection switch
        {
            0 => 14,
            1 => 7,
            2 => 0,
            3 => 7,
            _ => 0,
        };

        horse.Sprite.StopAnimation();
        horse.Sprite.loop = false;
        horse.Sprite.CurrentFrame = frame;
    }
}
