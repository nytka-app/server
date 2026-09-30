using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Search;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Ask;

/// <summary>A source the answer may cite. <paramref name="ConversationId"/> is the conversation itself, or a memory's source (null when it has none).</summary>
public sealed record AskSource(int N, string Kind, Guid Id, Guid? ConversationId, string? Title, DateTime At, string Snippet);

public sealed record AskResult(string Answer, IReadOnlyList<AskSource> Sources);

/// <summary>
/// Answers a question from the archive (docs/specs/v0.8.md, Ask): a plan call, retrieval through
/// <see cref="SearchStore"/> (any word of a query may match), an answer call, then the markers are checked. Nothing here is logged or stored: the
/// question, the sources and the answer are all text of the person's life. Throws <see cref="LlmException"/>
/// when a model call fails.
/// </summary>
public sealed partial class AskService(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> options,
    SettingsService settings,
    SearchStore search,
    ConversationStore conversations,
    TaskStore tasks,
    MemoryStore memories,
    TimeProvider time)
{
    public const int MaxQuestionLength = 500;
    public const int MaxConversations = 8;
    public const int MaxMemories = 10;
    public const int SnippetLength = 200;

    /// <summary>Hits asked of each query, of both kinds together; enough to fill both caps after merging.</summary>
    private const int HitsPerQuery = 20;

    /// <summary>What <see cref="Block"/> adds to a head: the marker, the blank line between sources and the transcript label.</summary>
    private const int Overhead = 24;

    public bool IsConfigured => llm.IsConfigured;

    private sealed record Candidate(string Kind, Guid Id, float Score, DateTime At, string? Snippet, long? SegmentId);

    /// <summary><paramref name="Anchor"/> is the index in <paramref name="Lines"/> of the best-matching segment, 0 when none matched.</summary>
    private sealed record Loaded(
        string Kind, Guid Id, Guid? ConversationId, string? Title, DateTime At, string Snippet, string Head,
        IReadOnlyList<string> Lines, int Anchor);

    [GeneratedRegex(@"(?<space>\s*)\[(?<numbers>\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex MarkerPattern();

    public async Task<AskResult> AskAsync(string question, CancellationToken ct)
    {
        var llmOptions = options.CurrentValue;
        var zone = UserTimeZone.Resolve(settings);
        var zoneName = UserTimeZone.Name(zone);
        var today = ConversationPrompt.LocalDate(time.GetUtcNow(), zone);

        var plan = LlmJson.Parse<AskPlan>(await llm.CompleteJsonAsync(
            new LlmRequest(AskPrompt.PlanSchemaName, AskPrompt.PlanSchema, AskPrompt.PlanSystem(zoneName), AskPrompt.PlanUser(today, question)), ct));

        var candidates = await RetrieveAsync(plan, zone, ct);
        var header = AskPrompt.AnswerUser(today, question, "");
        var sources = await LoadAsync(candidates, Math.Max(0, llmOptions.MaxInputChars - header.Length), zone, ct);

        var user = AskPrompt.AnswerUser(today, question, string.Join("\n\n", sources.Select((s, i) => Block(i + 1, s))));
        var answer = LlmJson.Parse<AskModelAnswer>(await llm.CompleteJsonAsync(
            new LlmRequest(
                AskPrompt.AnswerSchemaName, AskPrompt.AnswerSchema, AskPrompt.AnswerSystem(llmOptions.OutputLanguage, zoneName), user), ct));
        return Finish(answer.Answer, sources);
    }

    /// <summary>
    /// Drops markers that name no source and numbers the sources that are cited from 1, in order of first mention.
    /// The markers in the text decide: <c>cited</c> only asks the model to think about them.
    /// </summary>
    private static AskResult Finish(string text, IReadOnlyList<Loaded> sources)
    {
        var order = new List<int>();
        var cleaned = MarkerPattern().Replace(text, match =>
        {
            var marks = new StringBuilder();
            foreach (var part in match.Groups["numbers"].Value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > sources.Count)
                {
                    continue;
                }

                var index = order.IndexOf(n);
                if (index < 0)
                {
                    order.Add(n);
                    index = order.Count - 1;
                }

                marks.Append('[').Append(index + 1).Append(']');
            }

            // A marker with no valid number goes away with the space before it.
            return marks.Length == 0 ? "" : match.Groups["space"].Value + marks;
        }).Trim();

        var cited = order.Select((n, i) =>
        {
            var s = sources[n - 1];
            return new AskSource(i + 1, s.Kind, s.Id, s.ConversationId, s.Title, s.At, s.Snippet);
        }).ToList();
        return new AskResult(cleaned, cited);
    }

    private async Task<IReadOnlyList<Candidate>> RetrieveAsync(AskPlan plan, TimeZoneInfo zone, CancellationToken ct)
    {
        var queries = plan.Queries.Select(SearchQuery.ExtractTerms).Where(t => t.Count > 0).Take(AskPrompt.MaxQueries).ToList();
        var (from, to) = Range(plan, zone);

        if (queries.Count == 0)
        {
            if (from is null && to is null)
            {
                return [];
            }

            var recent = await conversations.ListAsync(to, from, MaxConversations, ct);
            return recent.Select(c => new Candidate(SearchStore.Conversation, c.Id, 0, c.StartedAt, null, null)).ToList();
        }

        var merged = new Dictionary<Guid, Candidate>();
        foreach (var terms in queries)
        {
            foreach (var row in await search.SearchAsync(terms, true, true, HitsPerQuery, 0, ct, from, to, anyTerm: true))
            {
                if (!merged.TryGetValue(row.Id, out var known) || known.Score < row.Score)
                {
                    merged[row.Id] = new Candidate(row.Kind, row.Id, row.Score, row.At, row.Snippet, row.SegmentId);
                }
            }
        }

        var ranked = merged.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.At).ThenBy(c => c.Id).ToList();
        return
        [
            .. ranked.Where(c => c.Kind == SearchStore.Conversation).Take(MaxConversations)
                .Concat(ranked.Where(c => c.Kind == SearchStore.Memory).Take(MaxMemories))
                .OrderByDescending(c => c.Score).ThenByDescending(c => c.At).ThenBy(c => c.Id),
        ];
    }

    /// <summary>The plan's local dates as an instant range, <c>to</c> exclusive (the start of the day after). Unreadable dates or an inverted range mean no range.</summary>
    private static (DateTimeOffset? From, DateTimeOffset? To) Range(AskPlan plan, TimeZoneInfo zone)
    {
        var from = Day(plan.From);
        var to = Day(plan.To);
        if (from is { } f && to is { } t && t < f)
        {
            return (null, null);
        }

        return (from is { } a ? StartOfDay(a, zone) : null, to is { } b ? StartOfDay(b.AddDays(1), zone) : null);
    }

    /// <summary>A plan date; years outside 1970 to 9998 count as none, so the day arithmetic after it cannot overflow.</summary>
    private static DateOnly? Day(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        && day.Year is >= 1970 and <= 9998 ? day : null;

    private static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1); // a day that starts inside a clock jump
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime(); // Npgsql writes only offset 0
    }

    /// <summary>
    /// Reads the candidates that fit <paramref name="budget"/> characters, in order, as the text the model sees: the
    /// heads (kind, date, title, summary, tasks or memory) first, then the transcripts split what is left, the short
    /// ones giving back what they do not need. A transcript is the window around its best-matching segment.
    /// </summary>
    private async Task<IReadOnlyList<Loaded>> LoadAsync(IReadOnlyList<Candidate> candidates, int budget, TimeZoneInfo zone, CancellationToken ct)
    {
        var loaded = new List<Loaded>();
        var used = 0;
        foreach (var candidate in candidates)
        {
            var item = candidate.Kind == SearchStore.Memory
                ? await LoadMemoryAsync(candidate, zone, ct)
                : await LoadConversationAsync(candidate, zone, ct);
            if (item is null)
            {
                continue;
            }

            var cost = item.Head.Length + Overhead;
            if (used + cost > budget)
            {
                break;
            }

            used += cost;
            loaded.Add(item);
        }

        var left = budget - used;
        var pending = Enumerable.Range(0, loaded.Count).Where(i => loaded[i].Lines.Count > 0)
            .OrderBy(i => loaded[i].Lines.Sum(l => l.Length + 1)).ToList();
        for (var k = 0; k < pending.Count; k++)
        {
            var i = pending[k];
            var share = left / (pending.Count - k);
            var text = Window(loaded[i].Lines, loaded[i].Anchor, share);
            left -= text.Length;
            loaded[i] = loaded[i] with { Lines = text.Length == 0 ? [] : [text] };
        }

        return loaded;
    }

    /// <summary>
    /// The lines around <paramref name="anchor"/> that fit <paramref name="max"/> characters, the anchor first, then
    /// the line after and the line before in turn. An anchor longer than the budget is cut.
    /// </summary>
    private static string Window(IReadOnlyList<string> lines, int anchor, int max)
    {
        if (max <= 0 || lines.Count == 0)
        {
            return "";
        }

        anchor = Math.Clamp(anchor, 0, lines.Count - 1);
        if (lines[anchor].Length > max)
        {
            return ConversationPrompt.Cut(lines[anchor], max);
        }

        var low = anchor;
        var high = anchor;
        var size = lines[anchor].Length;
        var grew = true;
        while (grew)
        {
            grew = false;
            if (high + 1 < lines.Count && size + 1 + lines[high + 1].Length <= max)
            {
                size += 1 + lines[++high].Length;
                grew = true;
            }

            if (low > 0 && size + 1 + lines[low - 1].Length <= max)
            {
                size += 1 + lines[--low].Length;
                grew = true;
            }
        }

        return string.Join('\n', lines.Skip(low).Take(high - low + 1));
    }

    private async Task<Loaded?> LoadConversationAsync(Candidate candidate, TimeZoneInfo zone, CancellationToken ct)
    {
        if (await conversations.GetAsync(candidate.Id, ct) is not { } conversation)
        {
            return null;
        }

        var segments = await conversations.SegmentsAsync(candidate.Id, ct);
        var lines = new List<string>();
        var anchor = 0;
        foreach (var segment in segments)
        {
            // One segment renders to one line, or to none when it has no text.
            var line = TranscriptText.Render([new TranscriptSegment(new DateTimeOffset(segment.StartedAt), segment.Label(), segment.Text)], zone);
            if (line.Count == 0)
            {
                continue;
            }

            if (segment.Id == candidate.SegmentId)
            {
                anchor = lines.Count;
            }

            lines.Add(line[0]);
        }

        var date = ConversationPrompt.LocalDate(new DateTimeOffset(conversation.StartedAt), zone);
        var head = new StringBuilder("Conversation, ").Append(date);
        if (!string.IsNullOrWhiteSpace(conversation.Title))
        {
            head.Append(": ").Append(conversation.Title);
        }

        if (!string.IsNullOrWhiteSpace(conversation.Summary))
        {
            head.Append("\nSummary: ").Append(conversation.Summary);
        }

        var taskTexts = (await tasks.ForConversationAsync(candidate.Id, ct)).Select(t => t.Text).ToList();
        if (taskTexts.Count > 0)
        {
            head.Append("\nTasks: ").Append(string.Join("; ", taskTexts));
        }

        var snippet = Plain(candidate.Snippet) is { Length: > 0 } hit
            ? hit
            : conversation.Summary ?? (lines.Count > 0 ? lines[0] : "");
        return new Loaded(
            SearchStore.Conversation, conversation.Id, conversation.Id, conversation.Title, conversation.StartedAt,
            ConversationPrompt.Cut(snippet, SnippetLength), head.ToString(), lines, anchor);
    }

    private async Task<Loaded?> LoadMemoryAsync(Candidate candidate, TimeZoneInfo zone, CancellationToken ct)
    {
        if (await memories.GetAsync(candidate.Id, ct) is not { } memory)
        {
            return null;
        }

        var date = ConversationPrompt.LocalDate(new DateTimeOffset(memory.UpdatedAt), zone);
        return new Loaded(
            SearchStore.Memory, memory.Id, memory.ConversationId, null, memory.UpdatedAt,
            ConversationPrompt.Cut(memory.Text, SnippetLength), $"Memory, {date}: {memory.Text}", [], 0);
    }

    private static string Block(int n, Loaded source) =>
        source.Lines.Count == 0 ? $"[{n}] {source.Head}" : $"[{n}] {source.Head}\nTranscript:\n{source.Lines[0]}";

    /// <summary>The snippet without the private-use match markers (the answer call reads text, not markup).</summary>
    private static string Plain(string? snippet) =>
        (snippet ?? "").Replace(SearchStore.MarkStart.ToString(), "", StringComparison.Ordinal)
            .Replace(SearchStore.MarkEnd.ToString(), "", StringComparison.Ordinal).Trim();
}
