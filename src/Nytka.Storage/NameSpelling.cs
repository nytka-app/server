using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// docs/specs/name-spelling.md: replaces a person's old name with the new one in what the model wrote about them
/// (generated titles and summaries, open tasks that were not edited), inside the caller's transaction.
/// </summary>
public static class NameSpelling
{
    private sealed record Row(Guid Id, string Text);

    /// <summary>The whole <paramref name="oldName"/> on word boundaries, ignoring case; null when it does not occur.</summary>
    public static string? Respell(string text, string oldName, string newName)
    {
        var changed = Pattern(oldName).Replace(text, match => Shape(text, match, oldName, newName));
        return changed == text ? null : changed;
    }

    public static async Task RespellAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid personId, string oldName, string newName, CancellationToken ct)
    {
        if (oldName == newName || oldName.Length == 0)
        {
            return;
        }

        var conversations = $"select distinct c.id from segments s {SpeakerLabel.Joins} join conversations c on c.id = s.conversation_id where {SpeakerLabel.PersonId} = @personId";
        var arguments = new { personId };
        // Matching happens here, not in SQL: ilike's case rules for Cyrillic depend on the database's collation.
        var titles = await connection.QueryAsync<Row>(new CommandDefinition(
            $"select id as Id, ai_title as Text from conversations where ai_title is not null and id in ({conversations})",
            arguments, transaction, cancellationToken: ct));
        await WriteAsync(connection, transaction, titles, "conversations", "ai_title", oldName, newName, ct);
        var summaries = await connection.QueryAsync<Row>(new CommandDefinition(
            $"select id as Id, ai_summary as Text from conversations where ai_summary is not null and id in ({conversations})",
            arguments, transaction, cancellationToken: ct));
        await WriteAsync(connection, transaction, summaries, "conversations", "ai_summary", oldName, newName, ct);
        var tasks = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            select id as Id, text as Text from tasks
            where not done and deleted_at is null and not edited and (person_id = @personId or conversation_id in ({conversations}))
            """,
            arguments, transaction, cancellationToken: ct));
        await WriteAsync(connection, transaction, tasks, "tasks", "text", oldName, newName, ct);
    }

    private static async Task WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<Row> rows, string table, string column,
        string oldName, string newName, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (Respell(row.Text, oldName, newName) is { } text)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"update {table} set {column} = @text where id = @id", new { row.Id, text }, transaction, cancellationToken: ct));
            }
        }
    }

    private static Regex Pattern(string oldName) =>
        new("(?<![\\p{L}\\p{N}])" + Regex.Escape(oldName) + "(?![\\p{L}\\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A case-insensitive match that is not spelled like oldName is often a common word (renaming
    // "Will" must leave "will" alone), unless a sentence start capitalised it the way English does.
    private static string Shape(string text, Match match, string oldName, string newName)
    {
        var matched = match.Value;
        if (matched == oldName)
        {
            return newName;
        }
        if (matched == Capitalize(oldName) && IsSentenceStart(text, match.Index))
        {
            return Capitalize(newName);
        }
        return matched;
    }

    private static string Capitalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static bool IsSentenceStart(string text, int index)
    {
        var i = index;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }
        return i == 0 || text[i - 1] is '.' or '!' or '?';
    }
}
