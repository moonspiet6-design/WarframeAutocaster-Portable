namespace Autocaster.Windows;

public sealed class OverlayCursor
{
    public const int SetCursorMessage = 0x0020;
    private readonly WindowTargetService _game = new();

    public bool GameHasFocus() => _game.HasFocus() || _game.TryArm();

    public bool HideWhileGameFocused()
    {
        if (!GameHasFocus())
            return false;
        Native.SetCursor(0);
        return true;
    }
}
