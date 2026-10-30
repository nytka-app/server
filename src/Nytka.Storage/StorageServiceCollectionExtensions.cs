using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Nytka.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data source and the stores. The connection string is read when the data
    /// source is first resolved, so test hosts can supply it late.
    /// </summary>
    public static IServiceCollection AddNytkaStorage(this IServiceCollection services)
    {
        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.")));

        services.AddSingleton<ChunkStore>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<BatchStore>();
        services.AddSingleton<TranscriptPassStore>();
        services.AddSingleton<DiagnosticsStore>();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<SettingStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<NoteStore>();
        services.AddSingleton<TodayStore>();
        services.AddSingleton<MemoryStore>();
        services.AddSingleton<SearchStore>();
        services.AddSingleton<EmbeddingStore>();
        services.AddSingleton<WebhookStore>();
        services.AddSingleton<PeopleStore>();
        services.AddSingleton<PersonFactStore>();
        services.AddSingleton<PeopleBackfillStore>();
        services.AddSingleton<ImportStore>();
        services.AddSingleton<RestoreStore>();
        services.AddSingleton<BookmarkStore>();
        services.AddSingleton<DigestStore>();
        services.AddSingleton<ExportStore>();
        services.AddSingleton<ObsidianExportStore>();
        services.AddSingleton<VoiceStore>();
        services.AddSingleton<NameSuggestionStore>();
        services.AddSingleton<VoiceGroupStore>();
        services.AddSingleton<ReviewStore>();
        services.AddSingleton<CalendarStore>();
        services.AddSingleton<TagStore>();
        services.AddSingleton<TopicStore>();
        services.AddSingleton<RollupStore>();
        services.AddSingleton<RelatedStore>();
        services.AddSingleton<TagSuggestionStore>();
        services.AddSingleton<SpeechStore>();
        services.AddSingleton<LabelStore>();
        services.AddSingleton<GoldenStore>();
        services.AddSingleton<ContextRangeStore>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<QualityStore>();
        services.AddSingleton<InsightStore>();
        services.AddSingleton<NudgeStore>();
        services.AddSingleton<DayEventStore>();
        services.AddSingleton<TrackStore>();
        services.AddSingleton<LinkStore>();
        services.AddSingleton<TrackNameStore>();

        return services;
    }
}
