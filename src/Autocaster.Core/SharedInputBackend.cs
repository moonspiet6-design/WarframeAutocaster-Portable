namespace Autocaster.Core;

public sealed class SharedInputBackend(IInputBackend backend) : IInputBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<InputEvent, int> _holds = [];

    public bool IsPressed(InputEvent input)
    {
        lock (_gate)
            return !_holds.ContainsKey(input) && backend.IsPressed(input);
    }

    public void Send(InputEvent input)
    {
        lock (_gate)
        {
            if (input.IsDown)
            {
                if (_holds.TryGetValue(input, out int existing))
                {
                    _holds[input] = existing + 1;
                    return;
                }
                if (backend.IsPressed(input))
                    throw new InvalidOperationException($"{input.Parameter} is already down.");
                backend.Send(input);
                _holds.Add(input, 1);
            }
            else if (input.IsUp)
            {
                var down = new InputEvent(input.Kind == InputKind.KeyUp ? InputKind.KeyDown : InputKind.MouseButtonDown, input.Value);
                if (_holds.TryGetValue(down, out int count) && count > 1)
                {
                    _holds[down] = count - 1;
                    return;
                }
                backend.Send(input);
                _holds.Remove(down);
            }
            else
                backend.Send(input);
        }
    }
}
