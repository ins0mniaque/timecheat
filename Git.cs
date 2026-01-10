using System.Text.RegularExpressions;

using LibGit2Sharp;

namespace Timecheat;

internal static partial class Git
{
    [GeneratedRegex(@"\b(?<key>[A-Z][A-Z0-9]+)-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex BranchPrefixRegex();

    public static string? DetectBranchPrefix(this Repository repo, int minOccurrences = 2)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var branch in repo.Branches)
        {
            var name = branch.FriendlyName;
            var match = BranchPrefixRegex().Match(branch.FriendlyName);
            if (!match.Success)
                continue;

            var key = match.Groups["key"].Value;

            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        return counts.Where(kvp => kvp.Value >= minOccurrences)
                     .OrderByDescending(kvp => kvp.Value)
                     .Select(kvp => kvp.Key)
                     .FirstOrDefault();
    }
}