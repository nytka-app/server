using Nytka.Server.Usage;

namespace Nytka.Server.Tests.Usage;

public class PriceTableTests
{
    [Fact]
    public void The_default_table_matches_the_bill_it_was_fitted_to()
    {
        // 27 September to 20 October 2026: 4,717 batches, 16.03 hours of speech, 811,269 characters, billed $8.10.
        var rate = PriceTable.Parse(PriceTable.Default).RateOf("soniox");

        var cost = 4_717 * rate.PerRequest + 16.03m * 3_600_000 * rate.PerAudioMs + 811_269 * rate.PerTextChar;

        Assert.InRange(cost, 8.00m, 8.20m);
    }

    [Fact]
    public void A_request_costs_its_context_its_audio_and_its_text()
    {
        var rate = PriceTable.Parse("""{"soniox":{"audioTokensPerHour":30000,"audioUsdPerMillionTokens":1.5,"requestTokens":390,"textInUsdPerMillionTokens":3.5,"textCharsPerToken":3,"textOutUsdPerMillionTokens":3.5}}""")
            .RateOf("soniox");

        // 390 × $3.50/M + 12 s × 30,000/h × $1.50/M + 300 / 3 × $3.50/M
        Assert.Equal(0.001365m + 0.00015m + 0.00035m, rate.Cost(12_000, 300));
    }

    [Fact]
    public void Engines_match_in_any_case_and_an_unlisted_one_costs_nothing()
    {
        var table = PriceTable.Parse("""{"Soniox":{"requestTokens":1000000,"textInUsdPerMillionTokens":2}}""");

        Assert.Equal(2m, table.RateOf("soniox").Cost(0, 0));
        Assert.Equal(0m, table.RateOf("parakeet").Cost(60_000, 500));
    }

    [Fact]
    public void Left_out_prices_are_zero()
    {
        var rate = PriceTable.Parse("""{"whisper-1":{}}""").RateOf("whisper-1");

        Assert.Equal(0m, rate.Cost(3_600_000, 10_000));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"soniox":1}""")]
    [InlineData("""{"soniox":{"audioPerHour":1}}""")]
    [InlineData("""{"soniox":{"requestTokens":-1}}""")]
    [InlineData("""{"soniox":{"requestTokens":"390"}}""")]
    [InlineData("""{" ":{}}""")]
    [InlineData("""{"soniox":{},"SONIOX":{}}""")]
    public void A_table_that_is_not_one_is_refused(string json)
    {
        Assert.NotNull(PriceTable.Validate(json));
        Assert.Equal(0m, PriceTable.Parse(json).RateOf("soniox").Cost(60_000, 100));
    }

    [Fact]
    public void The_default_is_valid()
    {
        Assert.Null(PriceTable.Validate(PriceTable.Default));
    }
}
