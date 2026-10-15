using Microsoft.Xna.Framework;

namespace HorseFollowsYou.Integrations;

// ----------------------------
// Iconic Framework API
// ----------------------------
public interface IIconicFrameworkApi
{
    void AddToolbarIcon(
        string id,
        string texturePath,
        Rectangle? sourceRect,
        Func<string>? getTitle,
        Func<string>? getDescription,
        Action onClick,
        Action? onRightClick = null);
}
