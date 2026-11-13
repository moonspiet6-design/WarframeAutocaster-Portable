using Autocaster.Core;

namespace Autocaster.App;

internal static class Words
{
    internal static string Action(ActionKind kind) => kind switch
    {
        ActionKind.KeyPress => "Key press",
        ActionKind.KeyHold => "Hold key",
        ActionKind.Wait => "Delay",
        ActionKind.MouseClick => "Mouse click",
        ActionKind.MouseMovement => "Mouse movement",
        ActionKind.StartMacroTimer => "Start timer",
        ActionKind.LarvaDetect => "Larva detect",
        ActionKind.RathuumKillFeed => "Rathuum kill feed",
        _ => kind.ToString()
    };

    internal static string Key(InputKey key) => key switch
    {
        >= InputKey.D0 and <= InputKey.D9 => ((int)key - (int)InputKey.D0).ToString(),
        InputKey.LeftShift => "Left Shift",
        InputKey.RightShift => "Right Shift",
        InputKey.LeftCtrl => "Left Ctrl",
        InputKey.RightCtrl => "Right Ctrl",
        InputKey.LeftAlt => "Left Alt",
        InputKey.RightAlt => "Right Alt",
        _ => key.ToString()
    };

    internal static string Mouse(MouseButton button) => button switch
    {
        MouseButton.Left => "Left click",
        MouseButton.Right => "Right click",
        MouseButton.Middle => "Middle click",
        MouseButton.X1 => "Mouse 4",
        MouseButton.X2 => "Mouse 5",
        _ => button.ToString()
    };
}
