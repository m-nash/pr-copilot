// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PrCopilot.StateMachine;

/// <summary>
/// The agent's verdict for one review thread in an auto-mode batch, reported with the comments_processed event.
/// The server only posts results that survive validation; anything else is handed to the user.
/// </summary>
public class AutoCommentResult
{
    public const string Fixed = "fixed";
    public const string PushedBack = "pushed_back";
    public const string FollowUp = "follow_up";
    public const string Answered = "answered";
    public const string NeedsUser = "needs_user";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Regex CommitShaRegex = new(@"([0-9a-f]{7,40})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Owner and repo feed a gh API path, so they are limited to the characters GitHub allows in names.
    private static readonly Regex IssueUrlRegex = new(
        @"^https?://[^/\s]+/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)/issues/(?<number>\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IssueShorthandRegex = new(
        @"^(?:(?<owner>[\w.-]+)/(?<repo>[\w.-]+))?#(?<number>\d+)$",
        RegexOptions.Compiled);

    /// <summary>The review thread ID (CommentInfo.Id) this result applies to.</summary>
    [JsonPropertyName("comment_id")]
    public string CommentId { get; set; } = "";

    /// <summary>One of fixed, pushed_back, follow_up, answered, or needs_user.</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "";

    [JsonPropertyName("reply_text")]
    public string? ReplyText { get; set; }

    /// <summary>What the agent verified that supports its decision: a test result, build output, or a pinned permalink.</summary>
    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    /// <summary>The commit containing the fix, or the new test that backs a pushback.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("issue_url")]
    public string? IssueUrl { get; set; }

    /// <summary>Why the comment was handed to the user.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The review thread this result was matched to during validation.</summary>
    [JsonIgnore]
    public CommentInfo? Comment { get; set; }

    /// <summary>
    /// Parse the results array from comments_processed event data. Returns an empty list for missing or malformed data,
    /// which leaves every comment in the batch for the user.
    /// </summary>
    public static List<AutoCommentResult> ParseResults(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return [];

            // A malformed entry costs only its own comment, which then goes to the user.
            var parsed = new List<AutoCommentResult>();
            foreach (var entry in results.EnumerateArray())
            {
                try
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.Deserialize<AutoCommentResult>(_jsonOptions) is { } result)
                        parsed.Add(result);
                }
                catch (Exception ex) when (ex is JsonException or FileNotFoundException)
                {
                }
            }
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            // Single-file publishing can surface JSON errors as FileNotFoundException (see SamplingHelper).
            return [];
        }
    }

    /// <summary>
    /// Enforce the evidence rules that don't need GitHub: every decision needs reply text and the evidence behind it,
    /// a fix needs its commit, and a follow-up needs its issue. A result that breaks a rule is handed to the user.
    /// </summary>
    public void EnforceEvidenceRules()
    {
        Decision = Decision?.Trim().ToLowerInvariant() ?? "";
        var problem = Decision switch
        {
            NeedsUser => null,
            Fixed or PushedBack or FollowUp or Answered when string.IsNullOrWhiteSpace(ReplyText) => $"reported {Describe(Decision)} without reply text",
            Fixed or PushedBack or FollowUp or Answered when string.IsNullOrWhiteSpace(Evidence) => $"reported {Describe(Decision)} without evidence",
            Fixed when NormalizeCommit(Commit) is null => "reported a fix without the SHA of the commit that contains it",
            FollowUp when string.IsNullOrWhiteSpace(IssueUrl) => "reported a follow-up without the issue link",
            Fixed or PushedBack or FollowUp or Answered => null,
            _ => $"reported an unknown decision '{Decision}'"
        };

        if (problem != null)
            HandToUser($"auto mode {problem}");
        else if (Decision == NeedsUser && string.IsNullOrWhiteSpace(Reason))
            Reason = "auto mode could not decide";
    }

    private static string Describe(string decision) => decision switch
    {
        Fixed => "a fix",
        PushedBack => "a pushback",
        FollowUp => "a follow-up",
        _ => "an answer"
    };

    /// <summary>Hand this comment to the user instead of posting the agent's reply.</summary>
    public void HandToUser(string reason)
    {
        Decision = NeedsUser;
        Reason = reason;
    }

    /// <summary>Extract a commit SHA from a bare SHA, an owner/repo@sha reference, or a commit URL.</summary>
    public static string? NormalizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
            return null;
        var match = CommitShaRegex.Match(commit.Trim());
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Parse an issue URL, owner/repo#N, or #N (in the PR's repository) into its parts.
    /// </summary>
    public static bool TryParseIssueReference(string? reference, string defaultOwner, string defaultRepo,
        out string owner, out string repo, out int number)
    {
        owner = defaultOwner;
        repo = defaultRepo;
        number = 0;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var trimmed = reference.Trim();
        var match = IssueUrlRegex.Match(trimmed);
        if (!match.Success)
            match = IssueShorthandRegex.Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out number))
            return false;

        if (match.Groups["owner"].Success)
        {
            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
        }
        return true;
    }
}
