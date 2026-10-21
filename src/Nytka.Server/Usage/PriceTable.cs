using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Usage;

/// <summary>
/// What one engine charges, in the units its price list uses. Audio is billed by the token, <see cref="AudioTokensPerHour"/>
/// of them per hour of speech; every request also pays for <see cref="RequestTokens"/> of text in (the context a service
/// sends with each request); the answer pays per text token out, one per <see cref="TextCharsPerToken"/> characters.
/// </summary>
public sealed record EnginePrice(
    decimal AudioTokensPerHour, decimal AudioUsdPerMillionTokens, decimal RequestTokens, decimal TextInUsdPerMillionTokens,
    decimal TextCharsPerToken, decimal TextOutUsdPerMillionTokens)
{
    public UsageRate Rate => new(
        RequestTokens * TextInUsdPerMillionTokens / 1_000_000m,
        AudioTokensPerHour / 3_600_000m * AudioUsdPerMillionTokens / 1_000_000m,
        TextCharsPerToken > 0 ? TextOutUsdPerMillionTokens / 1_000_000m / TextCharsPerToken : 0);
}

/// <summary>
/// <c>stt.prices</c>: a JSON object from engine (the <c>model</c> Nytka sends, any case) to its <see cref="EnginePrice"/>
/// fields, each optional and 0 when left out. An engine the table does not name costs nothing, as a local one does.
/// </summary>
public sealed class PriceTable(IReadOnlyDictionary<string, EnginePrice> engines)
{
    public const string Key = "stt.prices";

    /// <summary>
    /// Soniox async: audio $1.50 per million tokens at about 30,000 tokens an hour, text in and out $3.50 per million. The
    /// 390 tokens a request and 3 characters a token are fitted to a real bill: 4,717 batches, 16 hours of mostly Ukrainian
    /// speech and 811,000 characters of text cost $8.10 from 27 September to 20 October 2026, against $8.11 by this table.
    /// stt's context is 611 characters, under 390 tokens: the request term also carries a per-request charge the bill hides.
    /// </summary>
    public const string Default =
        """{"soniox":{"audioTokensPerHour":30000,"audioUsdPerMillionTokens":1.5,"requestTokens":390,"textInUsdPerMillionTokens":3.5,"textCharsPerToken":3,"textOutUsdPerMillionTokens":3.5}}""";

    private const int MaxEngines = 32;

    private static readonly IReadOnlyList<string> Fields =
        ["audioTokensPerHour", "audioUsdPerMillionTokens", "requestTokens", "textInUsdPerMillionTokens", "textCharsPerToken", "textOutUsdPerMillionTokens"];

    public static PriceTable Empty { get; } = new(new Dictionary<string, EnginePrice>());

    public UsageRate RateOf(string engine) => engines.TryGetValue(engine, out var price) ? price.Rate : default;

    public static string? Validate(string json) => TryParse(json, out _);

    /// <summary>The table, or <see cref="Empty"/> when <paramref name="json"/> is not one (the validator keeps that from being saved).</summary>
    public static PriceTable Parse(string? json) => json is not null && TryParse(json, out var table) is null ? table : Empty;

    private static string? TryParse(string json, out PriceTable table)
    {
        table = Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "Must be a JSON object of engines.";
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "Must be a JSON object of engines.";
            }

            var engines = new Dictionary<string, EnginePrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var engine in document.RootElement.EnumerateObject())
            {
                if (engine.Name.Trim().Length == 0 || engine.Value.ValueKind != JsonValueKind.Object)
                {
                    return "Each engine must be a name and an object of prices.";
                }

                var values = new Dictionary<string, decimal>();
                foreach (var field in engine.Value.EnumerateObject())
                {
                    if (!Fields.Contains(field.Name))
                    {
                        return $"Prices take only {string.Join(", ", Fields)}.";
                    }

                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDecimal(out var value) || value < 0 || value > 1_000_000)
                    {
                        return "Every price must be a number from 0 to 1000000.";
                    }

                    values[field.Name] = value;
                }

                if (!engines.TryAdd(engine.Name.Trim(), Price(values)))
                {
                    return "An engine is named twice.";
                }
            }

            if (engines.Count > MaxEngines)
            {
                return $"At most {MaxEngines} engines.";
            }

            table = new PriceTable(engines);
            return null;
        }
    }

    private static EnginePrice Price(Dictionary<string, decimal> values) => new(
        values.GetValueOrDefault("audioTokensPerHour"), values.GetValueOrDefault("audioUsdPerMillionTokens"),
        values.GetValueOrDefault("requestTokens"), values.GetValueOrDefault("textInUsdPerMillionTokens"),
        values.GetValueOrDefault("textCharsPerToken"), values.GetValueOrDefault("textOutUsdPerMillionTokens"));
}
