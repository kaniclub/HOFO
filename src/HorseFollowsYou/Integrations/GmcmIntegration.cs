using HorseFollowsYou.Services;
using StardewModdingAPI;

namespace HorseFollowsYou.Integrations;

// ----------------------------
// Generic Mod Config Menu 連携
// ----------------------------
internal sealed class GmcmIntegration
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";

    private readonly IModHelper helper;
    private readonly IManifest manifest;
    private readonly ITranslationHelper translation;
    private readonly System.Func<ModConfig> getConfig;
    private readonly System.Action<ModConfig> setConfig;
    private readonly Func<IReadOnlyList<HorseMountOption>> getMountOptions;
    private readonly Func<string, bool> isHorseDisabled;
    private readonly Action<string, bool> setHorseDisabled;
    private readonly Action resetAllSettings;
    private readonly Action saveAllSettings;

    public GmcmIntegration(
        IModHelper helper,
        IManifest manifest,
        ITranslationHelper translation,
        System.Func<ModConfig> getConfig,
        System.Action<ModConfig> setConfig,
        Func<IReadOnlyList<HorseMountOption>> getMountOptions,
        Func<string, bool> isHorseDisabled,
        Action<string, bool> setHorseDisabled,
        Action resetAllSettings,
        Action saveAllSettings)
    {
        this.helper = helper;
        this.manifest = manifest;
        this.translation = translation;
        this.getConfig = getConfig;
        this.setConfig = setConfig;
        this.getMountOptions = getMountOptions;
        this.isHorseDisabled = isHorseDisabled;
        this.setHorseDisabled = setHorseDisabled;
        this.resetAllSettings = resetAllSettings;
        this.saveAllSettings = saveAllSettings;
    }

    public void RegisterOrReload()
    {
        if (!this.helper.ModRegistry.IsLoaded(GmcmId))
        {
            return;
        }

        IGenericModConfigMenuApi? api = this.helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (api is null)
        {
            return;
        }

        api.Unregister(this.manifest);
        api.Register(
            this.manifest,
            reset: this.resetAllSettings,
            save: this.saveAllSettings
        );

        this.AddGeneralOptions(api);
        this.AddDisabledMountOptions(api);
    }

    // ----------------------------
    // 通常設定を登録する
    // ----------------------------
    private void AddGeneralOptions(IGenericModConfigMenuApi api)
    {
        api.AddBoolOption(this.manifest, () => this.getConfig().ModEnabled, value => this.getConfig().ModEnabled = value, () => this.T("config.enabled.name"), () => this.T("config.enabled.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().EnableWarpFollow, value => this.getConfig().EnableWarpFollow = value, () => this.T("config.warp_follow.name"), () => this.T("config.warp_follow.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().EnableFollowOnLoad, value => this.getConfig().EnableFollowOnLoad = value, () => this.T("config.follow_on_load.name"), () => this.T("config.follow_on_load.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().AutoEnableFollowOnMountOrFlute, value => this.getConfig().AutoEnableFollowOnMountOrFlute = value, () => this.T("config.auto_enable_follow.name"), () => this.T("config.auto_enable_follow.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().EnableWarpEffectsAndSound, value => this.getConfig().EnableWarpEffectsAndSound = value, () => this.T("config.warp_effects_and_sound.name"), () => this.T("config.warp_effects_and_sound.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().EnableHudMessages, value => this.getConfig().EnableHudMessages = value, () => this.T("config.hud_messages.name"), () => this.T("config.hud_messages.description"));
        api.AddKeybindList(this.manifest, () => this.getConfig().ToggleFollowKey, value => this.getConfig().ToggleFollowKey = value, () => this.T("config.toggle_key.name"), () => this.T("config.toggle_key.description"));

        api.AddNumberOption(this.manifest, () => this.getConfig().FollowStartDistance, value => this.getConfig().FollowStartDistance = value, () => this.T("config.follow_start_distance.name"), () => this.T("config.follow_start_distance.description"), 1.0f, 8.0f, 0.25f);
        api.AddNumberOption(this.manifest, () => this.getConfig().StopDistance, value => this.getConfig().StopDistance = value, () => this.T("config.stop_distance.name"), () => this.T("config.stop_distance.description"), 0.5f, 4.0f, 0.25f);
        api.AddNumberOption(this.manifest, () => this.getConfig().PathAbortDistance, value => this.getConfig().PathAbortDistance = value, () => this.T("config.path_abort_distance.name"), () => this.T("config.path_abort_distance.description"), 0, 99, 1);
        api.AddTextOption(
            this.manifest,
            () => this.getConfig().PathFailureAction.ToString(),
            value =>
            {
                if (int.TryParse(value, out int parsed))
                {
                    this.getConfig().PathFailureAction = System.Math.Clamp(parsed, 0, 3);
                    return;
                }

                this.getConfig().PathFailureAction = 0;
            },
            () => this.T("config.path_failure_action.name"),
            () => this.T("config.path_failure_action.description"),
            new[] { "0", "1", "2", "3" },
            value => value switch
            {
                "1" => this.T("config.path_failure_action.warp"),
                "2" => this.T("config.path_failure_action.turn_off"),
                "3" => this.T("config.path_failure_action.do_not_move"),
                _ => this.T("config.path_failure_action.wait"),
            }
        );
        api.AddNumberOption(this.manifest, () => this.getConfig().PathRebuildSeconds, value => this.getConfig().PathRebuildSeconds = value, () => this.T("config.path_rebuild_seconds.name"), () => this.T("config.path_rebuild_seconds.description"), 0.25f, 5.0f, 0.25f);
        api.AddNumberOption(this.manifest, () => this.getConfig().NearSpeedMultiplier, value => this.getConfig().NearSpeedMultiplier = value, () => this.T("config.near_speed.name"), () => this.T("config.near_speed.description"), 0.25f, 6.0f, 0.05f);
        api.AddNumberOption(this.manifest, () => this.getConfig().MidSpeedMultiplier, value => this.getConfig().MidSpeedMultiplier = value, () => this.T("config.mid_speed.name"), () => this.T("config.mid_speed.description"), 0.25f, 6.0f, 0.05f);
        api.AddNumberOption(this.manifest, () => this.getConfig().FarSpeedMultiplier, value => this.getConfig().FarSpeedMultiplier = value, () => this.T("config.far_speed.name"), () => this.T("config.far_speed.description"), 0.25f, 6.0f, 0.05f);
        api.AddBoolOption(this.manifest, () => this.getConfig().UseNarrowHitbox, value => this.getConfig().UseNarrowHitbox = value, () => this.T("config.narrow_hitbox.name"), () => this.T("config.narrow_hitbox.description"));
        api.AddBoolOption(this.manifest, () => this.getConfig().IgnorePlayerCollision, value => this.getConfig().IgnorePlayerCollision = value, () => this.T("config.ignore_player_collision.name"), () => this.T("config.ignore_player_collision.description"));
    }

    // ----------------------------
    // 同行対象外の馬一覧を登録する
    // ----------------------------
    private void AddDisabledMountOptions(IGenericModConfigMenuApi api)
    {
        api.AddSectionTitle(this.manifest, () => this.T("config.disabled_mounts.title"), () => this.T("config.disabled_mounts.description"));

        IReadOnlyList<HorseMountOption> mountOptions = this.getMountOptions();
        if (mountOptions.Count <= 0)
        {
            api.AddParagraph(this.manifest, () => this.T("config.disabled_mounts.empty"));
            return;
        }

        api.AddParagraph(this.manifest, () => this.T("config.disabled_mounts.helper"));

        foreach (HorseMountOption mountOption in mountOptions)
        {
            HorseMountOption currentOption = mountOption;
            api.AddBoolOption(
                this.manifest,
                () => this.isHorseDisabled(currentOption.HorseId),
                value => this.setHorseDisabled(currentOption.HorseId, value),
                () => currentOption.DisplayName,
                () => this.T("config.disabled_mounts.entry_tooltip")
            );
        }
    }

    private string T(string key)
    {
        return this.translation.Get(key).ToString();
    }
}
