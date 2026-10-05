using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace AstroColour.App.Controls;

/// <summary>A Grid whose mouse cursor can be changed (ProtectedCursor is not public on UIElement).</summary>
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);

    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
