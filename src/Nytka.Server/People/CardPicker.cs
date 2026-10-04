using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>One line a card shows: a segment of the stretch the clip covers.</summary>
public sealed record CardLine(long SegmentId, DateTime StartedAt, string Text);

/// <summary>
/// "Who is this?" or "Is this Olena?": one clean stretch of one owner in one conversation. The clip is capture times
/// <paramref name="From"/> to <paramref name="Until"/>, at most <see cref="CardPicker.MaxClipMs"/> long.
/// </summary>
public sealed record Card(
    CardOwner Owner, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, DateTime From, DateTime Until,
    IReadOnlyList<CardLine> Lines);

/// <summary>
/// Picks the cards (docs/specs/people.md, Cards) from rows, with no database and no clock. A stretch is consecutive segments of one
/// owner in one conversation, with no other segment between them, together at least <see cref="MinStretchMs"/>. A set holds at most
/// <see cref="MaxCards"/> cards, at most <see cref="MaxPerConversation"/> from one conversation, the newest conversation first.
/// </summary>
public static class CardPicker
{
    public const int MinStretchMs = 5000;
    public const int MaxClipMs = 10_000;
    public const int MaxCards = 4;
    public const int MaxPerConversation = 2;

    /// <summary>The cards of <paramref name="owners"/>, one per owner, from <paramref name="segments"/> (every owner's, in any order).</summary>
    public static IReadOnlyList<Card> Pick(IReadOnlyList<CardOwner> owners, IReadOnlyList<CardSegment> segments)
    {
        var picked = new List<Card>();
        var taken = new HashSet<Guid>();
        var perConversation = new Dictionary<Guid, int>();
        foreach (var card in owners.SelectMany(o => Cards(o, segments))
                     .OrderByDescending(c => c.ConversationStartedAt).ThenByDescending(c => c.From).ThenBy(c => c.Owner.Id))
        {
            if (picked.Count == MaxCards)
            {
                break;
            }

            if (taken.Contains(card.Owner.Id) || perConversation.GetValueOrDefault(card.ConversationId) >= MaxPerConversation)
            {
                continue;
            }

            taken.Add(card.Owner.Id);
            perConversation[card.ConversationId] = perConversation.GetValueOrDefault(card.ConversationId) + 1;
            picked.Add(card);
        }

        return picked;
    }

    /// <summary>The newest card of one owner, or null when it has no clean stretch.</summary>
    public static Card? For(CardOwner owner, IReadOnlyList<CardSegment> segments) =>
        Cards(owner, segments).OrderByDescending(c => c.ConversationStartedAt).ThenByDescending(c => c.From).FirstOrDefault();

    /// <summary>A card for each clean stretch of the owner.</summary>
    private static IEnumerable<Card> Cards(CardOwner owner, IReadOnlyList<CardSegment> segments)
    {
        foreach (var conversation in segments.Where(s => s.Kind == owner.Kind && s.OwnerId == owner.Id).GroupBy(s => s.ConversationId))
        {
            var stretch = new List<CardSegment>();
            foreach (var segment in conversation.OrderBy(s => s.Ordinal))
            {
                if (stretch.Count > 0 && segment.Ordinal != stretch[^1].Ordinal + 1)
                {
                    if (StretchCard(owner, stretch) is { } card)
                    {
                        yield return card;
                    }

                    stretch.Clear();
                }

                stretch.Add(segment);
            }

            if (StretchCard(owner, stretch) is { } last)
            {
                yield return last;
            }
        }
    }

    private static Card? StretchCard(CardOwner owner, List<CardSegment> stretch)
    {
        if (stretch.Sum(s => Math.Max(0, (s.EndedAt - s.StartedAt).TotalMilliseconds)) < MinStretchMs)
        {
            return null;
        }

        var first = stretch[0];
        var from = first.StartedAt;
        var until = new[] { stretch[^1].EndedAt, from.AddMilliseconds(MaxClipMs) }.Min();
        return new Card(
            owner, first.ConversationId, first.ConversationTitle, first.ConversationStartedAt, from, until,
            [.. stretch.Where(s => s.StartedAt < until).Select(s => new CardLine(s.SegmentId, s.StartedAt, s.Text))]);
    }
}
