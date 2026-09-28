using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OmiPlatform.Storage;

namespace OmiPlatform.Mcp;

/// <summary>
/// The tool surface over the warehouse.
/// </summary>
/// <remarks>
/// Reads the database Omi's own data was ingested into, never Omi's API — proxying the API would
/// only reproduce the app in text form, and the value here is a warehouse that can eventually be
/// joined against everything else in the homelab (Oura sleep, calendar). This process holds no Omi
/// credential of its own.
/// </remarks>
[McpServerToolType]
public static class OmiTools
{
    internal const string Instructions =
        """
        Omi necklace warehouse. Answers questions about conversations, memories and action items
        captured by an Omi AI wearable, by querying a Postgres copy of the ingest, not Omi's API.

        Start with `coverage`: it reports the date span and row counts actually held. A day with
        nothing in it means the necklace recorded nothing that day, not that ingest failed.

        Conversation categories come from a fixed set Omi assigns (personal, work, health, ...);
        `list_conversations` and `search_conversations` both take one as an optional filter.
        """;

    [McpServerTool(Name = "coverage", Title = "What the warehouse holds", ReadOnly = true, Idempotent = true)]
    [Description("The date span and row counts actually in the warehouse. Call this first: it is "
        + "what separates 'nothing recorded' from 'ingest has not run yet'.")]
    public static async Task<WarehouseCoverage> Coverage(WarehouseRepository warehouse, CancellationToken cancellationToken) =>
        await warehouse.CoverageAsync(cancellationToken);

    [McpServerTool(Name = "list_conversations", Title = "List conversations", ReadOnly = true, Idempotent = true)]
    [Description("Conversation summaries (title, overview, category, action item count) over an "
        + "optional date range and category, newest first. No transcript — use `conversation_detail` "
        + "for one conversation's full text.")]
    public static async Task<IReadOnlyList<ConversationSummary>> ListConversations(
        WarehouseRepository warehouse,
        [Description("First day, yyyy-MM-dd. Omitted = no lower bound.")] string? from = null,
        [Description("Last day, inclusive, yyyy-MM-dd. Omitted = no upper bound.")] string? to = null,
        [Description("Filter to one category, e.g. work, health, personal.")] string? category = null,
        [Description("Max rows. Defaults to 25, capped at 100.")] int? limit = null,
        [Description("Rows to skip, for paging.")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        var fromDay = ParseOptionalDay(from, nameof(from));
        var toDay = ParseOptionalDay(to, nameof(to));
        ValidateRange(fromDay, toDay);

        return await warehouse.ListConversationsAsync(
            fromDay, toDay, category, Clamp(limit, 25, 100), Math.Max(offset ?? 0, 0), cancellationToken);
    }

    [McpServerTool(Name = "conversation_detail", Title = "One conversation, in full", ReadOnly = true, Idempotent = true)]
    [Description("One conversation's full transcript segments, events and action items, by id from "
        + "`list_conversations` or `search_conversations`.")]
    public static async Task<ConversationDetail> ConversationDetail(
        WarehouseRepository warehouse,
        [Description("Conversation id.")] string id,
        CancellationToken cancellationToken)
    {
        return await warehouse.ConversationDetailAsync(id, cancellationToken)
            ?? throw new McpException($"No conversation with id '{id}'. Check `list_conversations` for valid ids.");
    }

    [McpServerTool(Name = "search_conversations", Title = "Search conversation text", ReadOnly = true, Idempotent = true)]
    [Description("Full-text search over conversation titles, overviews and transcripts. Supports "
        + "quoted phrases and -exclusions, same as Postgres websearch syntax.")]
    public static async Task<IReadOnlyList<ConversationSummary>> SearchConversations(
        WarehouseRepository warehouse,
        [Description("Search text, e.g. \"renew passport\" or budget -groceries.")] string query,
        [Description("Max rows. Defaults to 10, capped at 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("`query` must not be empty.");
        }

        return await warehouse.SearchConversationsAsync(query, Clamp(limit, 10, 50), cancellationToken);
    }

    [McpServerTool(Name = "list_memories", Title = "List memories", ReadOnly = true, Idempotent = true)]
    [Description("Memory facts Omi extracted, newest first, optionally filtered to one category "
        + "(interesting, system, manual, core, habits, ...).")]
    public static async Task<IReadOnlyList<MemorySummary>> ListMemories(
        WarehouseRepository warehouse,
        [Description("Filter to one category.")] string? category = null,
        [Description("Max rows. Defaults to 25, capped at 100.")] int? limit = null,
        [Description("Rows to skip, for paging.")] int? offset = null,
        CancellationToken cancellationToken = default) =>
        await warehouse.ListMemoriesAsync(category, Clamp(limit, 25, 100), Math.Max(offset ?? 0, 0), cancellationToken);

    [McpServerTool(Name = "list_action_items", Title = "List action items", ReadOnly = true, Idempotent = true)]
    [Description("Action items extracted from conversations, each with its owning conversation id "
        + "and day, soonest due first. Filter to open ones with completed=false.")]
    public static async Task<IReadOnlyList<ActionItemDetail>> ListActionItems(
        WarehouseRepository warehouse,
        [Description("First conversation day, yyyy-MM-dd.")] string? from = null,
        [Description("Last conversation day, inclusive, yyyy-MM-dd.")] string? to = null,
        [Description("true = only completed, false = only open, omitted = both.")] bool? completed = null,
        [Description("Max rows. Defaults to 50, capped at 200.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var fromDay = ParseOptionalDay(from, nameof(from));
        var toDay = ParseOptionalDay(to, nameof(to));
        ValidateRange(fromDay, toDay);

        return await warehouse.ListActionItemsAsync(fromDay, toDay, completed, Clamp(limit, 50, 200), cancellationToken);
    }

    [McpServerTool(Name = "daily_activity", Title = "Conversations and memories per day", ReadOnly = true, Idempotent = true)]
    [Description("Conversation and memory counts for each day in a range. Days with nothing "
        + "recorded are included as zero, not omitted.")]
    public static async Task<IReadOnlyList<DailyActivity>> DailyActivity(
        WarehouseRepository warehouse,
        [Description("First day, yyyy-MM-dd.")] string from,
        [Description("Last day, inclusive, yyyy-MM-dd.")] string to,
        CancellationToken cancellationToken)
    {
        var start = ParseDay(from, nameof(from));
        var end = ParseDay(to, nameof(to));
        ValidateRange(start, end);

        return await warehouse.DailyActivityAsync(start, end, cancellationToken);
    }

    private static void ValidateRange(DateOnly? from, DateOnly? to)
    {
        if (from is { } start && to is { } end && end < start)
        {
            throw new McpException($"`to` ({end:yyyy-MM-dd}) is before `from` ({start:yyyy-MM-dd}).");
        }
    }

    private static int Clamp(int? value, int @default, int max) => Math.Clamp(value ?? @default, 1, max);

    private static DateOnly? ParseOptionalDay(string? value, string parameterName) =>
        value is null ? null : ParseDay(value, parameterName);

    /// <remarks>Tolerant on the way in because the caller is a language model, which will
    /// eventually send a whole timestamp where a date was asked for.</remarks>
    private static DateOnly ParseDay(string value, string parameterName)
    {
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return DateOnly.FromDateTime(timestamp.Date);
        }

        throw new McpException($"`{parameterName}`: '{value}' is not a date. Use yyyy-MM-dd.");
    }
}
