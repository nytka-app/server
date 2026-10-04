using Nytka.Server.People;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>The card rules of docs/specs/people.md (Cards), over rows, without a database.</summary>
public sealed class CardPickerTests
{
    private static readonly DateTime Day = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private static CardOwner Owner(int n) => new("group", new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), null, null, null);

    private static readonly Guid Morning = Guid.Parse("018f0000-0000-7000-8000-0000000000c1");
    private static readonly Guid Noon = Guid.Parse("018f0000-0000-7000-8000-0000000000c2");

    /// <summary>A segment of <paramref name="owner"/> from second <paramref name="start"/> to <paramref name="end"/> of its conversation.</summary>
    private static CardSegment Seg(
        CardOwner owner, int ordinal, double start, double end, Guid? conversation = null, int hour = 10, long? id = null) => new(
        owner.Kind, owner.Id, conversation ?? Morning, "title", Day.AddHours(hour - 10), id ?? ordinal, ordinal,
        Day.AddSeconds(start), Day.AddSeconds(end), $"line {ordinal}");

    [Fact]
    public void A_stretch_needs_at_least_5_s_together()
    {
        var olena = Owner(1);

        Assert.Empty(CardPicker.Pick([olena], [Seg(olena, 1, 0, 4)]));
        Assert.Empty(CardPicker.Pick([olena], [Seg(olena, 1, 0, 2), Seg(olena, 2, 2, 4.9)]));
        Assert.Single(CardPicker.Pick([olena], [Seg(olena, 1, 0, 6)]));
        Assert.Single(CardPicker.Pick([olena], [Seg(olena, 1, 0, 2), Seg(olena, 2, 2, 5)]));
    }

    [Fact]
    public void Another_segment_between_two_lines_splits_the_stretch()
    {
        var olena = Owner(1);

        Assert.Empty(CardPicker.Pick([olena], [Seg(olena, 1, 0, 3), Seg(olena, 3, 6, 9)]));
        var card = Assert.Single(CardPicker.Pick([olena], [Seg(olena, 1, 0, 3), Seg(olena, 3, 6, 9), Seg(olena, 4, 9, 12)]));
        Assert.Equal(Day.AddSeconds(6), card.From);
        Assert.Equal(new long[] { 3, 4 }, card.Lines.Select(l => l.SegmentId));
    }

    [Fact]
    public void The_clip_is_the_first_10_s_and_the_lines_are_those_it_starts_in()
    {
        var olena = Owner(1);
        var segments = Enumerable.Range(0, 10).Select(i => Seg(olena, i + 1, i * 3, (i * 3) + 3)).ToList();

        var card = Assert.Single(CardPicker.Pick([olena], segments));

        Assert.Equal((Day, Day.AddSeconds(10)), (card.From, card.Until));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, card.Lines.Select(l => l.SegmentId));
        Assert.Equal(Day.AddSeconds(6), Assert.Single(CardPicker.Pick([olena], [Seg(olena, 1, 0, 6)])).Until);
    }

    [Fact]
    public void A_set_holds_four_cards_and_two_from_one_conversation_newest_conversation_first()
    {
        var owners = Enumerable.Range(1, 5).Select(Owner).ToList();
        var inOne = owners.Take(3).Select((o, i) => Seg(o, i + 1, i * 6, (i * 6) + 6, Noon, hour: 12)).ToList();
        var later = Seg(owners[3], 1, 0, 6, Guid.NewGuid(), hour: 11);
        var earlier = Seg(owners[4], 1, 0, 6, Guid.NewGuid(), hour: 9);

        var cards = CardPicker.Pick(owners, [.. inOne, later, earlier]);

        Assert.Equal(4, cards.Count);
        Assert.Equal(new[] { Noon, Noon }, cards.Take(2).Select(c => c.ConversationId));
        Assert.Equal(new[] { owners[2].Id, owners[1].Id, owners[3].Id, owners[4].Id }, cards.Select(c => c.Owner.Id));
    }

    [Fact]
    public void Five_groups_in_five_conversations_give_four_cards_and_leave_the_oldest()
    {
        var owners = Enumerable.Range(1, 5).Select(Owner).ToList();
        var segments = owners.Select((o, i) => Seg(o, 1, 0, 6, Guid.NewGuid(), hour: 8 + i)).ToList();

        var cards = CardPicker.Pick(owners, segments);

        Assert.Equal(owners.AsEnumerable().Reverse().Take(4).Select(o => o.Id), cards.Select(c => c.Owner.Id));
    }

    [Fact]
    public void An_owner_gets_one_card_from_its_newest_stretch_and_one_without_a_stretch_gets_none()
    {
        var olena = Owner(1);
        var marko = Owner(2);
        var segments = new[] { Seg(olena, 1, 0, 6, Morning, hour: 9), Seg(olena, 1, 0, 6, Noon, hour: 12) };

        var card = Assert.Single(CardPicker.Pick([olena, marko], segments));

        Assert.Equal(Noon, card.ConversationId);
        Assert.Equal(Noon, CardPicker.For(olena, segments)!.ConversationId);
        Assert.Null(CardPicker.For(marko, segments));
    }
}
