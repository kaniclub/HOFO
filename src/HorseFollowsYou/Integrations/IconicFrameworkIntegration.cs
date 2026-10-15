using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace HorseFollowsYou.Integrations;

// ----------------------------
// Iconic Framework 連携
// ----------------------------
internal sealed class IconicFrameworkIntegration
{
    private const string IconicFrameworkId = "furyx639.ToolbarIcons";
    private const string MinimumVersion = "3.2.0";
    private const string ToggleIconId = "ToggleFollow";
    private const string IconAssetName = "kaniclub.HorseFollowsYou/IconicFramework/ToggleFollow";
    private const int IconSize = 16;

    private readonly IModHelper helper;
    private readonly ITranslationHelper translation;
    private readonly IMonitor monitor;
    private readonly Action toggleFollow;
    private bool registrationAttempted;

    public IconicFrameworkIntegration(
        IModHelper helper,
        ITranslationHelper translation,
        IMonitor monitor,
        Action toggleFollow)
    {
        this.helper = helper;
        this.translation = translation;
        this.monitor = monitor;
        this.toggleFollow = toggleFollow;
    }

    // ----------------------------
    // 起動時に一度だけ同行切り替えアイコンを登録する
    // ----------------------------
    public void Register()
    {
        // 再登録によるクリック処理の重複を防ぐ
        if (this.registrationAttempted)
        {
            return;
        }

        this.registrationAttempted = true;
        if (!this.helper.ModRegistry.IsLoaded(IconicFrameworkId))
        {
            return;
        }

        IModInfo? mod = this.helper.ModRegistry.Get(IconicFrameworkId);
        if (mod is null)
        {
            return;
        }

        if (mod.Manifest.Version.IsOlderThan(MinimumVersion))
        {
            this.monitor.Log($"Iconic Framework {MinimumVersion} or later is needed for icon integration. The companion toggle key is still available.", LogLevel.Warn);
            return;
        }

        try
        {
            IIconicFrameworkApi? api = this.helper.ModRegistry.GetApi<IIconicFrameworkApi>(IconicFrameworkId);
            if (api is null)
            {
                return;
            }

            this.helper.Events.Content.AssetRequested += this.OnAssetRequested;
            api.AddToolbarIcon(
                ToggleIconId,
                IconAssetName,
                new Rectangle(0, 0, IconSize, IconSize),
                () => this.translation.Get("icon.toggle_follow.title").ToString(),
                () => this.translation.Get("icon.toggle_follow.description").ToString(),
                this.toggleFollow
            );
        }
        catch (Exception ex)
        {
            this.helper.Events.Content.AssetRequested -= this.OnAssetRequested;
            this.monitor.Log($"Iconic Framework integration could not be registered. The companion toggle key is still available.\n{ex}", LogLevel.Warn);
        }
    }

    // ----------------------------
    // Framework の設定で使用するアイコン画像を用意する
    // ----------------------------
    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(IconAssetName))
        {
            e.LoadFrom(this.CreateToggleIcon, AssetLoadPriority.Exclusive);
        }
    }

    private Texture2D CreateToggleIcon()
    {
        const int sourceSize = 32;
        Texture2D source = this.helper.GameContent.Load<Texture2D>("Animals/horse");
        Color[] sourcePixels = new Color[sourceSize * sourceSize];
        source.GetData(0, new Rectangle(0, sourceSize, sourceSize, sourceSize), sourcePixels, 0, sourcePixels.Length);

        Color[] iconPixels = new Color[IconSize * IconSize];
        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                iconPixels[(y * IconSize) + x] = sourcePixels[((y * sourceSize / IconSize) * sourceSize) + (x * sourceSize / IconSize)];
            }
        }

        Texture2D icon = new(source.GraphicsDevice, IconSize, IconSize);
        icon.SetData(iconPixels);
        return icon;
    }
}
