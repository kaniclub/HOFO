using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using HorseFollowsYou.Integrations;
using HorseFollowsYou.Services;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace HorseFollowsYou;

// ----------------------------
// Mod の入口処理
// - 設定読込
// - イベント登録
// - GMCM 登録
// ----------------------------
public sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private HorseFollowManager? followManager;

    // ----------------------------
    // Mod 起動時の初期化を行う
    // ----------------------------
    public override void Entry(IModHelper helper)
    {
        this.config = this.NormalizeConfig(helper.ReadConfig<ModConfig>());
        this.followManager = new HorseFollowManager(helper.Translation, () => this.config, this.Monitor);

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
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
        new GmcmIntegration(
            this.Helper,
            this.ModManifest,
            this.Helper.Translation,
            () => this.config,
            value => this.config = this.NormalizeConfig(value)
        ).Register();
    }

    // ----------------------------
    // セーブ読込後の処理を行う
    // ----------------------------
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.followManager?.OnSaveLoaded();
    }

    // ----------------------------
    // タイトルへ戻ったときに状態を戻す
    // ----------------------------
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
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
    // デバッグ用の色付きタイルを描画する
    // ----------------------------
    private void DrawDebugTile(SpriteBatch spriteBatch, Point tile, Color color)
    {
        Vector2 pixelPosition = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f, tile.Y * 64f));
        Rectangle destination = new((int)pixelPosition.X, (int)pixelPosition.Y, 64, 64);
        spriteBatch.Draw(Game1.staminaRect, destination, color);
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

        if (Activator.CreateInstance(typeof(KeybindList), nonPublic: true) is KeybindList created)
        {
            return created;
        }

        throw new System.InvalidOperationException("Unable to create a KeybindList.");
    }
}