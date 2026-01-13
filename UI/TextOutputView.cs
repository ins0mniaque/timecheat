using System.Drawing;

using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Timecheat.UI;

internal sealed class TextOutputView : TextView
{
    private int _lastTopRow = -1;
    private bool _updating;

    public TextOutputView()
    {
        AllowsTab = false;
        Multiline = true;
        ReadOnly = true;
        WordWrap = true;

        CursorVisibility = CursorVisibility.Invisible;
        HorizontalScrollBar.AutoShow = true;
        VerticalScrollBar.AutoShow = true;
        VerticalScrollBar.Scrolled += (s, e) =>
        {
            if (_updating)
                return;

            _updating = true;

            TopRow = VerticalScrollBar.Position;

            var cursorRow = CursorPosition.Y;
            var bottomRow = TopRow + Frame.Height - 1;

            if (cursorRow < TopRow)
                CursorPosition = new Point(CursorPosition.X, TopRow);
            else if (cursorRow > bottomRow)
                CursorPosition = new Point(CursorPosition.X, bottomRow);

            _updating = false;
        };
    }

    protected override bool OnGettingAttributeForRole(in VisualRole role, ref Terminal.Gui.Drawing.Attribute currentAttribute)
    {
        var scheme = SchemeManager.GetSchemesForCurrentTheme()?["Base"];
        if (scheme is null)
            return false;

        currentAttribute = role switch
        {
            VisualRole.Focus => scheme.Focus,
            VisualRole.Highlight => scheme.Highlight,
            VisualRole.Disabled => scheme.Disabled,
            _ => scheme.Normal
        };

        return true;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_lastTopRow != TopRow && !_updating)
        {
            _lastTopRow = TopRow;
            UpdateScrollBars();

            var cursorRow = CursorPosition.Y;
            var bottomRow = TopRow + Frame.Height - 1;

            if (cursorRow < TopRow)
                CursorPosition = new Point(CursorPosition.X, TopRow);
            else if (cursorRow > bottomRow)
                CursorPosition = new Point(CursorPosition.X, bottomRow);
        }

        return base.OnDrawingContent(context);
    }

    private void UpdateScrollBars()
    {
        _updating = true;

        HorizontalScrollBar.ScrollableContentSize = Maxlength;
        HorizontalScrollBar.VisibleContentSize = Frame.Width;
        HorizontalScrollBar.Position = LeftColumn;

        VerticalScrollBar.ScrollableContentSize = Lines;
        VerticalScrollBar.VisibleContentSize = Frame.Height;
        VerticalScrollBar.Position = TopRow;

        _updating = false;
    }
}