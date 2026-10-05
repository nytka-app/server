using System.Text.RegularExpressions;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// The checks a suggested name passes after the model answered (docs/specs/people.md, Layer 1: Validation); the model is not
/// trusted. <see cref="Version"/> goes up when the rules change, and <c>POST /people/backfill?force=true</c> asks again the
/// runs made under an older one.
/// </summary>
public static partial class NameValidator
{
    public const int Version = 1;
    public const int MaxTokens = 3;
    public const int MaxLength = 40;

    /// <summary>How many segments either side of the model's segment are searched for the one that says the name.</summary>
    public const int EvidenceReach = 3;

    /// <summary>
    /// Words that are never a name in Ukrainian, Russian or English, lower case: pronouns, particles, answers, interjections,
    /// evaluations and generic address terms. A real name that is also one of them passes only when the evidence line writes
    /// it with a capital in the middle of a sentence.
    /// </summary>
    private static readonly HashSet<string> Stoplist = new(StringComparer.Ordinal)
    {
        // pronouns
        "ти", "ви", "я", "ми", "він", "вона", "воно", "вони", "мені", "тобі", "нам", "вам", "його", "її", "нас", "вас",
        "ты", "вы", "мы", "он", "она", "оно", "они", "мне", "тебе", "его", "её", "ее",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
        // particles, conjunctions, prepositions, question words
        "а", "і", "й", "та", "але", "не", "ні", "ну", "по", "на", "за", "до", "як", "що", "чого", "чому", "де", "коли", "хто",
        "це", "цей", "ця", "ось", "тут", "там", "тільки", "єдине", "єдиний", "просто", "може", "мабуть", "ходу", "будь", "ласка",
        "и", "но", "ни", "что", "как", "почему", "где", "когда", "кто", "это", "этот", "эта", "вот", "только", "единственное",
        "единственный", "может", "наверное", "ходу",
        "the", "an", "and", "or", "but", "not", "what", "who", "why", "how", "when", "where", "this", "that", "here", "there",
        "just", "only", "one",
        // answers and interjections
        "так", "ага", "угу", "ок", "окей", "да", "нет", "нэ", "нє", "ге", "эм", "ээ", "мм", "хм", "ох", "ах", "ой", "ого",
        "yes", "no", "yeah", "yep", "nope", "ok", "okay", "hmm", "um", "uh", "oh", "ah", "wow", "well", "so", "right", "sure",
        // greetings and thanks
        "привіт", "дякую", "вибач", "вибачте", "привет", "спасибо", "извини", "извините", "пожалуйста",
        "hi", "hey", "hello", "bye", "thanks", "thank", "please", "sorry",
        // evaluations
        "добре", "класно", "круто", "прикольно", "нормально", "гарно", "чудово", "погано", "хорошо", "класс", "ладно", "плохо",
        "good", "great", "fine", "nice", "cool", "bad",
        // commands the voice may be saying
        "розкажи", "скажи", "дивись", "слухай", "расскажи", "скажи", "смотри", "слушай",
        // generic address terms and terms of endearment
        "малий", "мала", "малюк", "малята", "дівчинка", "хлопчик", "дівчата", "хлопці", "друже", "брате", "сестро", "любий",
        "дорогий", "дорога", "сонце", "зайчик", "котику", "діти", "люди", "чоловіче", "пане", "пані",
        "малыш", "малышка", "девочка", "мальчик", "девчонки", "ребята", "друг", "брат", "сестра", "дорогой", "дорогая",
        "солнце", "зайка", "котик", "дети", "люди", "мужик", "чувак",
        "man", "guys", "dude", "bro", "buddy", "sir", "madam", "honey", "baby", "darling", "sweetie", "girl", "boy", "kid", "kids",
        "friend",
    };

    [GeneratedRegex(@"^\p{L}+(?:['’ʼ-]\p{L}+)*$")]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"\p{L}+(?:['’ʼ-]\p{L}+)*")]
    private static partial Regex Words();

    // What a wearer says to give their own name: "I'm X", "my name is X", "я X", "я — X", "мене звати X", "меня зовут X".
    [GeneratedRegex(@"(?i:\b(?:i am|i['’]m|my name is|я|мене звати|мене звуть|меня зовут))\s*[—–-]?\s*(?<name>\p{Lu}\p{L}+)")]
    private static partial Regex Introduction();

    public static string[] Tokens(string name) => name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether <paramref name="name"/> (one line) can be a person's name: 1 to 3 words of letters, hyphen and apostrophe, each
    /// with a capital first letter, at most 40 characters, none on the stoplist. <paramref name="evidence"/> is the line that
    /// says it, which lets a stoplisted word through when it is written with a capital in the middle of a sentence.
    /// </summary>
    public static bool IsName(string name, string evidence)
    {
        var tokens = Tokens(name);
        if (tokens.Length is 0 or > MaxTokens
            || name.Length > MaxLength
            || tokens.Any(t => t.Length < 2 || !TokenShape().IsMatch(t) || !char.IsUpper(t[0])))
        {
            return false;
        }

        return tokens.All(t => !Stoplist.Contains(t.ToLowerInvariant()) || CapitalizedMidSentence(t, evidence));
    }

    /// <summary>Whether every word of <paramref name="name"/> occurs in <paramref name="text"/>, in any case ending (<see cref="SameStem"/>).</summary>
    public static bool Occurs(string name, string text)
    {
        var words = Words().Matches(text).Select(m => m.Value).ToList();
        return Tokens(name).All(t => words.Any(w => SameStem(t, w)));
    }

    /// <summary>
    /// The segment that says the name: the model's own when its text does, else the nearest of the
    /// <see cref="EvidenceReach"/> segments either side that does (the earlier on a tie); null when none does.
    /// </summary>
    public static NameSegment? Evidence(string name, long segmentId, IReadOnlyList<NameSegment> segments)
    {
        var at = 0;
        while (at < segments.Count && segments[at].Id != segmentId)
        {
            at++;
        }

        if (at == segments.Count)
        {
            return null;
        }

        for (var distance = 0; distance <= EvidenceReach; distance++)
        {
            foreach (var index in distance == 0 ? [at] : new[] { at - distance, at + distance })
            {
                if (index >= 0 && index < segments.Count && Occurs(name, segments[index].Text))
                {
                    return segments[index];
                }
            }
        }

        return null;
    }

    /// <summary>The names the wearer's own segments give for themselves ("I'm X", "я X").</summary>
    public static IReadOnlyList<string> WearerNames(IEnumerable<NameSegment> segments) =>
        segments.Where(s => s.IsWearer)
            .SelectMany(s => Introduction().Matches(s.Text).Select(m => m.Groups["name"].Value))
            .Distinct().ToList();

    /// <summary>Whether any word of <paramref name="name"/> is a word of <paramref name="wearerName"/> (<c>memories.userName</c> may be a full name).</summary>
    public static bool SameName(string name, string wearerName) =>
        Tokens(name).Any(t => Tokens(wearerName).Any(w => SameStem(t, w)));

    /// <summary>
    /// Two words with one stem, as Діма, Діму and Дімі: the same first three letters in any case. A word of under three letters
    /// must be the other word, or begin it and differ by at most two letters.
    /// </summary>
    public static bool SameStem(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var common = 0;
        while (common < length && char.ToLowerInvariant(a[common]) == char.ToLowerInvariant(b[common]))
        {
            common++;
        }

        return length >= 3 ? common >= 3 : common == length && Math.Abs(a.Length - b.Length) <= 2;
    }

    private static bool CapitalizedMidSentence(string token, string text) =>
        Words().Matches(text).Any(m =>
            char.IsUpper(m.Value[0]) && SameStem(token, m.Value) && !StartsSentence(text, m.Index));

    /// <summary>True when only non-letters come before <paramref name="index"/>, or the last mark before it ends a sentence.</summary>
    private static bool StartsSentence(string text, int index)
    {
        var before = text[..index].TrimEnd();
        return !before.Any(char.IsLetterOrDigit) || before.EndsWith('.') || before.EndsWith('!') || before.EndsWith('?') || before.EndsWith('…');
    }
}
