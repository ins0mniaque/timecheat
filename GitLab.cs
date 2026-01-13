using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using NGitLab;
using NGitLab.Models;

namespace Timecheat;

internal static partial class GitLab
{
    public static List<CommitInfo> CollectCommitsFromMergeRequests(this GitLabClient client, long projectId, string author, DateTime start, DateTime end, GitLabCache cache)
    {
        var result = new List<CommitInfo>();

        var mrClient = client.GetMergeRequest(projectId);
        var commitClient = client.GetCommits(projectId);
        var mergeRequests = mrClient.Get(new()
        {
            UpdatedAfter = start,
            UpdatedBefore = end,
            OrderBy = "updated_at",
            Sort = "asc"
        });

        mergeRequests = mergeRequests.Where(mr => mr.State is "opened" or "merged");

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
            var issueIds = ExtractIssueIds($"{mr.SourceBranch}\n{mr.Title}\n{mr.Description}");
            var commitIds = cache.GetMergeRequestCommitIds(mrClient, mr);

            foreach (var commitId in commitIds)
            {
                var detailedCommit = cache.GetCommit(commitClient, commitId);

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
        DateTime = commit.CommittedDate.ToLocalTime(),
        Message = commit.Message,
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

    public static string DetectIssuePatternFromMergeRequests(this GitLabClient client, long projectId)
    {
        var mrClient = client.GetMergeRequest(projectId);
        var mergeRequests = mrClient.Get(new MergeRequestQuery
        {
            State = MergeRequestState.merged
        });

        var prefix = mergeRequests.Take(50)
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

    [GeneratedRegex(@"([A-Z][A-Z0-9]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IssueIdRegex();
}

internal sealed class GitLabCache
{
    private readonly string _commitsCacheDir;
    private readonly string _mergeRequestsCacheDir;
    private readonly ConcurrentDictionary<string, Commit> _commitsCache = new();
    private readonly ConcurrentDictionary<long, List<string>> _mergeRequestsCache = new();

    public GitLabCache(string appName)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Directory.GetCurrentDirectory();

        _commitsCacheDir = Path.Combine(baseDir, appName, "commits");
        _mergeRequestsCacheDir = Path.Combine(baseDir, appName, "merge-requests");
        Directory.CreateDirectory(_commitsCacheDir);
        Directory.CreateDirectory(_mergeRequestsCacheDir);
    }

    public Commit GetCommit(ICommitClient commitClient, string commitId)
    {
        if (_commitsCache.TryGetValue(commitId, out var cachedCommit))
            return cachedCommit;

        var cacheFile = Path.Combine(_commitsCacheDir, $"{commitId}.json");

        if (File.Exists(cacheFile))
        {
            cachedCommit = JsonSerializer.Deserialize(File.ReadAllText(cacheFile), GitLabContext.Default.Commit)
                           ?? throw new JsonException($"Failed to deserialize commit {commitId}");
        }
        else
        {
            cachedCommit = commitClient.GetCommit(commitId);

            File.WriteAllText(cacheFile, JsonSerializer.Serialize(cachedCommit, GitLabContext.Default.Commit));
        }

        _commitsCache[commitId] = cachedCommit;
        return cachedCommit;
    }

    public IReadOnlyList<string> GetMergeRequestCommitIds(IMergeRequestClient mrClient, MergeRequest mergeRequest)
    {
        if (mergeRequest.State is not "merged")
            return mrClient.Commits(mergeRequest.Iid).All.Select(c => c.ShortId).ToList();

        if (_mergeRequestsCache.TryGetValue(mergeRequest.Iid, out var cachedIds))
            return cachedIds;

        var cacheFile = Path.Combine(_mergeRequestsCacheDir, $"{mergeRequest.Iid}.json");

        if (File.Exists(cacheFile))
        {
            cachedIds = JsonSerializer.Deserialize(File.ReadAllText(cacheFile), GitLabContext.Default.ListString) ??
                        throw new JsonException($"Failed to deserialize MR {mergeRequest.Iid} commit IDs");
        }
        else
        {
            cachedIds = mrClient.Commits(mergeRequest.Iid).All.Select(c => c.ShortId).ToList();

            File.WriteAllText(cacheFile, JsonSerializer.Serialize(cachedIds, GitLabContext.Default.ListString));
        }

        _mergeRequestsCache[mergeRequest.Iid] = cachedIds;
        return cachedIds;
    }

    public void ClearCache()
    {
        _commitsCache.Clear();
        _mergeRequestsCache.Clear();

        if (Directory.Exists(_commitsCacheDir))
            Directory.Delete(_commitsCacheDir, true);

        if (Directory.Exists(_mergeRequestsCacheDir))
            Directory.Delete(_mergeRequestsCacheDir, true);

        Directory.CreateDirectory(_commitsCacheDir);
        Directory.CreateDirectory(_mergeRequestsCacheDir);
    }
}

[JsonSerializable(typeof(Commit))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class GitLabContext : JsonSerializerContext;