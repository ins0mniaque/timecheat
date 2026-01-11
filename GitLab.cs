using System.Text.RegularExpressions;

using NGitLab;
using NGitLab.Models;

namespace Timecheat;

internal static partial class GitLab
{
    public static List<CommitInfo> CollectCommitsFromMergeRequests(this GitLabClient client, long projectId, string author, DateTime start, DateTime end)
    {
        var result = new List<CommitInfo>();

        var mrClient = client.GetMergeRequest(projectId);
        var commitClient = client.GetCommits(projectId);

        var mergeRequests = mrClient.Get(new MergeRequestQuery
        {
            UpdatedAfter = start,
            UpdatedBefore = end,
            OrderBy = "updated_at",
            Sort = "asc"
        }).Where(mr => mr.State is "opened" or "merged");

        if (!string.IsNullOrEmpty(author))
        {
            mergeRequests = mergeRequests.Where(mr =>
                string.Equals(mr.Author?.Name, author, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mr.Author?.Username, author, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mr.Assignee?.Name, author, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mr.Assignee?.Username, author, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var mr in mergeRequests)
        {
            var issueIds = ExtractIssueIds(
                $"{mr.SourceBranch}\n{mr.Title}\n{mr.Description}");

            var mrCommits = mrClient.Commits(mr.Iid);

            foreach (var commit in mrCommits.All)
            {
                var detailedCommit = commitClient.GetCommit(commit.ShortId);

                if (issueIds.Count is 0)
                    result.Add(CreateCommitInfo(detailedCommit, null));
                else
                    result.AddRange(issueIds.Select(issueId => CreateCommitInfo(detailedCommit, issueId)));
            }
        }

        return [.. result.OrderBy(c => c.DateTime)];
    }

    private static CommitInfo CreateCommitInfo(Commit commit, string? issueId) => new()
    {
        Sha = commit.Id.ToString(),
        Title = commit.Title,
        TaskId = issueId ?? commit.Title,
        IssueId = issueId,
        HasIssue = issueId is not null,
        IsMergeCommit = true,
        DateTime = commit.CommittedDate.ToLocalTime(),
        Message = commit.Title,
        OriginalMessage = commit.Message,
        FilesChanged = commit.Stats?.Total ?? 0,
        LinesAdded = commit.Stats?.Additions ?? 0,
        LinesDeleted = commit.Stats?.Deletions ?? 0
    };

    private static List<string> ExtractIssueIds(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return [.. IssueIdRegex()
            .Matches(text)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct()];
    }

    [GeneratedRegex(@"([A-Z][A-Z0-9]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IssueIdRegex();

    public static string DetectIssuePatternFromMergeRequests(this GitLabClient client, long projectId)
    {
        var mrClient = client.GetMergeRequest(projectId);

        var mrs = mrClient.Get(new MergeRequestQuery
        {
            State = MergeRequestState.merged
        });

        var prefix = mrs.Take(50)
            .SelectMany(mr =>
                IssueIdRegex().Matches(
                    $"{mr.SourceBranch} {mr.Title} {mr.Description}")
                .Select(m => m.Groups[1].Value.ToUpperInvariant()))
            .Select(id => id.Split('-')[0])
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return prefix != null
            ? $@"{prefix.Key}"
            : @"[A-Z]+";
    }
}