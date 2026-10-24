using Nytka.Server.Search;

namespace Nytka.Server.Tests;

/// <summary>
/// An embeddings model in memory. A text's vector has a 1 for each topic one of whose word stems it contains (in
/// English, Ukrainian or Russian), plus a small constant, so texts on the same topic are close whatever their words, and
/// texts on no shared topic are far. Records every input; <see cref="Fail"/> makes the next calls throw.
/// </summary>
public sealed class FakeEmbeddings : IEmbeddingsClient
{
    public static readonly string[][] Topics =
    [
        ["budget", "money", "salary", "price", "бюджет", "гроші", "зарплат", "деньг"],
        ["trip", "flight", "vacation", "travel", "подорож", "відпустк", "поїздк", "отпуск"],
        ["doctor", "dentist", "sick", "hospital", "лікар", "хвор", "врач"],
        ["lunch", "dinner", "pizza", "food", "обід", "вечер", "їж"],
        ["zebrafish", "aquarium", "fish tank", "акваріум", "рибк"],
    ];

    private readonly List<string> _inputs = [];

    /// <summary>The vector size; anything at least <see cref="Topics"/> plus one.</summary>
    public int Dimensions { get; set; } = 8;

    public bool Fail { get; set; }

    public IReadOnlyList<string> Inputs
    {
        get
        {
            lock (_inputs)
            {
                return [.. _inputs];
            }
        }
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (Fail)
        {
            throw new EmbeddingsException("The embeddings endpoint answered 503.", 503);
        }

        lock (_inputs)
        {
            _inputs.AddRange(inputs);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Vector).ToList());
    }

    public float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        for (var i = 0; i < Topics.Length; i++)
        {
            if (Topics[i].Any(stem => text.Contains(stem, StringComparison.OrdinalIgnoreCase)))
            {
                vector[i] = 1;
            }
        }

        vector[^1] = 0.05f;
        return vector;
    }
}
