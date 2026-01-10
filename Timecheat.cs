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

        var commits = CollectCommits(repo, startDate, endDate);
        MatchMergeCommits(commits);
        var dailyWork = TaskTimeEstimator.EstimateHours(commits, startDate, endDate);

        return new Timesheet
        {
            StartDate = startDate,
            EndDate = endDate,
            Days = [.. dailyWork.Select(kvp => new TimesheetDay
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
                    .Select(c => new {
                        Commit = c,
                        Distance = LevenshteinDistance(normalizedBranch, NormalizeBranchName(c.TaskId))
                    })
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();

                var maxLength = Math.Max(normalizedBranch.Length, NormalizeBranchName(bestMatch?.Commit.TaskId ?? "").Length);
                var threshold = maxLength / 2;

                if (bestMatch != null && bestMatch.Distance <= threshold)
                {
                    merge.Title = bestMatch.Commit.Title;

                    if (merge.HasIssue)
                        bestMatch.Commit.IsDuplicate = true;
                    else
                        merge.IsDuplicate = true;
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
                var issueMatch = Regex.Match(branchName, _issuePattern, RegexOptions.IgnoreCase);

                if (issueMatch.Success)
                {
                    var issueId = issueMatch.Groups[1].Value.ToUpperInvariant();
                    var desc = branchName.Replace(issueId, "", StringComparison.Ordinal).Trim('-', '_', ' ');
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

        var match = Regex.Match(message, _issuePattern + @"[\s:\-]*(.*?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var issueId = match.Groups[1].Value.ToUpperInvariant();
            var description = match.Groups[2].Value.Trim();
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
    public static Dictionary<DateTime, DayWork> EstimateHours(
        List<CommitInfo> commits,
        DateTime startDate,
        DateTime endDate)
    {
        var dailyWork = InitializeDailyWork(startDate, endDate);
        var activeCommits = commits.Where(c => !c.IsDuplicate).ToList();

        var commitsByDate = activeCommits
            .Where(c => c.DateTime.DayOfWeek != DayOfWeek.Saturday &&
                       c.DateTime.DayOfWeek != DayOfWeek.Sunday)
            .GroupBy(c => c.DateTime.Date)
            .OrderBy(g => g.Key);

        // First pass: create raw estimates
        var rawEstimates = CreateRawEstimates(commitsByDate, dailyWork);

        // Second pass: adjust small task estimates BEFORE scaling
        AdjustSmallTaskEstimates(rawEstimates);

        // Third pass: backfill multi-day tasks BEFORE scaling
        BackfillMultiDayTasks(dailyWork, rawEstimates, activeCommits);

        // Fourth pass: scale and assign to daily work
        ScaleAndAssignTasks(dailyWork, rawEstimates);

        // Final pass: consolidate small final commits
        ConsolidateSmallFinalCommits(dailyWork);

        // Last pass: spread remaining hours to hit target
        SpreadRemainingHoursAfterScaling(dailyWork);

        return dailyWork;
    }

    private static Dictionary<DateTime, DayWork> InitializeDailyWork(DateTime start, DateTime end)
    {
        var dailyWork = new Dictionary<DateTime, DayWork>();

        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
            {
                dailyWork[date] = new DayWork();
            }
        }

        return dailyWork;
    }

    private static Dictionary<DateTime, List<TaskEstimate>> CreateRawEstimates(
        IEnumerable<IGrouping<DateTime, CommitInfo>> commitsByDate,
        Dictionary<DateTime, DayWork> dailyWork)
    {
        var rawEstimates = new Dictionary<DateTime, List<TaskEstimate>>();

        foreach (var dayGroup in commitsByDate)
        {
            var date = dayGroup.Key;
            if (!dailyWork.ContainsKey(date))
                continue;

            var dayCommits = dayGroup.OrderBy(c => c.DateTime).ToList();
            var taskGroups = dayCommits.GroupBy(c => c.TaskId).ToList();
            var taskEstimates = new List<TaskEstimate>();

            foreach (var taskGroup in taskGroups)
            {
                var taskId = taskGroup.Key;
                var firstCommit = taskGroup.First();
                var hasIssue = firstCommit.HasIssue;
                var title = firstCommit.Title;
                var issueId = firstCommit.IssueId;
                var taskCommits = taskGroup.OrderBy(c => c.DateTime).ToList();
                double hours = EstimateHoursFromCommits(taskCommits);

                taskEstimates.Add(new TaskEstimate(
                    title,
                    taskId,
                    issueId,
                    hasIssue,
                    hours,
                    taskCommits.First().DateTime,
                    taskCommits
                ));
            }

            rawEstimates[date] = taskEstimates;
        }

        return rawEstimates;
    }

    private static double EstimateHoursFromCommits(List<CommitInfo> commits)
    {
        double totalHours = 0;

        foreach (var commit in commits)
        {
            var totalLines = commit.LinesAdded + commit.LinesDeleted;
            double commitHours;

            if (totalLines == 0 && commit.FilesChanged == 0)
                commitHours = 0.5;
            else if (totalLines <= 10 && commit.FilesChanged == 1)
                commitHours = 0.5;
            else if (totalLines <= 50 && commit.FilesChanged <= 3)
                commitHours = 1.0;
            else if (totalLines <= 200)
                commitHours = 2.0;
            else if (totalLines <= 500)
                commitHours = 3.0;
            else
                commitHours = 4.0;

            totalHours += commitHours;
        }

        return totalHours;
    }

    private static void AdjustSmallTaskEstimates(Dictionary<DateTime, List<TaskEstimate>> rawEstimates)
    {
        foreach (var date in rawEstimates.Keys.ToList())
        {
            var trackedEstimates = rawEstimates[date].Where(t => t.HasIssue).ToList();
            var currentHours = trackedEstimates.Sum(t => t.Hours);

            // Only adjust if day looks suspiciously light (< 6h raw)
            if (currentHours >= 6.0)
                continue;

            var tasksToAdjust = trackedEstimates
                .Where(t => t.Hours >= 0.5 && t.Hours <= 2.0)
                .ToList();

            if (tasksToAdjust.Count == 0)
                continue;

            // Calculate how much we can/should add
            double targetHours = 7.0;
            double hoursToAdd = Math.Min(targetHours - currentHours, tasksToAdjust.Count * 1.5);

            if (hoursToAdd < 0.5)
                continue;

            // Distribute the extra hours across small tasks
            foreach (var estimate in tasksToAdjust.OrderBy(t => t.Hours))
            {
                if (hoursToAdd <= 0)
                    break;

                var taskCommits = estimate.Commits;

                // Calculate boost based on complexity signals
                double boost = 0;
                var totalLines = taskCommits.Sum(c => c.LinesAdded + c.LinesDeleted);
                var filesChanged = taskCommits.Sum(c => c.FilesChanged);

                // Small line changes often mean complex refactoring or research-heavy work
                if (totalLines <= 20 && filesChanged <= 2)
                {
                    boost = Math.Min(1.5, estimate.Hours * 1.0); // Can double small tasks
                }
                // Medium changes with few files also suggests careful work
                else if (totalLines <= 100 && filesChanged <= 5)
                {
                    boost = Math.Min(1.0, estimate.Hours * 0.75);
                }
                // Any task on a light day gets some boost
                else
                {
                    boost = Math.Min(0.5, estimate.Hours * 0.5);
                }

                // Don't exceed what we need to add
                boost = Math.Min(boost, hoursToAdd);
                boost = Math.Round(boost * 2) / 2; // Round to 0.5

                if (boost >= 0.5)
                {
                    // Update the estimate in place
                    var index = rawEstimates[date].IndexOf(estimate);
                    estimate.Hours += boost;
                    hoursToAdd -= boost;
                }
            }
        }
    }

    private static void BackfillMultiDayTasks(
        Dictionary<DateTime, DayWork> dailyWork,
        Dictionary<DateTime, List<TaskEstimate>> rawEstimates,
        List<CommitInfo> commits)
    {
        var sortedDays = dailyWork.Keys.OrderBy(d => d).ToList();

        for (int i = 0; i < sortedDays.Count; i++)
        {
            var currentDay = sortedDays[i];

            // Calculate current hours from raw estimates
            var currentHours = rawEstimates.TryGetValue(currentDay, out List<TaskEstimate>? value)
                ? value.Where(t => t.HasIssue).Sum(t => t.Hours)
                : 0;

            // Skip if day already has 7.5+ hours of work
            if (currentHours >= 7.5)
                continue;

            var hoursNeeded = 8.0 - currentHours;

            // Look ahead up to 5 working days for tasks that might have started earlier
            var lookAheadDays = sortedDays.Skip(i + 1).Take(5).ToList();

            foreach (var futureDay in lookAheadDays)
            {
                if (!rawEstimates.ContainsKey(futureDay))
                    continue;

                var futureTasks = rawEstimates[futureDay].Where(t => t.HasIssue).ToList();

                foreach (var task in futureTasks)
                {
                    var taskCommits = commits
                        .Where(c => c.TaskId == task.TaskId && c.DateTime.Date == futureDay)
                        .ToList();

                    if (taskCommits.Count == 0)
                        continue;

                    var totalLines = taskCommits.Sum(c => c.LinesAdded + c.LinesDeleted);
                    var daysBetween = (futureDay - currentDay).Days;

                    // Determine if this task likely represents multi-day work
                    bool isMultiDayTask = false;
                    double hoursToBackfill = 0;

                    // Be more aggressive - most tasks can be backfilled
                    if (totalLines > 500 || task.Hours >= 6)
                    {
                        isMultiDayTask = true;
                        hoursToBackfill = Math.Min(7.0, task.Hours * 0.7); // 70% of work
                    }
                    else if (totalLines > 200 || task.Hours >= 4)
                    {
                        isMultiDayTask = true;
                        hoursToBackfill = Math.Min(5.0, task.Hours * 0.65); // 65% of work
                    }
                    else if (totalLines > 50 || task.Hours >= 2.5)
                    {
                        isMultiDayTask = true;
                        hoursToBackfill = Math.Min(4.0, task.Hours * 0.6); // 60% of work
                    }
                    else if (totalLines > 10 || task.Hours >= 1.5)
                    {
                        isMultiDayTask = true;
                        hoursToBackfill = Math.Min(3.0, task.Hours * 0.55); // 55% of work
                    }
                    else if (daysBetween >= 2)
                    {
                        // Even small tasks can be backfilled if there's a gap
                        isMultiDayTask = true;
                        hoursToBackfill = Math.Min(2.0, task.Hours * 0.5); // 50% of work
                    }

                    if (isMultiDayTask && hoursToBackfill >= 0.5)
                    {
                        // Don't backfill more than we need
                        hoursToBackfill = Math.Min(hoursToBackfill, hoursNeeded);

                        // Round to nearest 0.5
                        hoursToBackfill = Math.Round(hoursToBackfill * 2) / 2;

                        // Prefer moving entire task if it would fit and fills most of the need
                        bool moveEntireTask = task.Hours <= hoursNeeded && task.Hours >= hoursNeeded * 0.7;

                        if (moveEntireTask)
                        {
                            // Move the entire task to the earlier day
                            if (!rawEstimates.ContainsKey(currentDay))
                                rawEstimates[currentDay] = [];

                            rawEstimates[currentDay].Add(new TaskEstimate(
                                task.Title,
                                task.TaskId,
                                task.IssueId,
                                true,
                                task.Hours,
                                currentDay.AddHours(9),
                                task.Commits
                            ));

                            // Remove from future day
                            rawEstimates[futureDay].RemoveAll(t => t.TaskId == task.TaskId && t.HasIssue);

                            hoursNeeded -= task.Hours;
                        }
                        else
                        {
                            // Split the task across days
                            if (!rawEstimates.ContainsKey(currentDay))
                                rawEstimates[currentDay] = [];

                            rawEstimates[currentDay].Add(new TaskEstimate(
                                task.Title,
                                task.TaskId,
                                task.IssueId,
                                true,
                                hoursToBackfill,
                                currentDay.AddHours(9),
                                [] // No commits on backfilled day
                            ));

                            // Reduce hours on the future day in raw estimates
                            var futureTaskIndex = rawEstimates[futureDay].FindIndex(t => t.TaskId == task.TaskId && t.HasIssue);
                            if (futureTaskIndex >= 0)
                            {
                                var futureTask = rawEstimates[futureDay][futureTaskIndex];
                                futureTask.Hours = Math.Max(0.5, futureTask.Hours - hoursToBackfill);
                                futureTask.Hours = Math.Round(futureTask.Hours * 2) / 2;
                            }

                            hoursNeeded -= hoursToBackfill;
                        }

                        // If we've filled this day to 7+ hours, move to next day
                        if (hoursNeeded <= 0)
                            break;
                    }
                }

                // If we filled this day to 7+ hours, stop looking ahead
                if (hoursNeeded <= 0)
                    break;
            }
        }
    }

    private static void ScaleAndAssignTasks(
        Dictionary<DateTime, DayWork> dailyWork,
        Dictionary<DateTime, List<TaskEstimate>> rawEstimates)
    {
        foreach (var date in dailyWork.Keys.ToList())
        {
            if (!rawEstimates.ContainsKey(date))
                continue;

            var trackedEstimates = rawEstimates[date].Where(t => t.HasIssue).ToList();
            var untrackedEstimates = rawEstimates[date].Where(t => !t.HasIssue).ToList();

            var totalTracked = trackedEstimates.Sum(t => t.Hours);
            double targetHours = Math.Min(Math.Max(totalTracked, 6.0), 8.0);

            if (totalTracked > 0)
            {
                var scale = targetHours / totalTracked;

                foreach (var estimate in trackedEstimates)
                {
                    var scaledHours = Math.Round(estimate.Hours * scale * 2) / 2;
                    if (scaledHours < 0.5) scaledHours = 0.5;

                    dailyWork[date].TrackedTasks.Add(new TaskWork
                    {
                        Title = estimate.Title,
                        TaskId = estimate.TaskId,
                        IssueId = estimate.IssueId,
                        Hours = scaledHours,
                        StartTime = estimate.StartTime,
                        Commits = estimate.Commits.Count
                    });
                }
            }

            foreach (var estimate in untrackedEstimates)
            {
                var hours = Math.Round(estimate.Hours * 2) / 2;
                if (hours < 0.5) hours = 0.5;

                dailyWork[date].UntrackedTasks.Add(new TaskWork
                {
                    Title = estimate.Title,
                    TaskId = estimate.TaskId,
                    IssueId = null,
                    Hours = hours,
                    StartTime = estimate.StartTime,
                    Commits = estimate.Commits.Count
                });
            }
        }
    }

    private static void ConsolidateSmallFinalCommits(Dictionary<DateTime, DayWork> dailyWork)
    {
        var tasksByDay = new Dictionary<string, List<DateTime>>();

        foreach (var day in dailyWork)
        {
            foreach (var task in day.Value.TrackedTasks)
            {
                if (!tasksByDay.ContainsKey(task.TaskId))
                    tasksByDay[task.TaskId] = [];
                tasksByDay[task.TaskId].Add(day.Key);
            }
        }

        foreach (var taskDays in tasksByDay.Where(t => t.Value.Count > 1))
        {
            var days = taskDays.Value.OrderBy(d => d).ToList();
            var lastDay = days.Last();
            var lastDayTask = dailyWork[lastDay].TrackedTasks.FirstOrDefault(t => t.TaskId == taskDays.Key);

            if (lastDayTask != null && lastDayTask.Hours <= 1.0 && lastDayTask.Commits == 1)
            {
                if (days.Count > 1)
                {
                    var prevDay = days[^2];
                    var prevDayTask = dailyWork[prevDay].TrackedTasks.FirstOrDefault(t => t.TaskId == taskDays.Key);

                    if (prevDayTask != null)
                    {
                        prevDayTask.Hours += lastDayTask.Hours;
                        dailyWork[lastDay].TrackedTasks.Remove(lastDayTask);
                    }
                }
            }
        }
    }

    private static void SpreadRemainingHoursAfterScaling(Dictionary<DateTime, DayWork> dailyWork)
    {
        // Calculate total tracked hours
        var totalHours = dailyWork.Values.Sum(d => d.TrackedTasks.Sum(t => t.Hours));

        // Target is 8 hours per working day
        var targetTotal = dailyWork.Count * 8.0;

        // If we're under target, boost larger tasks proportionally more
        if (totalHours < targetTotal)
        {
            var hoursNeeded = targetTotal - totalHours;

            // Collect all tasks with their current hours
            var allTasks = dailyWork.Values
                .SelectMany(d => d.TrackedTasks)
                .OrderByDescending(t => t.Hours)
                .ToList();

            if (allTasks.Count == 0)
                return;

            // Calculate total "weight" - larger tasks have more weight
            // Use square of hours so larger tasks get disproportionately more boost
            var totalWeight = allTasks.Sum(t => t.Hours * t.Hours);

            // Distribute the needed hours based on task weight
            foreach (var task in allTasks)
            {
                var weight = task.Hours * task.Hours;
                var proportion = weight / totalWeight;
                var boost = hoursNeeded * proportion;

                // Apply boost
                task.Hours += boost;

                // Cap individual tasks at 5 hours - anything more should be split across days
                task.Hours = Math.Min(task.Hours, 5.0);

                task.Hours = Math.Round(task.Hours * 2) / 2; // Round to 0.5
            }

            // After capping, we might still be under target - redistribute the remainder
            var actualTotal = allTasks.Sum(t => t.Hours);
            if (actualTotal < targetTotal)
            {
                var remainingNeeded = targetTotal - actualTotal;
                // Give the remainder to tasks under 4h proportionally
                var tasksUnderCap = allTasks.Where(t => t.Hours < 4.0).ToList();
                if (tasksUnderCap.Count is not 0)
                {
                    var underCapWeight = tasksUnderCap.Sum(t => t.Hours * t.Hours);
                    foreach (var task in tasksUnderCap)
                    {
                        var weight = task.Hours * task.Hours;
                        var proportion = weight / underCapWeight;
                        var extraBoost = remainingNeeded * proportion;
                        task.Hours = Math.Min(task.Hours + extraBoost, 5.0);
                        task.Hours = Math.Round(task.Hours * 2) / 2;
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