using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Timecheat.UI;

internal static class ScrollView
{
    public static TView Scrollable<TView>(this TView view) where TView : View
    {
        view.VerticalScrollBar.Visible = true;
        view.VerticalScrollBar.AutoShow = true;
        view.HorizontalScrollBar.Visible = true;
        view.HorizontalScrollBar.AutoShow = true;

        // Handle keyboard scrolling (only when content view has focus)
        view.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.PageUp:
                    view.ScrollVertical(-view.Viewport.Height);
                    e.Handled = true;
                    return;
                case KeyCode.PageDown:
                    view.ScrollVertical(view.Viewport.Height);
                    e.Handled = true;
                    return;
                case KeyCode.CursorUp:
                    view.ScrollVertical(-1);
                    e.Handled = true;
                    return;
                case KeyCode.CursorDown:
                    view.ScrollVertical(1);
                    e.Handled = true;
                    return;
                case KeyCode.Home:
                    view.Viewport = new System.Drawing.Rectangle(0, 0, view.Viewport.Width, view.Viewport.Height);
                    e.Handled = true;
                    return;
                case KeyCode.End:
                    var maxY = Math.Max(0, view.GetContentSize().Height - view.Viewport.Height);
                    view.Viewport = new System.Drawing.Rectangle(0, maxY, view.Viewport.Width, view.Viewport.Height);
                    e.Handled = true;
                    return;
            }
        };

        view.MouseEvent += (s, e) =>
        {
            if (e.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                view.ScrollVertical(-3);
                e.Handled = true;
                return;
            }
            else if (e.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                view.ScrollVertical(3);
                e.Handled = true;
                return;
            }
        };

        return view;
    }
}