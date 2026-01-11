using System.Diagnostics.CodeAnalysis;

using NGitLab;

using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using TextCopy;

using Timecheat;
using Timecheat.UI;

ConfigurationManager.Enable(ConfigLocations.All);

using IApplication app = Application.Create();

app.Init();

using var mainWindow = new Window
{
    X = 0,
    Y = 0,
    Width = Dim.Fill(),
    Height = Dim.Fill(),
    Title = " timecheat "
};

var y = 1;

// GitLab token
var tokenLabel = new Label { X = 1, Y = y, Text = "GitLab token" };
var tokenField = new TextField
{
    X = 25,
    Y = y,
    Width = 40,
    Secret = true
};
mainWindow.Add(tokenLabel, tokenField);

// Credential store: create and attempt to reload saved token
var credentialStore = CredentialStore.Create();
try { tokenField.Text = credentialStore?.Get("timecheat", "gitlab-token") ?? ""; }
catch (CredentialStoreException) { }

y += 2;

// GitLab project
var projectButton = new Button { X = 25, Y = y, Text = "Select" };
var projectLabel = new Label { X = 1, Y = y, Text = "GitLab project" };
var projectPathLabel = new Label { X = 36, Y = y, Text = "<none>" };

var selectedProjectId = 0L;
var selectedProjectPath = (string?)null;

mainWindow.Add(projectLabel, projectButton, projectPathLabel);
y += 2;

// Author (username)
var authorLabel = new Label { X = 1, Y = y, Text = "Author (username)" };
var authorField = new TextField { X = 25, Y = y, Width = 30 };
mainWindow.Add(authorLabel, authorField);
y += 2;

// Issue prefix
var prefixLabel = new Label { X = 1, Y = y, Text = "Issue prefix" };
var prefixField = new TextField { X = 25, Y = y, Width = 30 };
mainWindow.Add(prefixLabel, prefixField);
y += 2;

var today = DateTime.Now.Date;

// Start date
var startLabel = new Label { X = 1, Y = y, Text = "Start date" };
var startField = new DateField
{
    X = 25,
    Y = y,
    Width = 12,
    Date = today.AddDays(-(int)today.DayOfWeek)
};
mainWindow.Add(startLabel, startField);
y += 2;

// End date
var endLabel = new Label { X = 1, Y = y, Text = "End date" };
var endField = new DateField
{
    X = 25,
    Y = y,
    Width = 12,
    Date = today.AddDays(6 - (int)today.DayOfWeek)
};
mainWindow.Add(endLabel, endField);
y += 3;

startField.WithDatePicker(app);
endField.WithDatePicker(app);

// Project picker
projectButton.Accepting += (s, e) => e.Handled = true;
projectButton.Accepted += (s, e) =>
{
    var token = tokenField.Text?.ToString()?.Trim();
    if (string.IsNullOrEmpty(token))
    {
        MessageBox.ErrorQuery(app, "Error", "Please enter a GitLab token first", "OK");
        return;
    }

    // Persist token
    try { credentialStore?.Set("timecheat", "gitlab-token", token); }
    catch (CredentialStoreException) { }

    if (!Try(() => new GitLabClient("https://gitlab.com", token), out var client))
    {
        MessageBox.ErrorQuery(app, "Error", "Failed to create GitLab client", "OK");
        return;
    }

    if (!Try(() => client.Projects.Accessible.OrderBy(p => p.PathWithNamespace).ToList(), out var projects))
    {
        MessageBox.ErrorQuery(app, "Error", "Failed to connect to GitLab", "OK");
        return;
    }

    var listView = new ListView
    {
        Width = Dim.Fill(),
        Height = Dim.Fill()
    };

    listView.SetSource<string>(new(projects.Select(p => p.PathWithNamespace)));

    var dialog = new Dialog { Title = " Select project ", Width = 48, Height = 12 };

    dialog.Add(listView);

    listView.OpenSelectedItem += (s, e) =>
    {
        if (e.Item is not { } item)
            return;

        var project = projects[item];
        selectedProjectId = project.Id;
        selectedProjectPath = project.PathWithNamespace;
        projectPathLabel.Text = selectedProjectPath;

        if (string.IsNullOrWhiteSpace(authorField.Text?.ToString()))
        {
            var user = client.Users.Current;

            authorField.Text = user.Username ?? user.Name ?? "";
        }

        if (string.IsNullOrWhiteSpace(prefixField.Text?.ToString()))
        {
            prefixField.Text =
                client.DetectIssuePatternFromMergeRequests(selectedProjectId);
        }

        app.RequestStop();
    };

    app.Run(dialog);
};

// GitLab cache
var cache = new GitLabCache("timecheat");

// Process commits button
using var processButton = new Button { X = 1, Y = y, Text = "Process Commits" };
mainWindow.Add(processButton);

processButton.Accepting += (s, e) => e.Handled = true;
processButton.Accepted += (s, e) =>
{
    if (selectedProjectId == 0)
    {
        MessageBox.ErrorQuery(app, "Error", "No GitLab project selected", "OK");
        return;
    }

    var token = tokenField.Text?.ToString()?.Trim();
    if (string.IsNullOrEmpty(token))
    {
        MessageBox.ErrorQuery(app, "Error", "No GitLab token specified", "OK");
        return;
    }

    // Persist token
    try { credentialStore?.Set("timecheat", "gitlab-token", token); }
    catch (CredentialStoreException) { }

    var prefix = prefixField.Text?.ToString()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(prefix))
    {
        MessageBox.ErrorQuery(app, "Error", "Invalid issue prefix", "OK");
        return;
    }

    if (startField.Date is not { } startDate || endField.Date is not { } endDate || startDate > endDate)
    {
        MessageBox.ErrorQuery(app, "Error", "Please enter valid start and end dates", "OK");
        return;
    }

    if (!Try(() => new GitLabClient("https://gitlab.com", token), out var client))
    {
        MessageBox.ErrorQuery(app, "Error", "Failed to create GitLab client", "OK");
        return;
    }

    if (!Try(() => client.CollectCommitsFromMergeRequests(selectedProjectId, authorField.Text, startDate, endDate, cache), out var commits))
    {
        MessageBox.ErrorQuery(app, "Error", "Failed to connect to GitLab", "OK");
        return;
    }

    var generator = new TimesheetGenerator(commits);
    var timesheet = generator.TimesheetFor(startDate, endDate);

    using var resultWindow = new Window
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        Title = $" Timesheet — {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} "
    };

    var contentView = new View
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
        var label = new Label { X = 0, Y = ry, Text = text };
        contentView.Add(label);
        ry++;
        contentWidth = Math.Max(contentWidth, text.Length);
    }

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
                    AddLine($"    {task.Hours:F1}h - {task.Title}");
            }

            AddLine("");
        }
    }

    AddLine(new string('-', 50));
    AddLine($"Total tracked hours: {totalHours:F1}h");
    var workDays = timesheet.Days.Count(d => d.TrackedTasks.Count is not 0);
    if (workDays > 0)
        AddLine($"Average hours/day: {totalHours / workDays:F1}h");

    contentView.Scrollable().SetContentSize(new System.Drawing.Size(contentWidth, ry));

    var logButton = new Button() { Text = "Log to Jira" };
    var copyButton = new Button() { Text = "Copy" };
    var closeButton = new Button() { Text = "Close" };

    copyButton.Accepting += (s, e) => e.Handled = true;
    copyButton.Accepted += (s, e) => ClipboardService.SetText(string.Join('\n', contentView.GetSubViews().OfType<Label>().Select(label => label.Text)));

    closeButton.Accepting += (s, e) => e.Handled = true;
    closeButton.Accepted += (s, e) => app.RequestStop();

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

    resultWindow.KeyDown += (s, e) =>
    {
        if (e.KeyCode is KeyCode.Esc)
        {
            app.RequestStop();
            e.Handled = true;
        }
    };

    contentView.SetFocus();

    app.Run(resultWindow);
};

app.Run(mainWindow);

bool Try<T>(Func<T> func, [NotNullWhen(true)] out T value)
{
    try
    {
        value = func();
        return value is not null;
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        value = default!;
        return false;
    }
}