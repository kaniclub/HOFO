using StardewModdingAPI.Utilities;

namespace HorseFollowsYou;

internal sealed class ModConfig
{
    // ----------------------------
    // Mod 全体の有効 / 無効
    // ----------------------------
    public bool ModEnabled { get; set; } = true;

    // ----------------------------
    // 馬移動許可先へのマップ跨ぎ追従
    // ----------------------------
    public bool EnableWarpFollow { get; set; } = true;

    // ----------------------------
    // HUD メッセージ表示
    // ----------------------------
    public bool EnableHudMessages { get; set; } = true;

    // ----------------------------
    // 追従トグルキー
    // - 同じキーで開始 / 停止を切り替える
    // ----------------------------
    public KeybindList ToggleFollowKey { get; set; } = KeybindList.Parse("H");

    // ----------------------------
    // 追従開始距離（タイル）
    // ----------------------------
    public float FollowStartDistance { get; set; } = 2.0f;

    // ----------------------------
    // 停止距離（タイル）
    // ----------------------------
    public float StopDistance { get; set; } = 2.0f;

    // ----------------------------
    // 降馬後に追従開始を待つ時間（ミリ秒）
    // ----------------------------
    public int DismountDelayMilliseconds { get; set; } = 250;

    // ----------------------------
    // 経路再計算の最小間隔（秒）
    // ----------------------------
    public float PathRebuildSeconds { get; set; } = 0.5f;

    // ----------------------------
    // 距離帯ごとの速度倍率
    // ----------------------------
    public float NearSpeedMultiplier { get; set; } = 1.5f;
    public float MidSpeedMultiplier { get; set; } = 2.5f;
    public float FarSpeedMultiplier { get; set; } = 4.0f;

    // ----------------------------
    // 細い当たり判定
    // ----------------------------
    public bool UseNarrowHitbox { get; set; } = false;

    // ----------------------------
    // プレイヤー貫通
    // - 馬とプレイヤーの衝突を無視する
    // ----------------------------
    public bool IgnorePlayerCollision { get; set; } = false;

    // ----------------------------
    // デバッグモード
    // ----------------------------
    public bool DebugMode { get; set; } = false;
}
