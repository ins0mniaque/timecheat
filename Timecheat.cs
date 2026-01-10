using System.Text.RegularExpressions;

using LibGit2Sharp;

namespace Timecheat;

internal sealed partial class TimesheetGenerator
{
    private readonly string _repoPath;
    private readonly string _issuePattern;

    public string? Author { get; set; }

    public TimesheetGenerator(string repoPath, string issuePattern)
    {
        if (!Repository.IsValid(repoPath))
            throw new ArgumentException("Invalid repository path", nameof(repoPath));

        _repoPath = repoPath;
        _issuePattern = issuePattern;
    }

    public Timesheet TimesheetFor(DateTime startDate, DateTime endDate)
    {
        using var repo = new Repository(_repoPath);

        // Look back a week to include previous commits to correctly identify tasks
        var collectFromDate = startDate.AddDays(-7);

        var commits = CollectCommits(repo, collectFromDate, endDate);

        MatchMergeCommits(commits);

        var dailyWork = TaskTimeEstimator.EstimateHours(commits, collectFromDate, endDate);

        return new Timesheet
        {
            StartDate = startDate, // Keep original start date for reporting
            EndDate = endDate,
            Days = [.. dailyWork
            .Where(kvp => kvp.Key >= startDate && kvp.Key <= endDate) // Filter output to requested range
            .Select(kvp => new TimesheetDay
            {
                Date = kvp.Key,
                TrackedTasks = kvp.Value.TrackedTasks,
                UntrackedTasks = kvp.Value.UntrackedTasks
            })],
            TotalCommits = commits.Count,
            TrackedCommits = commits.Count(c => c.HasIssue),
            TaskCount = commits.Where(c => c.HasIssue).Select(c => c.IssueId).Distinct().Count()
        };
    }

    private List<CommitInfo> CollectCommits(Repository repo, DateTime startDate, DateTime endDate)
    {
        var commits = new List<CommitInfo>();
        var processedShas = new HashSet<string>();

        var filter = new CommitFilter
        {
            IncludeReachableFrom = repo.Branches.Where(b => !b.IsRemote),
            SortBy = CommitSortStrategies.Time
        };

        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (!string.IsNullOrEmpty(Author) && !commit.Author.Email.Equals(Author, StringComparison.OrdinalIgnoreCase))
                continue;

            var localTime = commit.Author.When.LocalDateTime;
            if (localTime.Date < startDate || localTime.Date > endDate)
                continue;

            if (processedShas.Contains(commit.Sha))
                continue;

            processedShas.Add(commit.Sha);

            var (taskId, issueId, hasIssue) = ExtractTaskInfo(commit.Message);
            var isMerge = commit.Message.StartsWith("Merge branch", StringComparison.OrdinalIgnoreCase);
            var (filesChanged, linesAdded, linesDeleted) = CalculateCommitStats(repo, commit);

            commits.Add(new CommitInfo
            {
                Title = taskId,
                Sha = commit.Sha,
                TaskId = taskId,
                IssueId = issueId,
                HasIssue = hasIssue,
                IsMergeCommit = isMerge,
                DateTime = localTime,
                Message = commit.MessageShort,
                OriginalMessage = commit.Message,
                FilesChanged = filesChanged,
                LinesAdded = linesAdded,
                LinesDeleted = linesDeleted
            });
        }

        return [.. commits.OrderBy(c => c.DateTime)];
    }

    private static (int filesChanged, int linesAdded, int linesDeleted) CalculateCommitStats(Repository repo, Commit commit)
    {
        if (!commit.Parents.Any())
            return (0, 0, 0);

        var parent = commit.Parents.First();
        var changes = repo.Diff.Compare<Patch>(parent.Tree, commit.Tree);

        return (changes.Count(), changes.Sum(c => c.LinesAdded), changes.Sum(c => c.LinesDeleted));
    }

    private void MatchMergeCommits(List<CommitInfo> commits)
    {
        var mergeCommits = commits.Where(c => c.IsMergeCommit).ToList();
        var regularCommits = commits.Where(c => !c.IsMergeCommit).ToList();

        var normalizedByTask = regularCommits
            .Select(c => c.TaskId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t, NormalizeBranchName, StringComparer.OrdinalIgnoreCase);

        foreach (var merge in mergeCommits)
        {
            var mergeDate = merge.DateTime.Date;
            var branchMatch = MatchBranchRegex().Match(merge.OriginalMessage);
            if (!branchMatch.Success) continue;

            var branchName = branchMatch.Groups[1].Value;
            var normalizedBranch = NormalizeBranchName(branchName);

            var candidates = regularCommits
                .Where(c => c.DateTime.Date >= mergeDate.AddDays(-3) && c.DateTime.Date <= mergeDate)
                .Where(c => !c.IsDuplicate)
                .ToList();

            if (candidates.Count is not 0)
            {
                var bestMatch = candidates
                    .Select(commit =>
                    {
                        var normalized = normalizedByTask.TryGetValue(commit.TaskId, out var n) ? n : NormalizeBranchName(commit.TaskId);

                        return new
                        {
                            Commit = commit,
                            Normalized = normalized,
                            Distance = LevenshteinDistance(normalizedBranch, normalized)
                        };
                    })
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();

                if (bestMatch != null)
                {
                    var maxLength = Math.Max(normalizedBranch.Length, bestMatch.Normalized.Length);
                    var threshold = maxLength / 2;

                    if (bestMatch.Distance <= threshold)
                    {
                        merge.DateTime = bestMatch.Commit.DateTime;
                        merge.Title = bestMatch.Commit.Title;

                        if (merge.HasIssue)
                            bestMatch.Commit.IsDuplicate = true;
                        else
                            merge.IsDuplicate = true;
                    }
                }
            }
        }
    }

    private string NormalizeBranchName(string name)
    {
        name = NormalizeBranchRegex().Replace(name, "");
        name = Regex.Replace(name, _issuePattern + @"[\s\-_]*", "", RegexOptions.IgnoreCase);
        name = name.ToUpperInvariant();
        name = name.Replace('-', ' ').Replace('_', ' ');
        name = WhitespaceRegex().Replace(name, " ").Trim();
        return name;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var n = s.Length;
        var m = t.Length;
        var cols = m + 1;
        var d = new int[(n + 1) * cols];

        for (var i = 0; i <= n; i++)
            d[i * cols + 0] = i;

        for (var j = 0; j <= m; j++)
            d[0 * cols + j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                var idx = i * cols + j;
                var above = (i - 1) * cols + j;
                var left = i * cols + (j - 1);
                var diag = (i - 1) * cols + (j - 1);

                d[idx] = Math.Min(Math.Min(d[above] + 1, d[left] + 1), d[diag] + cost);
            }
        }

        return d[n * cols + m];
    }

    private (string taskId, string? issueId, bool hasIssue) ExtractTaskInfo(string message)
    {
        var mergePatterns = new[]
        {
            @"Merge branch ['\""'](.+?)['\""']",
            @"Merge pull request #\d+ from .+?[/:](.+?)(?:\s|$)",
        };

        foreach (var pattern in mergePatterns)
        {
            var mergeMatch = Regex.Match(message, pattern);
            if (mergeMatch.Success)
            {
                var branchName = mergeMatch.Groups[1].Value;
                var issueMatch = Regex.Match(branchName, $"(?<id>{_issuePattern})", RegexOptions.IgnoreCase);
                if (issueMatch.Success)
                {
                    var issueId = issueMatch.Groups["id"].Value.ToUpperInvariant();
                    var desc = branchName.Replace(issueId, "", StringComparison.OrdinalIgnoreCase).Trim('-', '_', ' ');
                    if (!string.IsNullOrEmpty(desc))
                    {
                        desc = desc.Replace('-', ' ').Replace('_', ' ');
                        return (desc, issueId, true);
                    }

                    return (issueId, issueId, true);
                }

                return ($"Merge branch '{branchName}'", null, false);
            }
        }

        var patternWithDesc = $"(?<id>{_issuePattern})[\\s:\\-]*(?<desc>.*?)(?:\\n|$)";
        var match = Regex.Match(message, patternWithDesc, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var issueId = match.Groups["id"].Value.ToUpperInvariant();
            var description = match.Groups["desc"].Value.Trim();
            var taskId = string.IsNullOrEmpty(description) ? issueId : description;
            return (taskId, issueId, true);
        }

        var firstLine = message.Split('\n')[0].Trim();
        if (firstLine.Length > 50)
            firstLine = string.Concat(firstLine.AsSpan(0, 47), "...");

        return (firstLine, null, false);
    }

    [GeneratedRegex(@"Merge branch ['\""'](.+?)['\""']")]
    private static partial Regex MatchBranchRegex();

    [GeneratedRegex(@"^(Merge branch |feature/|bugfix/|hotfix/)", RegexOptions.IgnoreCase)]
    private static partial Regex NormalizeBranchRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static class TaskTimeEstimator
{
    private const double TargetWeeklyHours = 85.0;

    public static Dictionary<DateTime, DayWork> EstimateHours(
        List<CommitInfo> commits,
        DateTime startDate,
        DateTime endDate)
    {
        // Look back a week to include previous commits to correctly identify tasks
        var effectiveStartDate = startDate.AddDays(-7);
        var dailyWork = InitializeDailyWork(effectiveStartDate, endDate);

        // Filter out duplicates only - keep merge commits that weren't matched
        var activeCommits = commits
            .Where(c => !c.IsDuplicate)
            .ToList();

        // Group commits by task across ALL dates to understand full task lifecycle
        var taskLifecycles = BuildTaskLifecycles(activeCommits);

        // Estimate hours per task per day (raw estimates, no scaling yet)
        var dailyEstimates = EstimateTasksByDay(activeCommits, taskLifecycles);

        // SMART BACKFILL: Fill empty/light days from next day's heavy work
        // This runs BEFORE any scaling or fudging
        SmartBackfillEmptyDays(dailyEstimates, taskLifecycles);

        // Assign to daily work structure
        AssignToDailyWork(dailyWork, dailyEstimates);

        // Scale to weekly target (gently) - runs AFTER backfill
        ScaleToWeeklyTarget(dailyWork, effectiveStartDate, endDate);

        return dailyWork;
    }

    private static Dictionary<DateTime, DayWork> InitializeDailyWork(DateTime start, DateTime end)
    {
        var dailyWork = new Dictionary<DateTime, DayWork>();

        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            dailyWork[date] = new DayWork();
        }

        return dailyWork;
    }

    private static Dictionary<string, TaskLifecycle> BuildTaskLifecycles(List<CommitInfo> commits)
    {
        var lifecycles = new Dictionary<string, TaskLifecycle>();

        // Group all commits by task ID
        var taskGroups = commits
            .Where(c => c.HasIssue)
            .GroupBy(c => c.TaskId);

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

        // Group commits by date
        var commitsByDate = activeCommits
            .GroupBy(c => c.DateTime.Date)
            .OrderBy(g => g.Key);

        foreach (var dayGroup in commitsByDate)
        {
            var date = dayGroup.Key;
            var dayCommits = dayGroup.OrderBy(c => c.DateTime).ToList();
            var estimates = new List<TaskEstimate>();

            // Process tracked tasks
            var trackedGroups = dayCommits
                .Where(c => c.HasIssue)
                .GroupBy(c => c.TaskId);

            foreach (var taskGroup in trackedGroups)
            {
                var taskId = taskGroup.Key;
                var taskCommits = taskGroup.ToList();
                var firstCommit = taskCommits.First();
                var lifecycle = lifecycles[taskId];

                var hours = EstimateTaskHoursForDay(
                    taskCommits,
                    lifecycle.FirstCommitDate == date);

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
            var untrackedGroups = dayCommits
                .Where(c => !c.HasIssue)
                .GroupBy(c => c.TaskId);

            foreach (var taskGroup in untrackedGroups)
            {
                var taskCommits = taskGroup.ToList();
                var firstCommit = taskCommits.First();

                var hours = EstimateUntrackedTask(taskCommits);

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
        bool isFirstDay)
    {
        var totalLines = dayCommits.Sum(c => c.LinesAdded + c.LinesDeleted);
        var totalFiles = dayCommits.Sum(c => c.FilesChanged);

        if (isFirstDay)
        {
            // First day = main work
            // Don't cap here - let backfilling logic handle multi-day tasks
            var baseHours = totalLines == 0 && totalFiles == 0 ? 0.5 :
                            totalLines <= 10 ? 0.5 :
                            totalLines <= 30 ? 1.0 :
                            totalLines <= 80 ? 1.5 :
                            totalLines <= 150 ? 2.5 :
                            totalLines <= 300 ? 3.5 :
                            totalLines <= 500 ? 4.5 :
                            totalLines <= 800 ? 6.0 :
                            totalLines <= 1500 ? 8.0 :
                                                 10.0; // Higher cap for truly large tasks

            // Small bonus for multiple files (suggests refactoring/complexity)
            if (totalFiles > 5)
                baseHours += 0.5;
            if (totalFiles > 10)
                baseHours += 0.5;

            return Math.Round(baseHours * 2) / 2;
        }
        else
        {
            // Subsequent days = small fixes, code review responses
            var baseHours = 0.5; // Default for any follow-up

            // Add time based on actual changes
            if (totalLines > 50)
                baseHours = 1.0;
            if (totalLines > 150)
                baseHours = 1.5;
            if (totalLines > 300)
                baseHours = 2.0;

            return Math.Round(baseHours * 2) / 2;
        }
    }

    private static double EstimateUntrackedTask(List<CommitInfo> commits)
    {
        var totalLines = commits.Sum(c => c.LinesAdded + c.LinesDeleted);
        var totalFiles = commits.Sum(c => c.FilesChanged);

        var hours = totalLines is 0 && totalFiles is 0 ? 0.5 :
                    totalLines <= 20 ? 1.0 :
                    totalLines <= 80 ? 2.0 :
                    totalLines <= 200 ? 3.0 :
                    totalLines <= 500 ? 4.5 :
                                        6.0;

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

            // Only backfill to adjacent days (max 1 day back, occasionally 2)
            var dayGap = (nextDay - currentDay).Days;
            if (dayGap > 2)
                continue;

            // Calculate current day's tracked hours
            var currentDayHours = dailyEstimates.TryGetValue(currentDay, out var value)
                ? value.Where(t => t.HasIssue).Sum(t => t.Hours)
                : 0;

            // Only backfill if current day is genuinely light (< 4h)
            if (currentDayHours >= 4.0)
                continue;

            // Calculate next day's tracked hours
            if (!dailyEstimates.ContainsKey(nextDay))
                continue;

            var nextDayEstimates = dailyEstimates[nextDay].Where(t => t.HasIssue).ToList();
            var nextDayHours = nextDayEstimates.Sum(t => t.Hours);

            // Only backfill if next day is genuinely heavy (> 7h raw)
            if (nextDayHours <= 7.0)
                continue;

            // Find large tasks on next day that could be backfilled
            var backfillCandidates = nextDayEstimates
                .Where(t => t.Hours >= 2.5)
                .Where(t => {
                    // Only backfill if this is the first time we see this specific task
                    var lifecycle = taskLifecycles[t.TaskId];
                    return lifecycle.FirstCommitDate == nextDay;
                })
                .OrderByDescending(t => t.Hours)
                .ToList();

            if (backfillCandidates.Count is 0)
                continue;

            // How much can we backfill?
            var hoursNeeded = Math.Min(8.0 - currentDayHours, nextDayHours - 7.0);
            hoursNeeded = Math.Max(0, hoursNeeded);

            if (hoursNeeded < 1.5)
                continue;

            // Initialize current day estimates if needed
            if (!dailyEstimates.ContainsKey(currentDay))
                dailyEstimates[currentDay] = [];

            // Backfill from the largest task(s)
            var backfilled = 0.0;
            foreach (var candidate in backfillCandidates)
            {
                if (backfilled >= hoursNeeded)
                    break;

                // Move 40-50% of the task's hours backward
                var lifecycle = taskLifecycles[candidate.TaskId];
                var totalLines = lifecycle.AllCommits.Sum(c => c.LinesAdded + c.LinesDeleted);

                // Determine backfill percentage based on complexity
                var backfillPct = totalLines >= 500 ? 0.50 : // Very large tasks: 50%
                                    totalLines >= 300 ? 0.45 : // Large tasks: 45%
                                                        0.40;  // Medium tasks: 40%

                var hoursToBackfill = candidate.Hours * backfillPct;
                hoursToBackfill = Math.Min(hoursToBackfill, hoursNeeded - backfilled);
                hoursToBackfill = Math.Round(hoursToBackfill * 2) / 2; // Round to 0.5h

                if (hoursToBackfill < 1.0) // Don't backfill tiny amounts
                    continue;

                // Add work to previous day
                dailyEstimates[currentDay].Add(new TaskEstimate(
                    candidate.Title,
                    candidate.TaskId,
                    candidate.IssueId,
                    true,
                    hoursToBackfill,
                    currentDay.AddHours(9), // Assume work started at 9 AM
                    [] // No commits on backfilled day
                ));

                // Reduce hours on next day
                candidate.Hours -= hoursToBackfill;
                candidate.Hours = Math.Max(0.5, candidate.Hours);
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
        // Group days into weeks
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

            if (currentWeekHours == 0)
                continue;

            // Count actual work days (days with commits)
            var daysWithWork = weekDays.Count(d => dailyWork[d].TrackedTasks.Count is not 0);
            if (daysWithWork == 0)
                continue;

            // Adjust target for partial weeks
            var targetForWeek = TargetWeeklyHours;
            if (daysWithWork < 5)
                targetForWeek = TargetWeeklyHours * (daysWithWork / 5.0);

            var scaleFactor = targetForWeek / currentWeekHours;

            // Apply GENTLE scaling - don't go crazy
            // Max 1.5x increase, max 0.7x decrease
            scaleFactor = Math.Max(0.7, Math.Min(scaleFactor, 1.5));

            // Only scale if we're reasonably far from target (more than 15% off)
            if (Math.Abs(scaleFactor - 1.0) < 0.15)
                continue;

            // Scale all tasks for this week
            foreach (var day in weekDays)
            {
                foreach (var task in dailyWork[day].TrackedTasks)
                {
                    task.Hours *= scaleFactor;
                    task.Hours = Math.Round(task.Hours * 2) / 2;
                    task.Hours = Math.Max(0.5, Math.Min(task.Hours, 8.0)); // Cap at 8h per task
                }

                foreach (var task in dailyWork[day].UntrackedTasks)
                {
                    task.Hours *= scaleFactor;
                    task.Hours = Math.Round(task.Hours * 2) / 2;
                    task.Hours = Math.Max(0.5, Math.Min(task.Hours, 8.0));
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