using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.SearchEval;

/// <summary>
/// Measures what vector search adds (NYTKA-66): for each question of a local file, the conversations full-text search
/// finds and those hybrid search finds, as recall at 5 and 10. Reads the database directly, so the server must have
/// embedded the archive with the same model. Prints numbers and question numbers only, never text from the archive.
/// </summary>
public static class Program
{
    private const string Usage =
        "Usage: Nytka.SearchEval --file <questions.jsonl> [--max-distance 0.5] [--query-prefix <text>]\n"
        + "Reads ConnectionStrings__Postgres, Nytka__Embeddings__BaseUrl, Nytka__Embeddings__Model and, if set,\n"
        + "Nytka__Embeddings__ApiKey and Nytka__Embeddings__Dimensions from the environment.\n"
        + "Each line of the file: {\"question\": \"...\", \"expected\": [\"<conversation id>\", ...], \"terms\": \"optional keywords\"}.\n"
        + "Without terms the question's own words are the full-text query, any of them matching, as Ask runs a query.";

    private const int Depth = 10;

    private sealed record Line(string Question, Guid[] Expected, string? Terms);

    public static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            options[args[i]] = args[i + 1];
        }

        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        var embeddings = new EmbeddingsOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("Nytka__Embeddings__BaseUrl"),
            Model = Environment.GetEnvironmentVariable("Nytka__Embeddings__Model"),
            ApiKey = Environment.GetEnvironmentVariable("Nytka__Embeddings__ApiKey"),
            Dimensions = int.TryParse(Environment.GetEnvironmentVariable("Nytka__Embeddings__Dimensions"), CultureInfo.InvariantCulture, out var d) ? d : null,
            QueryPrefix = options.GetValueOrDefault("--query-prefix") ?? "",
            MaxDistance = double.Parse(options.GetValueOrDefault("--max-distance") ?? "0.5", CultureInfo.InvariantCulture),
        };
        if (args.Length % 2 != 0 || options.GetValueOrDefault("--file") is not { } file || string.IsNullOrEmpty(connection)
            || !embeddings.IsConfigured)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var lines = File.ReadLines(file).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<Line>(l, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();

        await using (var db = NpgsqlDataSource.Create(connection))
        await using (var session = await db.OpenConnectionAsync())
        {
            var model = await session.QuerySingleOrDefaultAsync<string?>("select model from embedding_model where id = 1");
            if (model != embeddings.Model)
            {
                Console.Error.WriteLine($"The database holds vectors of '{model ?? "no model"}', not '{embeddings.Model}'.");
                return 1;
            }
        }

        using var search = new SearchStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connection })
            .Build());
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new EmbeddingsClient(http, new Fixed(embeddings));

        double[] words = [0, 0];
        double[] hybrid = [0, 0];
        var counted = 0;
        Console.WriteLine("#    words@5 words@10 hybrid@5 hybrid@10");
        for (var n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            var terms = SearchQuery.ExtractTerms(line.Terms ?? line.Question);
            if (line.Expected is not { Length: > 0 } || string.IsNullOrWhiteSpace(line.Question) || terms.Count == 0)
            {
                continue;
            }

            var vector = (await client.EmbedAsync([SemanticSearch.Prefix(embeddings) + line.Question.Trim()], default))[0];
            var plain = await Conversations(search, terms, null);
            var fused = await Conversations(search, terms, new SemanticQuery(vector, embeddings.MaxDistance));

            var row = new[] { Recall(plain, line.Expected, 5), Recall(plain, line.Expected, 10), Recall(fused, line.Expected, 5), Recall(fused, line.Expected, 10) };
            words[0] += row[0];
            words[1] += row[1];
            hybrid[0] += row[2];
            hybrid[1] += row[3];
            counted++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n + 1,-4} {row[0],7:0.00} {row[1],8:0.00} {row[2],8:0.00} {row[3],9:0.00}"));
        }

        if (counted == 0)
        {
            Console.Error.WriteLine("No question with expected ids.");
            return 1;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"\n{counted} questions, max distance {embeddings.MaxDistance}\n"
            + $"full-text  recall@5 {words[0] / counted:0.000}  recall@10 {words[1] / counted:0.000}\n"
            + $"hybrid     recall@5 {hybrid[0] / counted:0.000}  recall@10 {hybrid[1] / counted:0.000}"));
        return 0;
    }

    /// <summary>The first conversations of a search, best first; a memory counts as the conversation it came from.</summary>
    private static async Task<List<Guid>> Conversations(SearchStore search, IReadOnlyList<string> terms, SemanticQuery? semantic)
    {
        var rows = await search.SearchAsync(terms, true, true, 3 * Depth, 0, default, anyTerm: true, semantic: semantic);
        return rows.Select(r => r.Kind == SearchStore.Conversation ? r.Id : r.ConversationId)
            .OfType<Guid>().Distinct().Take(Depth).ToList();
    }

    private static double Recall(List<Guid> found, Guid[] expected, int k) =>
        (double)expected.Distinct().Count(found.Take(k).Contains) / expected.Distinct().Count();

    private sealed class Fixed(EmbeddingsOptions value) : IOptionsMonitor<EmbeddingsOptions>
    {
        public EmbeddingsOptions CurrentValue => value;

        public EmbeddingsOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EmbeddingsOptions, string?> listener) => null;
    }
}
