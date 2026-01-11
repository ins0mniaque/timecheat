namespace Timecheat;

internal sealed class TimesheetGenerator(IReadOnlyList<CommitInfo> commits)
{
    public IReadOnlyList<CommitInfo> Commits { get; } = commits;

    public Timesheet TimesheetFor(DateTime startDate, DateTime endDate)
    {
        var commits = Commits.Where(c => c.DateTime.Date >= startDate.Date && c.DateTime.Date <= endDate.Date).ToList();
        var dailyWork = TaskTimeEstimator.EstimateHours(commits, startDate, endDate);

        return new Timesheet
        {
            StartDate = startDate,
            EndDate = endDate,
            Days = [.. dailyWork.Where(d => d.Key.Date >= startDate.Date &&
                                            d.Key.Date <= endDate.Date)
                                .Select(d => new TimesheetDay
                                             {
                                                 Date = d.Key,
                                                 TrackedTasks = d.Value.TrackedTasks,
                                                 UntrackedTasks = d.Value.UntrackedTasks
                                             })],
            TotalCommits = commits.Count,
            TrackedCommits = commits.Count(c => c.HasIssue),
            TaskCount = commits.Where(c => c.HasIssue)
                               .Select(c => c.IssueId)
                               .Distinct()
                               .Count()
        };
    }
}

/// <summary>
/// Configuration constants for time estimation
/// </summary>
internal static class Estimation
{
    // Weekly targets
    public const double TargetWeeklyHours = 85.0;
    public const double MinWeeklyHours = 75.0;
    public const double MaxWeeklyHours = 95.0;

    // Daily limits
    public const double MaxHoursPerTask = 8.0;
    public const double MinHoursPerTask = 0.5;
    public const double MaxHoursPerDay = 16.0;  // Hard cap per day
    public const double LightDayThreshold = 4.0;
    public const double HeavyDayThreshold = 7.0;

    // Backfill settings
    public const int MaxBackfillDayGap = 2;
    public const double BackfillMinAmount = 1.5;  // Increased from 1.0
    public const double LargeTaskBackfillPct = 0.40;  // Reduced from 0.50
    public const double MediumTaskBackfillPct = 0.35;  // Reduced from 0.45
    public const double SmallTaskBackfillPct = 0.30;  // Reduced from 0.40

    // Scaling limits
    public const double MaxScaleFactor = 1.5;
    public const double MinScaleFactor = 0.7;
    public const double ScaleThreshold = 0.15; // Only scale if >15% off target

    // Task type multipliers
    internal static class TaskTypeMultipliers
    {
        public const double Clean = 0.25;     // Deletions/cleanup are fast
        public const double Fix = 0.7;        // Fixes are usually quick
        public const double Build = 0.4;      // Build/pipeline fixes are mechanical
        public const double Version = 0.2;    // Version bumps are trivial
        public const double Refactor = 1.2;   // Refactors take longer than line count suggests
        public const double Test = 1.1;       // Tests need thought
        public const double Feature = 1.0;    // Baseline
        public const double Infrastructure = 1.4; // Setup/config is time-consuming
        public const double Database = 1.3;   // Database/storage work needs care
    }

    // Line count thresholds for first-day estimates
    internal static class LineThresholds
    {
        public const int Tiny = 10;
        public const int Small = 30;
        public const int SmallMedium = 80;
        public const int Medium = 150;
        public const int MediumLarge = 300;
        public const int Large = 500;
        public const int VeryLarge = 800;
        public const int Huge = 1500;
    }

    // Hour estimates for different sizes
    internal static class HourEstimates
    {
        public const double Tiny = 0.5;
        public const double Small = 1.0;
        public const double SmallMedium = 1.5;
        public const double Medium = 2.5;
        public const double MediumLarge = 3.5;
        public const double Large = 4.5;
        public const double VeryLarge = 6.0;
        public const double Huge = 8.0;
        public const double Massive = 10.0;
    }

    // Follow-up day thresholds
    internal static class FollowUpThresholds
    {
        public const int Small = 50;
        public const int Medium = 150;
        public const int Large = 300;
    }

    // Follow-up hour estimates
    internal static class FollowUpHours
    {
        public const double Minimal = 0.5;
        public const double Small = 1.0;
        public const double Medium = 1.5;
        public const double Large = 2.0;
    }

    // Complexity bonuses
    public const int FileCountBonusThreshold1 = 5;
    public const int FileCountBonusThreshold2 = 10;
    public const double FileCountBonus = 0.5;
}

/// <summary>
/// Detects task characteristics from commit messages and metadata
/// </summary>
internal static class TaskCharacteristics
{
    internal enum TaskType
    {
        Feature,
        Fix,
        Clean,
        Build,
        Version,
        Refactor,
        Test,
        Infrastructure,
        Database
    }

    public static TaskType DetectTaskType(string title, string? message, int linesDeleted, int linesAdded)
    {
        var text = $"{title} {message}".ToUpperInvariant();

        // Version bumps - most trivial
        if (text.Contains("BUMP VERSION", StringComparison.Ordinal) ||
            text.Contains("VERSION BUMP", StringComparison.Ordinal))
            return TaskType.Version;

        // Build/pipeline fixes - mechanical
        if ((text.Contains("BUILD", StringComparison.Ordinal) ||
             text.Contains("PIPELINE", StringComparison.Ordinal) ||
             text.Contains("CI", StringComparison.Ordinal)) &&
            (text.Contains("FIX", StringComparison.Ordinal) ||
             text.Contains("ERROR", StringComparison.Ordinal)))
            return TaskType.Build;

        // Clean tasks: high deletions, keywords
        if (text.Contains("CLEAN", StringComparison.Ordinal) ||
            text.Contains("CLEANUP", StringComparison.Ordinal) ||
            text.Contains("REMOVE", StringComparison.Ordinal) ||
            text.Contains("DELETE", StringComparison.Ordinal))
        {
            // Only if actually deleting more than adding
            if (linesDeleted > linesAdded || text.Contains("CLEAN UP", StringComparison.Ordinal))
                return TaskType.Clean;
        }

        // Database/storage work
        if (text.Contains("DATABASE", StringComparison.Ordinal) ||
            text.Contains("MONGODB", StringComparison.Ordinal) ||
            text.Contains("SQLITE", StringComparison.Ordinal) ||
            text.Contains("REPLICAT", StringComparison.Ordinal) ||
            text.Contains("SYNC", StringComparison.Ordinal) ||
            text.Contains("STORE", StringComparison.Ordinal) ||
            text.Contains("STORAGE", StringComparison.Ordinal))
            return TaskType.Database;

        // Fix tasks - but not build fixes (already handled)
        if (text.Contains("FIX", StringComparison.Ordinal) ||
            text.Contains("BUG", StringComparison.Ordinal) ||
            text.Contains("CRASH", StringComparison.Ordinal) ||
            text.Contains("ISSUE", StringComparison.Ordinal) ||
            text.Contains("ERROR", StringComparison.Ordinal))
            return TaskType.Fix;

        // Refactor tasks
        if (text.Contains("REFACTOR", StringComparison.Ordinal) ||
            text.Contains("REORGANIZE", StringComparison.Ordinal) ||
            text.Contains("RESTRUCTURE", StringComparison.Ordinal) ||
            text.Contains("REPLACE", StringComparison.Ordinal) ||
            text.Contains("CONSOLIDATE", StringComparison.Ordinal) ||
            text.Contains("MIGRATE", StringComparison.Ordinal) ||
            text.Contains("MOVE", StringComparison.Ordinal))
            return TaskType.Refactor;

        // Test tasks
        if (text.Contains("TEST", StringComparison.Ordinal) ||
            text.Contains("SPEC", StringComparison.Ordinal) ||
            text.Contains("COVERAGE", StringComparison.Ordinal))
            return TaskType.Test;

        // Infrastructure
        if (text.Contains("PIPELINE", StringComparison.Ordinal) ||
            text.Contains("DEPLOY", StringComparison.Ordinal) ||
            text.Contains("DOCKER", StringComparison.Ordinal) ||
            text.Contains("CONFIG", StringComparison.Ordinal) ||
            text.Contains("SETUP", StringComparison.Ordinal) ||
            text.Contains("DAEMON", StringComparison.Ordinal) ||
            text.Contains("SERVER", StringComparison.Ordinal))
            return TaskType.Infrastructure;

        return TaskType.Feature;
    }

    public static double GetTypeMultiplier(TaskType type)
    {
        return type switch
        {
            TaskType.Clean => Estimation.TaskTypeMultipliers.Clean,
            TaskType.Fix => Estimation.TaskTypeMultipliers.Fix,
            TaskType.Build => Estimation.TaskTypeMultipliers.Build,
            TaskType.Version => Estimation.TaskTypeMultipliers.Version,
            TaskType.Refactor => Estimation.TaskTypeMultipliers.Refactor,
            TaskType.Test => Estimation.TaskTypeMultipliers.Test,
            TaskType.Infrastructure => Estimation.TaskTypeMultipliers.Infrastructure,
            TaskType.Database => Estimation.TaskTypeMultipliers.Database,
            _ => Estimation.TaskTypeMultipliers.Feature
        };
    }
}

internal static class TaskTimeEstimator
{
    public static Dictionary<DateTime, DayWork> EstimateHours(
        IReadOnlyList<CommitInfo> commits,
        DateTime startDate,
        DateTime endDate)
    {
        var effectiveStartDate = startDate.AddDays(-7);
        var dailyWork = InitializeDailyWork(effectiveStartDate, endDate);

        var activeCommits = commits.Where(c => !c.IsDuplicate).ToList();
        var taskLifecycles = BuildTaskLifecycles(activeCommits);
        var dailyEstimates = EstimateTasksByDay(activeCommits, taskLifecycles);

        SmartBackfillEmptyDays(dailyEstimates, taskLifecycles);
        AssignToDailyWork(dailyWork, dailyEstimates);
        ScaleToWeeklyTarget(dailyWork, effectiveStartDate, endDate);

        return dailyWork;
    }

    private static Dictionary<DateTime, DayWork> InitializeDailyWork(DateTime start, DateTime end)
    {
        var dailyWork = new Dictionary<DateTime, DayWork>();
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            dailyWork[date] = new DayWork();
        return dailyWork;
    }

    private static Dictionary<string, TaskLifecycle> BuildTaskLifecycles(List<CommitInfo> commits)
    {
        var lifecycles = new Dictionary<string, TaskLifecycle>();

        var taskGroups = commits.Where(c => c.HasIssue).GroupBy(c => c.TaskId);

        foreach (var group in taskGroups)
        {
            var taskCommits = group.OrderBy(c => c.DateTime).ToList();
            var commitsByDay = taskCommits
                .GroupBy(c => c.DateTime.Date)
                .OrderBy(g => g.Key)
                .ToList();

            lifecycles[group.Key] = new TaskLifecycle
            {
                TaskId = group.Key,
                FirstCommitDate = taskCommits.First().DateTime.Date,
                AllCommits = taskCommits,
                DayCount = commitsByDay.Count,
                CommitDates = [.. commitsByDay.Select(g => g.Key)]
            };
        }

        return lifecycles;
    }

    private static Dictionary<DateTime, List<TaskEstimate>> EstimateTasksByDay(
        List<CommitInfo> activeCommits,
        Dictionary<string, TaskLifecycle> lifecycles)
    {
        var dailyEstimates = new Dictionary<DateTime, List<TaskEstimate>>();
        var commitsByDate = activeCommits.GroupBy(c => c.DateTime.Date).OrderBy(g => g.Key);

        foreach (var dayGroup in commitsByDate)
        {
            var date = dayGroup.Key;
            var dayCommits = dayGroup.OrderBy(c => c.DateTime).ToList();
            var estimates = new List<TaskEstimate>();

            // Process tracked tasks
            var trackedGroups = dayCommits.Where(c => c.HasIssue).GroupBy(c => c.TaskId);

            foreach (var taskGroup in trackedGroups)
            {
                var taskId = taskGroup.Key;
                var taskCommits = taskGroup.ToList();
                var firstCommit = taskCommits.First();
                var lifecycle = lifecycles[taskId];

                var hours = EstimateTaskHoursForDay(
                    taskCommits,
                    lifecycle.FirstCommitDate == date,
                    firstCommit.Title,
                    firstCommit.Message);

                estimates.Add(new TaskEstimate(
                    firstCommit.Title,
                    taskId,
                    firstCommit.IssueId,
                    true,
                    hours,
                    taskCommits.First().DateTime,
                    taskCommits
                ));
            }

            // Process untracked tasks
            var untrackedGroups = dayCommits.Where(c => !c.HasIssue).GroupBy(c => c.TaskId);

            foreach (var taskGroup in untrackedGroups)
            {
                var taskCommits = taskGroup.ToList();
                var firstCommit = taskCommits.First();
                var hours = EstimateUntrackedTask(taskCommits, firstCommit.Title, firstCommit.Message);

                estimates.Add(new TaskEstimate(
                    firstCommit.Title,
                    taskGroup.Key,
                    null,
                    false,
                    hours,
                    taskCommits.First().DateTime,
                    taskCommits
                ));
            }

            dailyEstimates[date] = estimates;
        }

        return dailyEstimates;
    }

    private static double EstimateTaskHoursForDay(
        List<CommitInfo> dayCommits,
        bool isFirstDay,
        string title,
        string message)
    {
        var totalLines = dayCommits.Sum(c => c.LinesAdded + c.LinesDeleted);
        var totalFiles = dayCommits.Sum(c => c.FilesChanged);
        var linesAdded = dayCommits.Sum(c => c.LinesAdded);
        var linesDeleted = dayCommits.Sum(c => c.LinesDeleted);

        // Detect task type
        var taskType = TaskCharacteristics.DetectTaskType(title, message, linesDeleted, linesAdded);
        var typeMultiplier = TaskCharacteristics.GetTypeMultiplier(taskType);

        if (isFirstDay)
        {
            // Base estimation on line count
            var baseHours = totalLines is 0 && totalFiles is 0 ? Estimation.HourEstimates.Tiny :
                            totalLines <= Estimation.LineThresholds.Tiny ? Estimation.HourEstimates.Tiny :
                            totalLines <= Estimation.LineThresholds.Small ? Estimation.HourEstimates.Small :
                            totalLines <= Estimation.LineThresholds.SmallMedium ? Estimation.HourEstimates.SmallMedium :
                            totalLines <= Estimation.LineThresholds.Medium ? Estimation.HourEstimates.Medium :
                            totalLines <= Estimation.LineThresholds.MediumLarge ? Estimation.HourEstimates.MediumLarge :
                            totalLines <= Estimation.LineThresholds.Large ? Estimation.HourEstimates.Large :
                            totalLines <= Estimation.LineThresholds.VeryLarge ? Estimation.HourEstimates.VeryLarge :
                            totalLines <= Estimation.LineThresholds.Huge ? Estimation.HourEstimates.Huge :
                                                                                 Estimation.HourEstimates.Massive;

            // Apply task type multiplier
            baseHours *= typeMultiplier;

            // Complexity bonuses
            if (totalFiles > Estimation.FileCountBonusThreshold1)
                baseHours += Estimation.FileCountBonus;
            if (totalFiles > Estimation.FileCountBonusThreshold2)
                baseHours += Estimation.FileCountBonus;

            return Math.Round(baseHours * 2) / 2;
        }
        else
        {
            // Follow-up days
            var baseHours = Estimation.FollowUpHours.Minimal;

            if (totalLines > Estimation.FollowUpThresholds.Small)
                baseHours = Estimation.FollowUpHours.Small;
            if (totalLines > Estimation.FollowUpThresholds.Medium)
                baseHours = Estimation.FollowUpHours.Medium;
            if (totalLines > Estimation.FollowUpThresholds.Large)
                baseHours = Estimation.FollowUpHours.Large;

            // Apply reduced multiplier for follow-ups
            baseHours *= Math.Min(typeMultiplier, 1.0);

            return Math.Round(baseHours * 2) / 2;
        }
    }

    private static double EstimateUntrackedTask(List<CommitInfo> commits, string title, string message)
    {
        var totalLines = commits.Sum(c => c.LinesAdded + c.LinesDeleted);
        var linesAdded = commits.Sum(c => c.LinesAdded);
        var linesDeleted = commits.Sum(c => c.LinesDeleted);

        var taskType = TaskCharacteristics.DetectTaskType(title, message, linesDeleted, linesAdded);
        var typeMultiplier = TaskCharacteristics.GetTypeMultiplier(taskType);

        var hours = totalLines is 0 ? Estimation.HourEstimates.Tiny :
                    totalLines <= 20 ? 1.0 :
                    totalLines <= 80 ? 2.0 :
                    totalLines <= 200 ? 3.0 :
                    totalLines <= 500 ? 4.5 :
                                        6.0;

        hours *= typeMultiplier;
        return Math.Round(hours * 2) / 2;
    }

    private static void AssignToDailyWork(
        Dictionary<DateTime, DayWork> dailyWork,
        Dictionary<DateTime, List<TaskEstimate>> dailyEstimates)
    {
        foreach (var date in dailyEstimates.Keys)
        {
            if (!dailyWork.TryGetValue(date, out var value))
                dailyWork[date] = value = new();

            var trackedEstimates = dailyEstimates[date].Where(t => t.HasIssue).ToList();
            var untrackedEstimates = dailyEstimates[date].Where(t => !t.HasIssue).ToList();

            // Calculate total before capping
            var totalTracked = trackedEstimates.Sum(t => t.Hours);
            var totalUntracked = untrackedEstimates.Sum(t => t.Hours);
            var grandTotal = totalTracked + totalUntracked;

            // If day exceeds max hours, scale down proportionally BEFORE weekly scaling
            if (grandTotal > Estimation.MaxHoursPerDay)
            {
                var scaleFactor = Estimation.MaxHoursPerDay / grandTotal;

                foreach (var estimate in trackedEstimates)
                {
                    estimate.Hours *= scaleFactor;
                    estimate.Hours = Math.Round(estimate.Hours * 2) / 2;
                    estimate.Hours = Math.Max(Estimation.MinHoursPerTask, estimate.Hours);
                }

                foreach (var estimate in untrackedEstimates)
                {
                    estimate.Hours *= scaleFactor;
                    estimate.Hours = Math.Round(estimate.Hours * 2) / 2;
                    estimate.Hours = Math.Max(Estimation.MinHoursPerTask, estimate.Hours);
                }
            }

            foreach (var estimate in trackedEstimates)
            {
                value.TrackedTasks.Add(new TaskWork
                {
                    Title = estimate.Title,
                    TaskId = estimate.TaskId,
                    IssueId = estimate.IssueId,
                    Hours = estimate.Hours,
                    StartTime = estimate.StartTime,
                    Commits = estimate.Commits.Count
                });
            }

            foreach (var estimate in untrackedEstimates)
            {
                value.UntrackedTasks.Add(new TaskWork
                {
                    Title = estimate.Title,
                    TaskId = estimate.TaskId,
                    IssueId = null,
                    Hours = estimate.Hours,
                    StartTime = estimate.StartTime,
                    Commits = estimate.Commits.Count
                });
            }
        }
    }

    private static void SmartBackfillEmptyDays(
        Dictionary<DateTime, List<TaskEstimate>> dailyEstimates,
        Dictionary<string, TaskLifecycle> taskLifecycles)
    {
        var sortedDates = dailyEstimates.Keys.OrderBy(d => d).ToList();

        for (var i = 0; i < sortedDates.Count - 1; i++)
        {
            var currentDay = sortedDates[i];
            var nextDay = sortedDates[i + 1];

            var dayGap = (nextDay - currentDay).Days;
            if (dayGap > Estimation.MaxBackfillDayGap)
                continue;

            var currentDayHours = dailyEstimates.TryGetValue(currentDay, out var value)
                ? value.Where(t => t.HasIssue).Sum(t => t.Hours)
                : 0;

            if (currentDayHours >= Estimation.LightDayThreshold)
                continue;

            if (!dailyEstimates.ContainsKey(nextDay))
                continue;

            var nextDayEstimates = dailyEstimates[nextDay].Where(t => t.HasIssue).ToList();
            var nextDayHours = nextDayEstimates.Sum(t => t.Hours);

            if (nextDayHours <= Estimation.HeavyDayThreshold)
                continue;

            var backfillCandidates = nextDayEstimates
                .Where(t => t.Hours >= 2.5)
                .Where(t => {
                    var lifecycle = taskLifecycles[t.TaskId];
                    return lifecycle.FirstCommitDate == nextDay;
                })
                .OrderByDescending(t => t.Hours)
                .ToList();

            if (backfillCandidates.Count is 0)
                continue;

            var hoursNeeded = Math.Min(
                Estimation.MaxHoursPerTask - currentDayHours,
                nextDayHours - Estimation.HeavyDayThreshold);
            hoursNeeded = Math.Max(0, hoursNeeded);

            if (hoursNeeded < Estimation.BackfillMinAmount)
                continue;

            if (!dailyEstimates.ContainsKey(currentDay))
                dailyEstimates[currentDay] = [];

            var backfilled = 0.0;
            foreach (var candidate in backfillCandidates)
            {
                if (backfilled >= hoursNeeded)
                    break;

                var lifecycle = taskLifecycles[candidate.TaskId];
                var totalLines = lifecycle.AllCommits.Sum(c => c.LinesAdded + c.LinesDeleted);

                var backfillPct = totalLines >= 500 ? Estimation.LargeTaskBackfillPct :
                                    totalLines >= 300 ? Estimation.MediumTaskBackfillPct :
                                                        Estimation.SmallTaskBackfillPct;

                var hoursToBackfill = candidate.Hours * backfillPct;
                hoursToBackfill = Math.Min(hoursToBackfill, hoursNeeded - backfilled);
                hoursToBackfill = Math.Round(hoursToBackfill * 2) / 2;

                if (hoursToBackfill < Estimation.BackfillMinAmount)
                    continue;

                dailyEstimates[currentDay].Add(new TaskEstimate(
                    candidate.Title,
                    candidate.TaskId,
                    candidate.IssueId,
                    true,
                    hoursToBackfill,
                    currentDay.AddHours(9),
                    []
                ));

                candidate.Hours -= hoursToBackfill;
                candidate.Hours = Math.Max(Estimation.MinHoursPerTask, candidate.Hours);
                candidate.Hours = Math.Round(candidate.Hours * 2) / 2;

                backfilled += hoursToBackfill;
            }
        }
    }

    private static void ScaleToWeeklyTarget(
        Dictionary<DateTime, DayWork> dailyWork,
        DateTime startDate,
        DateTime endDate)
    {
        var weeks = new Dictionary<int, List<DateTime>>();

        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            if (!dailyWork.ContainsKey(date))
                continue;

            var weekNum = GetIsoWeekNumber(date);
            if (!weeks.ContainsKey(weekNum))
                weeks[weekNum] = [];
            weeks[weekNum].Add(date);
        }

        foreach (var week in weeks)
        {
            var weekDays = week.Value;
            var currentWeekHours = weekDays.Sum(d =>
                dailyWork[d].TrackedTasks.Sum(t => t.Hours));

            if (currentWeekHours is 0)
                continue;

            var daysWithWork = weekDays.Count(d => dailyWork[d].TrackedTasks.Count is not 0);
            if (daysWithWork is 0)
                continue;

            var targetForWeek = Estimation.TargetWeeklyHours;
            if (daysWithWork < 5)
                targetForWeek = Estimation.TargetWeeklyHours * (daysWithWork / 5.0);

            var scaleFactor = targetForWeek / currentWeekHours;
            scaleFactor = Math.Max(
                Estimation.MinScaleFactor,
                Math.Min(scaleFactor, Estimation.MaxScaleFactor));

            if (Math.Abs(scaleFactor - 1.0) < Estimation.ScaleThreshold)
                continue;

            foreach (var day in weekDays)
            {
                foreach (var task in dailyWork[day].TrackedTasks)
                {
                    task.Hours *= scaleFactor;
                    task.Hours = Math.Round(task.Hours * 2) / 2;
                    task.Hours = Math.Max(
                        Estimation.MinHoursPerTask,
                        Math.Min(task.Hours, Estimation.MaxHoursPerTask));
                }

                foreach (var task in dailyWork[day].UntrackedTasks)
                {
                    task.Hours *= scaleFactor;
                    task.Hours = Math.Round(task.Hours * 2) / 2;
                    task.Hours = Math.Max(
                        Estimation.MinHoursPerTask,
                        Math.Min(task.Hours, Estimation.MaxHoursPerTask));
                }
            }
        }
    }

    private static int GetIsoWeekNumber(DateTime date)
    {
        var day = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(date);
        if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
            date = date.AddDays(3);

        return System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
            date,
            System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);
    }

    private sealed class TaskLifecycle
    {
        public string TaskId { get; set; } = "";
        public DateTime FirstCommitDate { get; set; }
        public List<CommitInfo> AllCommits { get; set; } = [];
        public int DayCount { get; set; }
        public List<DateTime> CommitDates { get; set; } = [];
    }

    private sealed class TaskEstimate(string title, string taskId, string? issueId, bool hasIssue, double hours, DateTime startTime, List<CommitInfo> commits)
    {
        public string Title { get; set; } = title;
        public string TaskId { get; set; } = taskId;
        public string? IssueId { get; set; } = issueId;
        public bool HasIssue { get; set; } = hasIssue;
        public double Hours { get; set; } = hours;
        public DateTime StartTime { get; set; } = startTime;
        public List<CommitInfo> Commits { get; set; } = commits;
    }
}

internal sealed class CommitInfo
{
    public string Title { get; set; } = "";
    public string Sha { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string? IssueId { get; set; }
    public bool HasIssue { get; set; }
    public bool IsMergeCommit { get; set; }
    public bool IsDuplicate { get; set; }
    public DateTime DateTime { get; set; }
    public string Message { get; set; } = "";
    public string OriginalMessage { get; set; } = "";
    public int FilesChanged { get; set; }
    public int LinesAdded { get; set; }
    public int LinesDeleted { get; set; }
}

internal sealed class DayWork
{
    public List<TaskWork> TrackedTasks { get; set; } = [];
    public List<TaskWork> UntrackedTasks { get; set; } = [];
}

internal sealed class Timesheet
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<TimesheetDay> Days { get; set; } = [];
    public int TotalCommits { get; set; }
    public int TrackedCommits { get; set; }
    public int TaskCount { get; set; }
}

internal sealed class TimesheetDay
{
    public DateTime Date { get; set; }
    public List<TaskWork> TrackedTasks { get; set; } = [];
    public List<TaskWork> UntrackedTasks { get; set; } = [];

    public double TrackedHours => TrackedTasks.Sum(t => t.Hours);
    public double UntrackedHours => UntrackedTasks.Sum(t => t.Hours);
}

internal sealed class TaskWork
{
    public string Title { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string? IssueId { get; set; }
    public double Hours { get; set; }
    public DateTime StartTime { get; set; }
    public int Commits { get; set; }
}