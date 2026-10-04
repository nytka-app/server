using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Jobs;

public class JobKindsTests
{
    [Theory]
    [InlineData(JobKinds.ProcessSession, JobLane.Audio)]
    [InlineData(JobKinds.Transcribe, JobLane.Audio)]
    [InlineData(JobKinds.CloseConversations, JobLane.Audio)]
    [InlineData(JobKinds.Retention, JobLane.Audio)]
    [InlineData(JobKinds.EnrichConversation, JobLane.Ai)]
    [InlineData(JobKinds.ExtractMemories, JobLane.Ai)]
    [InlineData(JobKinds.MakeDigest, JobLane.Ai)]
    [InlineData(JobKinds.SuggestNames, JobLane.Ai)]
    [InlineData(JobKinds.DeliverWebhook, JobLane.Hooks)]
    [InlineData("nobody-handles-this", JobLane.Audio)]
    public void A_kind_runs_in_its_lane_and_an_unclaimed_kind_in_audio(string kind, JobLane lane) =>
        Assert.Equal(lane, JobKinds.LaneOf(kind));

    [Fact]
    public void The_kinds_are_the_names_the_specs_give_them()
    {
        Assert.Equal("enrich-conversation", JobKinds.EnrichConversation);
        Assert.Equal("extract-memories", JobKinds.ExtractMemories);
        Assert.Equal("deliver-webhook", JobKinds.DeliverWebhook);
    }

    [Fact]
    public void Dedupe_keys_are_the_kind_and_the_subject()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal($"enrich-conversation:{id}", JobKinds.EnrichConversationKey(id));
        Assert.Equal($"extract-memories:{id}", JobKinds.ExtractMemoriesKey(id));
        Assert.Equal($"deliver-webhook:{id}", JobKinds.DeliverWebhookKey(id));
    }

    [Fact]
    public void The_ai_and_hooks_lanes_take_their_kinds_and_audio_takes_the_rest()
    {
        var ai = JobKinds.FilterOf(JobLane.Ai);
        var hooks = JobKinds.FilterOf(JobLane.Hooks);
        var audio = JobKinds.FilterOf(JobLane.Audio);

        Assert.False(ai.Exclude);
        Assert.Equal(["enrich-conversation", "extract-memories", "make-digest", "suggest-names"], ai.Kinds.Order());
        Assert.False(hooks.Exclude);
        Assert.Equal(["deliver-webhook"], hooks.Kinds);
        Assert.True(audio.Exclude);
        Assert.Equal(["deliver-webhook", "enrich-conversation", "extract-memories", "make-digest", "suggest-names"], audio.Kinds.Order());
    }
}
