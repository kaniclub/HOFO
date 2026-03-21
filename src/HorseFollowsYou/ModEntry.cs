using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using HorseFollowsYou.Integrations;
using HorseFollowsYou.Services;
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
// ----------------------------
public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "follow-mount-settings";

    private ModConfig config = new();
    private FollowMountSaveData saveData = new();
    private HorseFollowManager? followManager;
    private HorseMountRegistry mountRegistry = null!;
    private GmcmIntegration? gmcmIntegration;
    private IReadOnlyList<HorseMountOption> cachedMountOptions = Array.Empty<HorseMountOption>();
    private bool horseWarpEventSubscribed;

    // ----------------------------
    // Mod 起動時の初期化を行う
    // ----------------------------
    public override void Entry(IModHelper helper)
    {
        this.config = this.NormalizeConfig(helper.ReadConfig<ModConfig>());
        this.saveData = new FollowMountSaveData();
        this.mountRegistry = new HorseMountRegistry();
        this.followManager = new HorseFollowManager(
            helper.Translation,
            () => this.config,
            this.Monitor,
            this.IsHorseDisabled,
            this.GetDefaultTrackedHorse
        );

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Player.Warped += this.OnWarped;
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
        this.LoadSaveData();
        this.RefreshMountOptions();
        this.gmcmIntegration?.RegisterOrReload();

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
        this.followManager?.OnMountFilterChanged();
    }

    // ----------------------------
    // タイトルへ戻ったときに状態を戻す
    // ----------------------------
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.UnsubscribeHorseWarpEvent();
        this.saveData = new FollowMountSaveData();
        this.cachedMountOptions = Array.Empty<HorseMountOption>();
        this.gmcmIntegration?.RegisterOrReload();
        this.followManager?.Reset();
    }

    // ----------------------------
    // 毎 tick の更新処理を呼ぶ
    // ----------------------------
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.followManager?.OnUpdateTicked(e.Ticks);
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

        this.followManager?.OnWarped();
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
    // セーブデータを読み込む
    // ----------------------------
    private void LoadSaveData()
    {
        FollowMountSaveData? loaded = this.Helper.Data.ReadSaveData<FollowMountSaveData>(SaveDataKey);
        this.saveData = this.NormalizeSaveData(loaded);
    }

    // ----------------------------
    // セーブデータを保存する
    // ----------------------------
    private void SaveSaveData()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        this.Helper.Data.WriteSaveData(SaveDataKey, this.NormalizeSaveData(this.saveData));
    }

    // ----------------------------
    // GMCM からの全設定保存を行う
    // ----------------------------
    private void SaveAllSettings()
    {
        this.config = this.NormalizeConfig(this.config);
        this.saveData = this.NormalizeSaveData(this.saveData);

        this.Helper.WriteConfig(this.config);
        this.SaveSaveData();
        this.RefreshMountOptions();
        this.followManager?.OnMountFilterChanged();
    }

    // ----------------------------
    // GMCM からの全設定リセットを行う
    // ----------------------------
    private void ResetAllSettings()
    {
        this.config = this.NormalizeConfig(new ModConfig());
        this.saveData = new FollowMountSaveData();
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
        string normalizedHorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId);
        return this.saveData.DisabledHorseIds.Any(savedId => string.Equals(
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
        string normalizedHorseId = HorseMountRegistry.NormalizeHorseIdKey(horseId);

        this.saveData.DisabledHorseIds = this.saveData.DisabledHorseIds
            .Where(savedId => !string.Equals(
                HorseMountRegistry.NormalizeHorseIdKey(savedId),
                normalizedHorseId,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToList();

        if (disabled)
        {
            this.saveData.DisabledHorseIds.Add(normalizedHorseId);
        }
    }

    // ----------------------------
    // 初回同期用の馬を返す
    // ----------------------------
    private Horse? GetDefaultTrackedHorse()
    {
        return this.mountRegistry.FindFirstEligibleHorse(this.IsHorseDisabled);
    }

    // ----------------------------
    // セーブデータの内容を補正する
    // ----------------------------
    private FollowMountSaveData NormalizeSaveData(FollowMountSaveData? saveData)
    {
        saveData ??= new FollowMountSaveData();
        saveData.DisabledHorseIds = (saveData.DisabledHorseIds ?? new List<string>())
            .Where(static horseId => !string.IsNullOrWhiteSpace(horseId))
            .Select(HorseMountRegistry.NormalizeHorseIdKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static horseId => horseId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return saveData;
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