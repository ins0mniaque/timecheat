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

    // Daily limits
    public const double MaxHoursPerTask = 8.0;
    public const double MinHoursPerTask = 0.5;
    public const double MaxHoursPerDay = 16.0;

    // Sleep window - very probably asleep during this time
    public const int SleepStartHour = 3;
    public const int SleepEndHour = 7;

    // Time-based estimation thresholds
    public const double MaxReasonableWorkHours = 12.0;  // Max continuous work session
    public const double MinTaskHours = 0.25;  // Minimum billable time

    // Scaling limits
    public const double MaxScaleFactor = 1.5;
    public const double MinScaleFactor = 0.7;
    public const double ScaleThreshold = 0.15;

    // Task type multipliers (for size-based fallback)
    internal static class TaskTypeMultipliers
    {
        public const double Clean = 0.25;
        public const double Fix = 0.7;
        public const double Build = 0.4;
        public const double Version = 0.2;
        public const double Refactor = 1.2;
        public const double Test = 1.1;
        public const double Feature = 1.0;
        public const double Infrastructure = 1.4;
        public const double Database = 1.3;
    }

    // Line count thresholds (for sanity checks and fallback)
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

    // Hour estimates (for fallback when no previous commit)
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

    public const int FileCountBonusThreshold1 = 5;
    public const int FileCountBonusThreshold2 = 10;
    public const double FileCountBonus = 0.5;
}

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

        if (text.Contains("BUMP VERSION", StringComparison.Ordinal) ||
            text.Contains("VERSION BUMP", StringComparison.Ordinal))
            return TaskType.Version;

        if ((text.Contains("BUILD", StringComparison.Ordinal) ||
             text.Contains("PIPELINE", StringComparison.Ordinal) ||
             text.Contains("CI", StringComparison.Ordinal)) &&
            (text.Contains("FIX", StringComparison.Ordinal) ||
             text.Contains("ERROR", StringComparison.Ordinal)))
            return TaskType.Build;

        if (text.Contains("CLEAN", StringComparison.Ordinal) ||
            text.Contains("CLEANUP", StringComparison.Ordinal) ||
            text.Contains("REMOVE", StringComparison.Ordinal) ||
            text.Contains("DELETE", StringComparison.Ordinal))
        {
            if (linesDeleted > linesAdded || text.Contains("CLEAN UP", StringComparison.Ordinal))
                return TaskType.Clean;
        }

        if (text.Contains("DATABASE", StringComparison.Ordinal) ||
            text.Contains("MONGODB", StringComparison.Ordinal) ||
            text.Contains("SQLITE", StringComparison.Ordinal) ||
            text.Contains("REPLICAT", StringComparison.Ordinal) ||
            text.Contains("SYNC", StringComparison.Ordinal) ||
            text.Contains("STORE", StringComparison.Ordinal) ||
            text.Contains("STORAGE", StringComparison.Ordinal))
            return TaskType.Database;

        if (text.Contains("FIX", StringComparison.Ordinal) ||
            text.Contains("BUG", StringComparison.Ordinal) ||
            text.Contains("CRASH", StringComparison.Ordinal) ||
            text.Contains("ISSUE", StringComparison.Ordinal) ||
            text.Contains("ERROR", StringComparison.Ordinal))
            return TaskType.Fix;

        if (text.Contains("REFACTOR", StringComparison.Ordinal) ||
            text.Contains("REORGANIZE", StringComparison.Ordinal) ||
            text.Contains("RESTRUCTURE", StringComparison.Ordinal) ||
            text.Contains("REPLACE", StringComparison.Ordinal) ||
            text.Contains("CONSOLIDATE", StringComparison.Ordinal) ||
            text.Contains("MIGRATE", StringComparison.Ordinal) ||
            text.Contains("MOVE", StringComparison.Ordinal))
            return TaskType.Refactor;

        if (text.Contains("TEST", StringComparison.Ordinal) ||
            text.Contains("SPEC", StringComparison.Ordinal) ||
            text.Contains("COVERAGE", StringComparison.Ordinal))
            return TaskType.Test;

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

        var activeCommits = commits
            .Where(c => !c.IsDuplicate)
            .OrderBy(c => c.DateTime)
            .ToList();

        var dailyEstimates = EstimateTasksFromCommitTimes(activeCommits);
        BackfillLargeTasksToEmptyDays(dailyEstimates, activeCommits, effectiveStartDate);
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

    private static Dictionary<DateTime, List<TaskEstimate>> EstimateTasksFromCommitTimes(List<CommitInfo> commits)
    {
        var estimates = new List<TaskEstimate>();

        for (int i = 0; i < commits.Count; i++)
        {
            var currentCommit = commits[i];
            var currentTaskCommits = commits.Where(c => c.TaskId == currentCommit.TaskId).ToList();

            // Skip if we've already processed this task
            if (estimates.Any(e => e.TaskId == currentCommit.TaskId))
                continue;

            double hours;

            if (i == 0)
            {
                // First commit ever - assume started work ~2 hours ago
                hours = EstimateFromTimeDelta(TimeSpan.FromHours(2), currentTaskCommits, currentCommit.Title, currentCommit.Message);
            }
            else
            {
                var previousCommit = commits[i - 1];
                var timeDelta = currentCommit.DateTime - previousCommit.DateTime;

                // If this is the first commit after likely sleeping (previous commit was before 3 AM, current is after 7 AM)
                // OR if more than 8 hours passed (likely slept), cap the time delta
                var previousInSleepWindow = previousCommit.DateTime.Hour >= Estimation.SleepStartHour || previousCommit.DateTime.Hour < Estimation.SleepEndHour;
                var currentAfterSleep = currentCommit.DateTime.Hour >= Estimation.SleepEndHour;
                var likelySlept = (previousInSleepWindow && currentAfterSleep) || timeDelta.TotalHours > 8;

                if (likelySlept)
                {
                    // First commit of new work session - assume started ~1-2 hours ago
                    // Use commit size to guess: small commits = less time, larger = more time
                    var totalLines = currentTaskCommits.Sum(c => c.LinesAdded + c.LinesDeleted);
                    var assumedHours = totalLines <= 50 ? 1.0 :
                                      totalLines <= 200 ? 1.5 :
                                      2.0;
                    hours = EstimateFromTimeDelta(TimeSpan.FromHours(assumedHours), currentTaskCommits, currentCommit.Title, currentCommit.Message);
                }
                else
                {
                    hours = EstimateFromTimeDelta(timeDelta, currentTaskCommits, currentCommit.Title, currentCommit.Message);
                }
            }

            estimates.Add(new TaskEstimate(
                currentCommit.Title,
                currentCommit.TaskId,
                currentCommit.IssueId,
                currentCommit.HasIssue,
                hours,
                currentCommit.DateTime,
                currentTaskCommits
            ));
        }

        // Group by date for daily work assignment
        return estimates
            .GroupBy(e => e.StartTime.Date)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static double EstimateFromTimeDelta(
        TimeSpan timeDelta,
        List<CommitInfo> taskCommits,
        string title,
        string message)
    {
        var totalHours = timeDelta.TotalHours;

        // Subtract sleep time if the delta crosses the sleep window
        var sleepHours = CalculateSleepHours(timeDelta);

        totalHours -= sleepHours;

        // Cap at reasonable work session length
        totalHours = Math.Min(totalHours, Estimation.MaxReasonableWorkHours);

        // Sanity check against commit size
        var sizeBasedEstimate = EstimateFromSize(taskCommits, title, message);

        // If time suggests much more work than size indicates, trust size more
        // (e.g. working on something else in between)
        if (totalHours > sizeBasedEstimate * 2.5)
            totalHours = Math.Max(sizeBasedEstimate, totalHours * 0.4);

        // If time suggests much less work than size indicates, trust time but add a bit
        // (e.g. quick fix during bigger task)
        if (totalHours < sizeBasedEstimate * 0.4 && sizeBasedEstimate > 2.0)
            totalHours = Math.Min(sizeBasedEstimate, totalHours * 1.5);

        totalHours = Math.Max(Estimation.MinTaskHours, totalHours);
        totalHours = Math.Min(Estimation.MaxHoursPerTask, totalHours);

        return Math.Round(totalHours * 2) / 2;
    }

    private static double CalculateSleepHours(TimeSpan timeDelta)
    {
        if (timeDelta.TotalHours < 4)
            return 0;

        // Rough heuristic: if delta > 4 hours, assume one sleep period crossed
        // Sleep window is 3 AM to 7 AM (4 hours)
        if (timeDelta.TotalHours >= 4 && timeDelta.TotalHours <= 24)
        {
            return 4.0;  // One sleep period
        }

        if (timeDelta.TotalHours > 24)
        {
            var days = (int)(timeDelta.TotalHours / 24);
            return days * 4.0;  // Multiple sleep periods
        }

        return 0;
    }

    private static double EstimateFromSize(List<CommitInfo> commits, string title, string message)
    {
        var totalLines = commits.Sum(c => c.LinesAdded + c.LinesDeleted);
        var totalFiles = commits.Sum(c => c.FilesChanged);
        var linesAdded = commits.Sum(c => c.LinesAdded);
        var linesDeleted = commits.Sum(c => c.LinesDeleted);

        var taskType = TaskCharacteristics.DetectTaskType(title, message, linesDeleted, linesAdded);
        var typeMultiplier = TaskCharacteristics.GetTypeMultiplier(taskType);

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

        baseHours *= typeMultiplier;

        if (totalFiles > Estimation.FileCountBonusThreshold1)
            baseHours += Estimation.FileCountBonus;
        if (totalFiles > Estimation.FileCountBonusThreshold2)
            baseHours += Estimation.FileCountBonus;

        return Math.Round(baseHours * 2) / 2;
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

            var totalTracked = trackedEstimates.Sum(t => t.Hours);
            var totalUntracked = untrackedEstimates.Sum(t => t.Hours);
            var grandTotal = totalTracked + totalUntracked;

            if (grandTotal > Estimation.MaxHoursPerDay)
            {
                var scaleFactor = Estimation.MaxHoursPerDay / grandTotal;

                foreach (var estimate in trackedEstimates.Concat(untrackedEstimates))
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
                dailyWork[d].TrackedTasks.Sum(t => t.Hours) +
                dailyWork[d].UntrackedTasks.Sum(t => t.Hours));

            if (currentWeekHours is 0)
                continue;

            var daysWithWork = weekDays.Count(d =>
                dailyWork[d].TrackedTasks.Count is not 0 ||
                dailyWork[d].UntrackedTasks.Count is not 0);

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

    private static void BackfillLargeTasksToEmptyDays(
        Dictionary<DateTime, List<TaskEstimate>> dailyEstimates,
        List<CommitInfo> allCommits,
        DateTime startDate)
    {
        var sortedDates = dailyEstimates.Keys.OrderBy(d => d).ToList();

        foreach (var commitDate in sortedDates)
        {
            var tasksOnCommitDay = dailyEstimates[commitDate].ToList();

            foreach (var task in tasksOnCommitDay)
            {
                var taskCommits = allCommits.Where(c => c.TaskId == task.TaskId).ToList();
                var totalLines = taskCommits.Sum(c => c.LinesAdded + c.LinesDeleted);

                // Only backfill substantial tasks
                if (totalLines < 200)
                    continue;

                // Calculate size-based estimate
                var sizeBasedEstimate = EstimateFromSize(taskCommits, task.Title, "");

                // If current time-based estimate is much smaller than size suggests,
                // and there are empty days before this commit, backfill
                if (task.Hours < sizeBasedEstimate * 0.6)
                {
                    var previousDate = sortedDates
                        .Where(d => d < commitDate)
                        .DefaultIfEmpty(startDate.AddDays(-1))
                        .Max();

                    var daysSincePrevious = (commitDate - previousDate).Days;

                    if (daysSincePrevious > 1 && daysSincePrevious <= 4)
                    {
                        // Find empty work days
                        var emptyDays = new List<DateTime>();
                        for (var d = commitDate.AddDays(-1); d > previousDate && emptyDays.Count < daysSincePrevious - 1; d = d.AddDays(-1))
                            if (d >= startDate)
                                emptyDays.Add(d);

                        if (emptyDays.Count > 0)
                        {
                            // Calculate hours to backfill
                            var hoursDeficit = sizeBasedEstimate - task.Hours;
                            var hoursToBackfill = Math.Min(hoursDeficit, sizeBasedEstimate * 0.5);

                            if (hoursToBackfill >= 2.0)
                            {
                                // Distribute evenly across empty days
                                var hoursPerDay = hoursToBackfill / emptyDays.Count;
                                hoursPerDay = Math.Round(hoursPerDay * 2) / 2;

                                if (hoursPerDay >= Estimation.MinHoursPerTask)
                                {
                                    foreach (var day in emptyDays)
                                    {
                                        if (!dailyEstimates.ContainsKey(day))
                                            dailyEstimates[day] = [];

                                        dailyEstimates[day].Add(new TaskEstimate(
                                            task.Title,
                                            task.TaskId,
                                            task.IssueId,
                                            task.HasIssue,
                                            hoursPerDay,
                                            day.AddHours(10),
                                            []
                                        ));
                                    }

                                    // Add remaining hours to commit day
                                    task.Hours += hoursPerDay;
                                    task.Hours = Math.Round(task.Hours * 2) / 2;
                                }
                            }
                        }
                    }
                }
            }
        }
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