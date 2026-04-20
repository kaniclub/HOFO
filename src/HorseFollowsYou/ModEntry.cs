using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using HorseFollowsYou.Integrations;
using HorseFollowsYou.Multiplayer;
using HorseFollowsYou.Services;
using HorseFollowsYou.Services.Multiplayer;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou;

// ----------------------------
// Mod の入口処理
// - 設定読込
// - イベント登録
// - GMCM 登録
// - マルチプレイの同行馬占有同期
// - マルチプレイの馬ワープ要求受信
// ----------------------------
public sealed class ModEntry : Mod
{
    private const string ClaimMessageType = "HorseClaimState";
    private const string WarpRequestMessageType = "HorseWarpRequest";

    private sealed class HorseClaimState
    {
        public long PlayerId { get; set; }
        public long StampUtcMs { get; set; }
    }

    private ModConfig config = new();
    private HorseFollowManager? followManager;
    private HorseMountRegistry mountRegistry = null!;
    private HorseRemoteAnimationSyncService remoteAnimationSyncService = null!;
    private GmcmIntegration? gmcmIntegration;
    private IReadOnlyList<HorseMountOption> cachedMountOptions = Array.Empty<HorseMountOption>();
    private readonly Dictionary<string, HorseClaimState> horseClaims = new(StringComparer.OrdinalIgnoreCase);
    private bool horseWarpEventSubscribed;

    // ----------------------------
    // Mod 起動時の初期化を行う
    // ----------------------------
    public override void Entry(IModHelper helper)
    {
        this.config = this.NormalizeConfig(helper.ReadConfig<ModConfig>());
        this.mountRegistry = new HorseMountRegistry();
        this.remoteAnimationSyncService = new HorseRemoteAnimationSyncService(this.mountRegistry, this.Monitor, () => this.config);
        this.followManager = new HorseFollowManager(
            helper.Translation,
            () => this.config,
            this.Monitor,
            this.IsHorseDisabled,
            this.IsHorseClaimedByOther,
            this.GetDefaultTrackedHorse,
            this.mountRegistry.FindHorseById,
            this.OnTrackedHorseChanged,
            this.RequestHostHorseWarp
        );

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
    }

    // ----------------------------
    // GameLaunched 時の登録処理を行う
    // ----------------------------
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.gmcmIntegration = new GmcmIntegration(
            this.Helper,
            this.ModManifest,
            this.Helper.Translation,
            () => this.config,
            value => this.config = this.NormalizeConfig(value),
            () => this.cachedMountOptions,
            this.IsHorseDisabled,
            this.SetHorseDisabled,
            this.ResetAllSettings,
            this.SaveAllSettings
        );
        this.gmcmIntegration.RegisterOrReload();
    }

    // ----------------------------
    // セーブ読込後の処理を行う
    // ----------------------------
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.horseClaims.Clear();
        this.remoteAnimationSyncService.Reset();
        this.RefreshMountOptions();
        this.gmcmIntegration?.RegisterOrReload();

        if (Game1.IsMasterGame)
        {
            HorseInstanceConsolidator.ConsolidateAllOnHost(this.mountRegistry);
        }

        this.SubscribeHorseWarpEvent();
        this.followManager?.OnSaveLoaded();
    }

    // ----------------------------
    // 毎朝一覧更新を行う
    // ----------------------------
    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.RefreshMountOptions();
        this.gmcmIntegration?.RegisterOrReload();

        if (Game1.IsMasterGame)
        {
            HorseInstanceConsolidator.ConsolidateAllOnHost(this.mountRegistry);
        }

        this.followManager?.OnMountFilterChanged();
    }

    // ----------------------------
    // タイトルへ戻ったときに状態を戻す
    // ----------------------------
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.UnsubscribeHorseWarpEvent();
        this.horseClaims.Clear();
        this.cachedMountOptions = Array.Empty<HorseMountOption>();
        this.remoteAnimationSyncService.Reset();
        this.gmcmIntegration?.RegisterOrReload();
        this.followManager?.Reset();
    }

    // ----------------------------
    // 毎 tick の更新処理を呼ぶ
    // ----------------------------
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.followManager?.OnUpdateTicked(e.Ticks);
        this.remoteAnimationSyncService.Update(this.IsHorseClaimed, this.IsHorseLocallyControlled);
    }

    // ----------------------------
    // デバッグ表示を描画する
    // ----------------------------
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (this.followManager is null || !Context.IsWorldReady || !this.config.DebugMode)
        {
            return;
        }

        IReadOnlyList<Point>? path = this.followManager.GetDebugPath();
        Point? targetTile = this.followManager.GetDebugTargetTile();

        if (path is null && targetTile is null)
        {
            return;
        }

        if (path is not null)
        {
            foreach (Point tile in path)
            {
                this.DrawDebugTile(e.SpriteBatch, tile, Color.Red * 0.22f);
            }
        }

        if (targetTile is Point goal)
        {
            this.DrawDebugTile(e.SpriteBatch, goal, Color.Black * 0.28f);
        }
    }

    // ----------------------------
    // 画面移動時の処理を呼ぶ
    // ----------------------------
    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer)
        {
            return;
        }

        string? oldLocationName = e.OldLocation?.NameOrUniqueName;
        string? newLocationName = e.NewLocation?.NameOrUniqueName;
        bool changedMap = !string.Equals(oldLocationName, newLocationName, StringComparison.OrdinalIgnoreCase);

        if (!changedMap)
        {
            return;
        }

        this.followManager?.OnWarped();
        this.followManager?.OnLocalPlayerWarpedToDifferentMap();
    }

    // ----------------------------
    // マルチプレイメッセージを受信して処理を振り分ける
    // ----------------------------
    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!e.FromModID.Equals(this.ModManifest.UniqueID, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (e.Type.Equals(ClaimMessageType, StringComparison.Ordinal))
        {
            HorseClaimMessage message = e.ReadAs<HorseClaimMessage>();
            this.ApplyHorseClaimMessage(message);
            return;
        }

        if (e.Type.Equals(WarpRequestMessageType, StringComparison.Ordinal))
        {
            HorseWarpRequestMessage message = e.ReadAs<HorseWarpRequestMessage>();
            this.ApplyHorseWarpRequestMessage(message);
        }
    }

    // ----------------------------
    // 新しく参加したプレイヤーへ現在の占有状態を送る
    // ----------------------------
    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame)
        {
            return;
        }

        foreach ((string horseId, HorseClaimState claim) in this.horseClaims)
        {
            HorseClaimMessage message = new()
            {
                HorseId = horseId,
                PlayerId = claim.PlayerId,
                StampUtcMs = claim.StampUtcMs,
                IsClaimed = true,
            };

            this.Helper.Multiplayer.SendMessage(
                message,
                ClaimMessageType,
                new[] { this.ModManifest.UniqueID },
                new[] { e.Peer.PlayerID }
            );
        }
    }

    // ----------------------------
    // 切断したプレイヤーの占有状態を解除する
    // ----------------------------
    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        foreach (string horseId in this.horseClaims
            .Where(pair => pair.Value.PlayerId == e.Peer.PlayerID)
            .Select(pair => pair.Key)
            .ToList())
        {
            this.horseClaims.Remove(horseId);
        }
    }

    // ----------------------------
    // フルートによるワープ要求イベントを購読する
    // ----------------------------
    private void SubscribeHorseWarpEvent()
    {
        if (!Context.IsWorldReady || Game1.player?.team is null || this.horseWarpEventSubscribed)
        {
            return;
        }

        Game1.player.team.requestHorseWarpEvent.onEvent += this.OnHorseWarpRequested;
        this.horseWarpEventSubscribed = true;
    }

    // ----------------------------
    // フルートによるワープ要求イベント購読を解除する
    // ----------------------------
    private void UnsubscribeHorseWarpEvent()
    {
        if (!this.horseWarpEventSubscribed || Game1.player?.team is null)
        {
            this.horseWarpEventSubscribed = false;
            return;
        }

        Game1.player.team.requestHorseWarpEvent.onEvent -= this.OnHorseWarpRequested;
        this.horseWarpEventSubscribed = false;
    }

    // ----------------------------
    // フルートによる馬ワープ要求を受け取る
    // ----------------------------
    private void OnHorseWarpRequested(long uid)
    {
        if (!Context.IsWorldReady || Game1.player is null || uid != Game1.player.UniqueMultiplayerID)
        {
            return;
        }

        this.followManager?.OnHorseFluteWarpRequested();
    }

    // ----------------------------
    // デバッグ用の色付きタイルを描画する
    // ----------------------------
    private void DrawDebugTile(SpriteBatch spriteBatch, Point tile, Color color)
    {
        Vector2 pixelPosition = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f, tile.Y * 64f));
        Rectangle destination = new((int)pixelPosition.X, (int)pixelPosition.Y, 64, 64);
        spriteBatch.Draw(Game1.staminaRect, destination, color);
    }

    // ----------------------------
    // GMCM からの全設定保存を行う
    // ----------------------------
    private void SaveAllSettings()
    {
        this.config = this.NormalizeConfig(this.config);
        this.Helper.WriteConfig(this.config);
        this.RefreshMountOptions();
        this.followManager?.OnMountFilterChanged();
    }

    // ----------------------------
    // GMCM からの全設定リセットを行う
    // ----------------------------
    private void ResetAllSettings()
    {
        string? currentSaveKey = this.GetCurrentSaveConfigKey();
        Dictionary<string, List<string>> preservedPerSave = this.config.DisabledHorseIdsBySaveId
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        if (currentSaveKey is not null)
        {
            preservedPerSave.Remove(currentSaveKey);
        }

        ModConfig resetConfig = this.NormalizeConfig(new ModConfig());
        resetConfig.DisabledHorseIdsBySaveId = preservedPerSave;
        this.config = resetConfig;

        this.RefreshMountOptions();
        this.followManager?.OnMountFilterChanged();
    }

    // ----------------------------
    // ワールド内の馬一覧を更新する
    // ----------------------------
    private void RefreshMountOptions()
    {
        this.cachedMountOptions = this.mountRegistry.GetMountOptions();
    }

    // ----------------------------
    // 同行対象外か判定する
    // ----------------------------
    private bool IsHorseDisabled(string horseId)
    {
        string? currentSaveKey = this.GetCurrentSaveConfigKey();
        if (currentSaveKey is null
            || !this.config.DisabledHorseIdsBySaveId.TryGetValue(currentSaveKey, out List<string>? disabledHorseIds))
        {
            return false;
        }

        string normalizedHorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId);
        return disabledHorseIds.Any(savedId => string.Equals(
            HorseMountRegistry.NormalizeHorseIdKey(savedId),
            normalizedHorseId,
            StringComparison.OrdinalIgnoreCase
        ));
    }

    // ----------------------------
    // 同行対象外設定を切り替える
    // ----------------------------
    private void SetHorseDisabled(string horseId, bool disabled)
    {
        string? currentSaveKey = this.GetCurrentSaveConfigKey();
        if (currentSaveKey is null)
        {
            return;
        }

        string normalizedHorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId);
        if (!this.config.DisabledHorseIdsBySaveId.TryGetValue(currentSaveKey, out List<string>? disabledHorseIds))
        {
            disabledHorseIds = new List<string>();
            this.config.DisabledHorseIdsBySaveId[currentSaveKey] = disabledHorseIds;
        }

        disabledHorseIds.RemoveAll(savedId => string.Equals(
            HorseMountRegistry.NormalizeHorseIdKey(savedId),
            normalizedHorseId,
            StringComparison.OrdinalIgnoreCase
        ));

        if (disabled)
        {
            disabledHorseIds.Add(normalizedHorseId);
        }

        this.config.DisabledHorseIdsBySaveId[currentSaveKey] = disabledHorseIds
            .Select(HorseMountRegistry.NormalizeHorseIdKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(savedId => savedId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ----------------------------
    // 初回同期用の馬を返す
    // ----------------------------
    private Horse? GetDefaultTrackedHorse()
    {
        return this.mountRegistry.FindFirstEligibleHorse(horse => !this.IsHorseDisabled(HorseMountRegistry.GetHorseIdKey(horse)) && !this.IsHorseClaimedByOther(horse));
    }

    // ----------------------------
    // 現在のセーブデータ用の設定キーを返す
    // ----------------------------
    private string? GetCurrentSaveConfigKey()
    {
        if (!Context.IsWorldReady)
        {
            return null;
        }

        return Game1.uniqueIDForThisGame.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ----------------------------
    // 指定した馬が占有中か判定する
    // ----------------------------
    private bool IsHorseClaimed(string horseId)
    {
        return this.horseClaims.ContainsKey(HorseMountRegistry.NormalizeHorseIdKey(horseId));
    }

    // ----------------------------
    // 指定した馬が他プレイヤーに占有されているか判定する
    // ----------------------------
    private bool IsHorseClaimedByOther(Horse horse)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            return false;
        }

        string horseId = HorseMountRegistry.NormalizeHorseIdKey(HorseMountRegistry.GetHorseIdKey(horse));
        return this.horseClaims.TryGetValue(horseId, out HorseClaimState? claim)
            && claim.PlayerId != Game1.player.UniqueMultiplayerID;
    }

    // ----------------------------
    // 指定した馬がこの画面で制御中か判定する
    // ----------------------------
    private bool IsHorseLocallyControlled(string horseId)
    {
        return string.Equals(
            this.followManager?.GetTrackedHorseId(),
            HorseMountRegistry.NormalizeHorseIdKey(horseId),
            StringComparison.OrdinalIgnoreCase
        );
    }

    // ----------------------------
    // 追跡対象の変更にあわせて占有状態を通知する
    // ----------------------------
    private void OnTrackedHorseChanged(string? previousHorseId, string? currentHorseId)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousHorseId))
        {
            this.BroadcastHorseClaim(previousHorseId, isClaimed: false);
        }

        if (!string.IsNullOrWhiteSpace(currentHorseId))
        {
            this.BroadcastHorseClaim(currentHorseId, isClaimed: true);
        }
    }

    // ----------------------------
    // クライアントからホストへ馬ワープ要求を送る
    // ----------------------------
    private void RequestHostHorseWarp(string horseId, string locationName, Point playerTile, int facingDirection)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.IsMasterGame)
        {
            return;
        }

        HorseWarpRequestMessage message = new()
        {
            HorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId),
            PlayerId = Game1.player.UniqueMultiplayerID,
            LocationName = locationName,
            PlayerTileX = playerTile.X,
            PlayerTileY = playerTile.Y,
            FacingDirection = facingDirection,
        };

        this.Helper.Multiplayer.SendMessage(
            message,
            WarpRequestMessageType,
            new[] { this.ModManifest.UniqueID }
        );
    }

    // ----------------------------
    // ホスト側で馬ワープ要求を受け取り正式な馬実体を移動する
    // ----------------------------
    private void ApplyHorseWarpRequestMessage(HorseWarpRequestMessage message)
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame)
        {
            return;
        }

        Farmer? farmer = Game1.getAllFarmers().FirstOrDefault(player => player.UniqueMultiplayerID == message.PlayerId);
        if (farmer is null)
        {
            return;
        }

        Horse? horse = this.mountRegistry.FindHorseById(message.HorseId);
        if (horse is null)
        {
            return;
        }

        GameLocation? targetLocation = this.FindLocationByNameOrUniqueName(message.LocationName) ?? farmer.currentLocation;
        if (targetLocation is null)
        {
            return;
        }

        Utility.HorseWarpRestrictions restrictions = Utility.GetHorseWarpRestrictionsForFarmer(farmer);
        Utility.HorseWarpRestrictions hardBlock = restrictions & (Utility.HorseWarpRestrictions.NoOwnedHorse | Utility.HorseWarpRestrictions.Indoors | Utility.HorseWarpRestrictions.InUse);
        if (hardBlock != Utility.HorseWarpRestrictions.None)
        {
            return;
        }

        HorseWarpCoordinator coordinator = new(() => this.config, this.Monitor);
        horse.mutex.RequestLock(() =>
        {
            try
            {
                HorseWarpCoordinator.WarpAttemptResult result = coordinator.TryWarpHorseNearTargetTile(horse, targetLocation, message.GetPlayerTile(), message.FacingDirection);
            }
            finally
            {
                horse.mutex.ReleaseLock();
            }
        });
    }

    // ----------------------------
    // 名前または一意名からロケーションを検索する
    // ----------------------------
    private GameLocation? FindLocationByNameOrUniqueName(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName) || !Context.IsWorldReady)
        {
            return null;
        }

        GameLocation? foundLocation = null;
        Utility.ForEachLocation(
            location =>
            {
                if (string.Equals(location.NameOrUniqueName, locationName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(location.Name, locationName, StringComparison.OrdinalIgnoreCase))
                {
                    foundLocation = location;
                    return false;
                }

                return true;
            },
            true,
            true
        );

        return foundLocation;
    }

    // ----------------------------
    // 馬の占有状態をローカル適用して他プレイヤーへ通知する
    // ----------------------------
    private void BroadcastHorseClaim(string horseId, bool isClaimed)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            return;
        }

        HorseClaimMessage message = new()
        {
            HorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId),
            PlayerId = Game1.player.UniqueMultiplayerID,
            StampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsClaimed = isClaimed,
        };

        this.ApplyHorseClaimMessage(message);
        this.Helper.Multiplayer.SendMessage(
            message,
            ClaimMessageType,
            new[] { this.ModManifest.UniqueID }
        );
    }

    // ----------------------------
    // 受信した占有状態をローカルへ反映する
    // ----------------------------
    private void ApplyHorseClaimMessage(HorseClaimMessage message)
    {
        string horseId = HorseMountRegistry.NormalizeHorseIdKey(message.HorseId);

        if (message.IsClaimed)
        {
            if (this.horseClaims.TryGetValue(horseId, out HorseClaimState? existingClaim)
                && existingClaim.StampUtcMs > message.StampUtcMs)
            {
                return;
            }

            this.horseClaims[horseId] = new HorseClaimState
            {
                PlayerId = message.PlayerId,
                StampUtcMs = message.StampUtcMs,
            };

            if (Context.IsWorldReady && Game1.player is not null && message.PlayerId != Game1.player.UniqueMultiplayerID)
            {
                this.followManager?.OnHorseClaimedByOther(horseId);
            }

            return;
        }

        if (this.horseClaims.TryGetValue(horseId, out HorseClaimState? currentClaim)
            && currentClaim.PlayerId == message.PlayerId
            && currentClaim.StampUtcMs <= message.StampUtcMs)
        {
            this.horseClaims.Remove(horseId);
        }
    }

    // ----------------------------
    // 設定値を補正する
    // ----------------------------
    private ModConfig NormalizeConfig(ModConfig? config)
    {
        config ??= new ModConfig();

        // キーバインドが未設定なら初期値を補う
        config.ToggleFollowKey = this.EnsureKeybindList(config.ToggleFollowKey, "H");

        // 数値が壊れていたときだけ安全な範囲へ戻す
        config.FollowStartDistance = System.Math.Clamp(config.FollowStartDistance, 1.0f, 8.0f);
        config.StopDistance = System.Math.Clamp(config.StopDistance, 0.5f, 4.0f);
        config.PathAbortDistance = System.Math.Clamp(config.PathAbortDistance, 0, 99);
        config.PathFailureAction = System.Math.Clamp(config.PathFailureAction, 0, 3);
        config.DismountDelayMilliseconds = System.Math.Max(0, config.DismountDelayMilliseconds);
        config.PathRebuildSeconds = System.Math.Clamp(config.PathRebuildSeconds, 0.25f, 5.0f);
        config.NearSpeedMultiplier = System.Math.Clamp(config.NearSpeedMultiplier, 0.25f, 6.0f);
        config.MidSpeedMultiplier = System.Math.Clamp(config.MidSpeedMultiplier, 0.25f, 6.0f);
        config.FarSpeedMultiplier = System.Math.Clamp(config.FarSpeedMultiplier, 0.25f, 6.0f);
        config.DisabledHorseIdsBySaveId = (config.DisabledHorseIdsBySaveId ?? new Dictionary<string, List<string>>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => (pair.Value ?? new List<string>())
                    .Where(horseId => !string.IsNullOrWhiteSpace(horseId))
                    .Select(HorseMountRegistry.NormalizeHorseIdKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(horseId => horseId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        return config;
    }

    // ----------------------------
    // キーバインドが未設定なら初期値を補う
    // ----------------------------
    private KeybindList EnsureKeybindList(KeybindList? keybind, string fallback)
    {
        if (keybind is not null)
        {
            return keybind;
        }

        MethodInfo? parseMethod = typeof(KeybindList).GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (parseMethod?.Invoke(null, new object?[] { fallback }) is KeybindList parsed)
        {
            return parsed;
        }

        return KeybindList.Parse(fallback);
    }
}