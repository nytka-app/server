using Nytka.Server.Memories;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary><c>checked</c>: pending model suggestions read. <c>removed</c>: those that failed and were deleted.</summary>
public sealed record RevalidateResult(int Checked, int Removed, int Kept);

/// <summary>
/// Runs the current name rules over the pending suggestions the model made and deletes the ones that fail (docs/specs/people.md,
/// Validation). The status column cannot say "rejected by the system", and a rejected name is never offered again, so a failing
/// row is deleted: a new run may offer the same name with a better evidence line.
/// </summary>
public sealed class SuggestionRevalidator(NameSuggestionStore suggestions, SettingsService settings)
{
    public async Task<RevalidateResult> RunAsync(CancellationToken ct)
    {
        var pending = await suggestions.PendingFromModelAsync(ct);
        var userName = MemorySettings.UserName(settings);
        var wearers = new Dictionary<Guid, IReadOnlyList<string>>();
        var failing = new List<Guid>();
        foreach (var item in pending)
        {
            if (!wearers.TryGetValue(item.ConversationId, out var wearer))
            {
                var input = await suggestions.ReadInputAsync(item.ConversationId, ct);
                wearers[item.ConversationId] = wearer = input is null ? [] : NameValidator.WearerNames(input.Segments);
            }

            var name = string.Join(' ', NameValidator.Tokens(item.Name));
            if (!NameValidator.IsName(name, item.EvidenceText)
                || !NameValidator.Occurs(name, item.EvidenceText)
                || userName is not null && NameValidator.SameName(name, userName)
                || wearer.Any(w => NameValidator.SameName(name, w)))
            {
                failing.Add(item.Id);
            }
        }

        var removed = failing.Count == 0 ? 0 : await suggestions.DeletePendingAsync(failing, ct);
        return new RevalidateResult(pending.Count, removed, pending.Count - removed);
    }
}
