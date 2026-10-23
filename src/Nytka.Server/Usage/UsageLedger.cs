using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Events;
using Nytka.Server.Settings;
using Nytka.Server.Transcription;
using Nytka.Storage;

namespace Nytka.Server.Usage;

/// <summary>
/// Where a batch goes: <paramref name="Model"/> is what Nytka sends as <c>model</c> (null: none), <paramref name="Engine"/>
/// its name in the ledger and the price table. <paramref name="OverBudget"/> when the month's budget is spent and the batch
/// goes to <c>stt.budgetModel</c> instead of the usual model. <paramref name="Soniox"/> when the batch goes to Soniox directly
/// (<c>Nytka__Stt__SonioxApiKey</c> is set) rather than to <c>stt.url</c>.
/// </summary>
public sealed record UsagePlan(string? Model, string Engine, bool OverBudget, bool Soniox = false);

/// <summary>
/// The transcription ledger (docs/specs/usage.md). Before a request, <see cref="PlanAsync"/> picks the model by the month's
/// spend; once the answer is stored, <see cref="RecordAsync"/> writes what it cost in the same transaction and raises the
/// budget events. The Audio lane sends one request at a time, so the spend a plan reads is never stale by more than one batch.
/// </summary>
public sealed class UsageLedger(UsageStore usage, SettingsService settings, IOptions<NytkaOptions> options, IEventPublisher events, TimeProvider time)
{
    public const string Warning = "warning";
    public const string Reached = "reached";

    /// <summary>The ledger's name for a model: lower case, or <c>auto</c> when none is sent and the endpoint picks.</summary>
    public static string Engine(string? model) => string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim().ToLowerInvariant();

    /// <summary>The first day of the month of <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public static DateOnly MonthOf(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DigestDay.Today(now, zone);
        return new DateOnly(today.Year, today.Month, 1);
    }

    public async Task<UsagePlan> PlanAsync(CancellationToken ct)
    {
        var stt = options.Value.Stt;
        var model = stt.Model;
        var usual = stt.UsesSoniox
            ? new UsagePlan(null, SonioxClient.Engine, false, Soniox: true)
            : new UsagePlan(string.IsNullOrWhiteSpace(model) ? null : model, Engine(model), false);
        if (UsageSettings.Budget(settings) is not { } budget)
        {
            return usual;
        }

        var budgetModel = UsageSettings.BudgetModel(settings);
        if (Engine(budgetModel) == usual.Engine)
        {
            return usual;
        }

        var zone = UserTimeZone.Resolve(settings);
        var spent = await usage.SpentSinceAsync(DigestDay.Bounds(MonthOf(time.GetUtcNow(), zone), zone).From, ct);
        return spent >= budget ? new UsagePlan(budgetModel, Engine(budgetModel), true) : usual;
    }

    /// <summary>
    /// Writes the request's row inside the batch's transaction, then records and publishes each budget crossing it caused:
    /// <c>usage.budget.warning</c> at <c>stt.budgetWarningPercent</c> of the budget, <c>usage.budget.reached</c> at all of it.
    /// </summary>
    public async Task RecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long batchId, Guid conversationId, UsagePlan plan, long audioMs, int textChars, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cost = UsageSettings.Prices(settings).RateOf(plan.Engine).Cost(audioMs, textChars);
        await UsageStore.InsertAsync(connection, transaction, new NewUsage(batchId, conversationId, now, plan.Engine, audioMs, textChars, cost, plan.OverBudget), ct);
        await RaiseCrossingsAsync(connection, transaction, now, ct);
    }

    /// <summary>Writes a paid pass's row inside the swap's transaction, and raises the budget crossings it caused, as <see cref="RecordAsync"/> does for a batch.</summary>
    public async Task RecordPassAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long passId, Guid conversationId, string engine, long audioMs, int textChars, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cost = UsageSettings.Prices(settings).RateOf(engine).Cost(audioMs, textChars);
        await UsageStore.InsertPassAsync(connection, transaction, new NewPassUsage(passId, conversationId, now, engine, audioMs, textChars, cost), ct);
        await RaiseCrossingsAsync(connection, transaction, now, ct);
    }

    /// <summary>
    /// Whether a pass of <paramref name="audioMs"/> may start: with a budget set, the month's spend is under it and the pass's
    /// estimated cost (<paramref name="textChars"/> being the draft's length) would not carry the month past it. No budget: always.
    /// </summary>
    public async Task<bool> CanAffordAsync(string engine, long audioMs, int textChars, CancellationToken ct)
    {
        if (UsageSettings.Budget(settings) is not { } budget)
        {
            return true;
        }

        var zone = UserTimeZone.Resolve(settings);
        var spent = await usage.SpentSinceAsync(DigestDay.Bounds(MonthOf(time.GetUtcNow(), zone), zone).From, ct);
        return spent < budget && spent + UsageSettings.Prices(settings).RateOf(engine).Cost(audioMs, textChars) <= budget;
    }

    private async Task RaiseCrossingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        if (UsageSettings.Budget(settings) is not { } budget)
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        var month = MonthOf(now, zone);
        var spent = await UsageStore.SpentSinceAsync(connection, transaction, DigestDay.Bounds(month, zone).From, ct);
        var crossed = new List<(string Kind, string Event)>();
        if (spent >= budget * UsageSettings.WarningPercent(settings) / 100m)
        {
            crossed.Add((Warning, NytkaEvent.UsageBudgetWarning));
        }

        if (spent >= budget)
        {
            crossed.Add((Reached, NytkaEvent.UsageBudgetReached));
        }

        foreach (var (kind, type) in crossed)
        {
            if (await UsageStore.InsertAlertAsync(connection, transaction, Guid.CreateVersion7(now), kind, month, budget, spent, now, ct) is { } id)
            {
                await events.PublishAsync(new NytkaEvent(type, id), connection, transaction, ct);
            }
        }
    }
}
