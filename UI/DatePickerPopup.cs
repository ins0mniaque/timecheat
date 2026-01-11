using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Timecheat.UI;

internal static class DatePickerPopup
{
    public static DateField WithDatePicker(this DateField dateField, IApplication app)
    {
        dateField.KeyDown += (s, e) =>
        {
            if (e.KeyCode is KeyCode.Enter)
            {
                dateField.ShowDatePicker(app);
                e.Handled = true;
            }
        };

        dateField.MouseEvent += (s, e) =>
        {
            if (e.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                dateField.ShowDatePicker(app);
                e.Handled = true;
            }
        };

        return dateField;
    }

    private static void ShowDatePicker(this DateField dateField, IApplication app)
    {
        using var picker = new DatePicker(dateField.Date ?? DateTime.Now.Date)
        {
            BorderStyle = LineStyle.None
        };

        picker.Margin!.Thickness = new(1, 0, 1, 0);

        if (picker.SubViews.OfType<TableView>().FirstOrDefault() is not { } calendar)
            throw new InvalidOperationException("Could not find calendar inside DatePicker");

        if (picker.SubViews.OfType<DateField>().FirstOrDefault() is { } pickerField)
            pickerField.Date = picker.Date;

        calendar.CellActivated += (s, e) =>
        {
            dateField.Date = picker.Date;
            app.RequestStop();
        };

        using var dialog = new Dialog { BorderStyle = LineStyle.Rounded };

        dialog.Add(picker);

        app.Run(dialog);
    }
}