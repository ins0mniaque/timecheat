using LibGit2Sharp;

using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using TextCopy;

using Timecheat;

ConfigurationManager.Enable(ConfigLocations.All);

using IApplication app = Application.Create();

app.Init();

var mainWindow = new Window
{
    X = 0,
    Y = 0,
    Width = Dim.Fill(),
    Height = Dim.Fill(),
    Title = "timecheat"
};

var y = 1;

// Repo directory
var repoButton = new Button { X = 25, Y = y, Text = "Select" };
var repoLabel = new Label { X = 1, Y = y, Text = "Repository directory" };
var repoPathLabel = new Label { X = 36, Y = y, Text = "<none>" };
var lastRepoPath = "";
var repoPath = "";

mainWindow.Add(repoLabel, repoButton, repoPathLabel);
y += 2;

// Author email (optional)
var emailLabel = new Label { X = 1, Y = y, Text = "Author email (optional)" };
var emailField = new TextField { X = 25, Y = y, Width = 30, Text = "" };
mainWindow.Add(emailLabel, emailField);
y += 2;

// Issue prefix
var prefixLabel = new Label { X = 1, Y = y, Text = "Issue prefix" };
var prefixField = new TextField { X = 25, Y = y, Width = 30, Text = "" };
mainWindow.Add(prefixLabel, prefixField);
y += 2;

// Start date
var startLabel = new Label { X = 1, Y = y, Text = "Start date" };
var startField = new TextField { X = 25, Y = y, Width = 12, Text = "" };
mainWindow.Add(startLabel, startField);
y += 2;

// End date
var endLabel = new Label { X = 1, Y = y, Text = "End date" };
var endField = new TextField { X = 25, Y = y, Width = 12, Text = "" };
mainWindow.Add(endLabel, endField);
y += 3;

// Auto-format dates on lost focus
void AutoFormatDate(TextField field)
{
    field.HasFocusChanged += (s, e) =>
    {
        if (!e.NewValue)
        {
            var txt = field.Text?.ToString()?.Trim() ?? "";
            if (DateTime.TryParse(txt, out DateTime dt))
                field.Text = $"{dt:yyyy-MM-dd}";
        }
    };
}
AutoFormatDate(startField);
AutoFormatDate(endField);

repoButton.Accepting += (s, e) => e.Handled = true;
repoButton.Accepted += (s, e) =>
{
    var dlg = new OpenDialog
    {
        Title = "Select repository",
        AllowsMultipleSelection = false,
        Path = string.IsNullOrEmpty(lastRepoPath) ? Environment.CurrentDirectory : lastRepoPath
    };

    app.Run(dlg);

    if (!dlg.Canceled)
    {
        repoPath = dlg.Path?.ToString() ?? "";
        lastRepoPath = repoPath;
        repoPathLabel.Text = repoPath;

        if (Repository.IsValid(repoPath))
        {
            using var repo = new Repository(repoPath);
            var email = repo.Config.Get<string>("user.email")?.Value ?? "";
            emailField.Text = email;
        }
        else
            MessageBox.ErrorQuery(app, "Error", "Selected directory is not a git repository", "OK");
    }
};

// Process commits button
using var processButton = new Button { X = 1, Y = y, Text = "Process Commits" };
mainWindow.Add(processButton);

processButton.Accepting += (s, e) => e.Handled = true;
processButton.Accepted += (s, e) =>
{
    if (string.IsNullOrWhiteSpace(repoPath) || !Repository.IsValid(repoPath))
    {
        MessageBox.ErrorQuery(app, "Error", "Invalid repository path", "OK");
        return;
    }

    var prefix = prefixField.Text?.ToString()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(prefix))
    {
        MessageBox.ErrorQuery(app, "Error", "Invalid issue prefix", "OK");
        return;
    }

    var generator = new TimesheetGenerator(repoPath, $@"({prefix}-\d+)");
    var author = emailField.Text?.ToString()?.Trim() ?? "";
    generator.Author = author;

    if (string.IsNullOrWhiteSpace(generator.Author))
    {
        MessageBox.ErrorQuery(app, "Error", "No author email specified", "OK");
        return;
    }

    if (!DateTime.TryParse(startField.Text?.ToString()?.Trim(), out DateTime startDate) ||
        !DateTime.TryParse(endField.Text?.ToString()?.Trim(), out DateTime endDate))
    {
        MessageBox.ErrorQuery(app, "Error", "Please enter valid start and end dates", "OK");
        return;
    }

    var timesheet = generator.TimesheetFor(startDate, endDate);

    // === Results window ===
    var resultWindow = new Window()
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        Title = "Results"
    };

    // Content view for scrolling
    var contentView = new View()
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill() - 3,
        CanFocus = true
    };
    resultWindow.Add(contentView);

    // Add results lines
    var ry = 0;
    var contentWidth = 0;

    void AddLine(string text)
    {
        var label = new Label()
        {
            X = 0,
            Y = ry,
            Text = text
        };
        contentView.Add(label);

        ry++;
        contentWidth = Math.Max(contentWidth, text.Length);
    }

    // Fill with timesheet results
    AddLine($"Found {timesheet.TotalCommits} commits ({timesheet.TrackedCommits} tracked) across {timesheet.TaskCount} tasks");
    AddLine("");

    var totalHours = 0.0;
    foreach (var day in timesheet.Days.OrderBy(d => d.Date))
    {
        if (day.TrackedTasks.Count is not 0 || day.UntrackedTasks.Count is not 0)
        {
            AddLine($"{day.Date:yyyy-MM-dd} ({day.Date:ddd})");

            foreach (var task in day.TrackedTasks.OrderBy(t => t.StartTime))
            {
                var issueDisplay = !string.IsNullOrEmpty(task.IssueId) ? $"[{task.IssueId}] " : "";
                AddLine($"  {task.Hours:F1}h - {issueDisplay}{task.Title}");
                totalHours += task.Hours;
            }

            AddLine($"  Total: {day.TrackedHours:F1}h");

            if (day.UntrackedTasks.Count is not 0)
            {
                AddLine("  Untracked (no issue):");
                foreach (var task in day.UntrackedTasks.OrderBy(t => t.StartTime))
                {
                    AddLine($"    {task.Hours:F1}h - {task.Title}");
                }
            }

            AddLine("");
        }
    }

    AddLine(new string('-', 50));
    AddLine($"Total tracked hours: {totalHours:F1}h");
    var workDays = timesheet.Days.Count(d => d.TrackedTasks.Count is not 0);
    if (workDays > 0)
        AddLine($"Average hours/day: {totalHours / workDays:F1}h");

    // Enable scrolling on content view
    contentView.SetContentSize(new System.Drawing.Size(contentWidth, ry));
    contentView.VerticalScrollBar.Visible = true;
    contentView.VerticalScrollBar.AutoShow = true;
    contentView.HorizontalScrollBar.Visible = true;
    contentView.HorizontalScrollBar.AutoShow = true;

    // Handle keyboard scrolling (only when content view has focus)
    contentView.KeyDown += (s, e) =>
    {
        switch (e.KeyCode)
        {
            case KeyCode.PageUp:
                contentView.ScrollVertical(-contentView.Viewport.Height);
                e.Handled = true;
                return;
            case KeyCode.PageDown:
                contentView.ScrollVertical(contentView.Viewport.Height);
                e.Handled = true;
                return;
            case KeyCode.CursorUp:
                contentView.ScrollVertical(-1);
                e.Handled = true;
                return;
            case KeyCode.CursorDown:
                contentView.ScrollVertical(1);
                e.Handled = true;
                return;
            case KeyCode.Home:
                contentView.Viewport = new System.Drawing.Rectangle(0, 0, contentView.Viewport.Width, contentView.Viewport.Height);
                e.Handled = true;
                return;
            case KeyCode.End:
                int maxY = Math.Max(0, contentView.GetContentSize().Height - contentView.Viewport.Height);
                contentView.Viewport = new System.Drawing.Rectangle(0, maxY, contentView.Viewport.Width, contentView.Viewport.Height);
                e.Handled = true;
                return;
        }
    };

    // Handle mouse wheel scrolling
    contentView.MouseEvent += (s, e) =>
    {
        if (e.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            contentView.ScrollVertical(3);
            e.Handled = true;
            return;
        }
        else if (e.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            contentView.ScrollVertical(-3);
            e.Handled = true;
            return;
        }
    };

    // === Bottom buttons ===
    var logButton = new Button() { Text = "Log to Jira" };
    var copyButton = new Button() { Text = "Copy" };
    var closeButton = new Button() { Text = "Close" };

    copyButton.Accepting += (s, e) => e.Handled = true;
    copyButton.Accepted += (s, e) => ClipboardService.SetText(string.Join('\n', contentView.GetSubViews().OfType<Label>().Select(label => label.Text)));

    closeButton.Accepting += (s, e) => e.Handled = true;
    closeButton.Accepted += (s, e) => app.RequestStop();

    // Layout buttons dynamically
    var spacing = 2;
    var padding = 4;
    var totalWidth = logButton.Text.Length + copyButton.Text.Length + closeButton.Text.Length + padding * 3 + spacing * 2;
    var startX = (app.Screen.Width - totalWidth) / 2;

    logButton.X = startX;
    copyButton.X = Pos.Right(logButton) + spacing;
    closeButton.X = Pos.Right(copyButton) + spacing;

    logButton.Y = Pos.AnchorEnd(2);
    copyButton.Y = Pos.AnchorEnd(2);
    closeButton.Y = Pos.AnchorEnd(2);

    resultWindow.Add(logButton, copyButton, closeButton);

    // Esc closes the window
    resultWindow.KeyDown += (s, e) =>
    {
        if (e.KeyCode is KeyCode.Esc)
        {
            app.RequestStop();
            e.Handled = true;
        }
    };

    // Set initial focus to content view for immediate scrolling
    contentView.SetFocus();

    // Run the results window
    app.Run(resultWindow);
};

app.Run(mainWindow);
mainWindow.Dispose();