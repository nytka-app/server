using Nytka.Server.Memories;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary><c>checked</c>: pending model suggestions read. <c>removed</c>: those that failed and were deleted.</summary>
public sealed record RevalidateResult(int Checked, int Removed, int Kept);

/// <summary>
/// Runs the current name rules (and, for a role-only suggestion, the role rules) over the pending suggestions the model made and deletes the ones that fail (docs/specs/people.md,
/// Validation). The status column cannot say "rejected by the system", and a rejected name is never offered again, so a failing
/// row is deleted: a new run may offer the same name with a better evidence line.
/// </summary>
public sealed class SuggestionRevalidator(NameSuggestionStore suggestions, SettingsService settings)
{
    public async Task<RevalidateResult> RunAsync(CancellationToken ct)
    {
        var pending = await suggestions.PendingFromModelAsync(ct);
        var userName = MemorySettings.UserName(settings);
        var conversations = new Dictionary<Guid, IReadOnlyList<NameSegment>>();
        var failing = new List<Guid>();
        foreach (var item in pending)
        {
            if (!conversations.TryGetValue(item.ConversationId, out var segments))
            {
                var input = await suggestions.ReadInputAsync(item.ConversationId, ct);
                conversations[item.ConversationId] = segments = input?.Segments ?? [];
            }

            var wearer = NameValidator.WearerNames(segments);
            var name = string.Join(' ', NameValidator.Tokens(item.Name));
            // A role-only suggestion holds the role's display form as its name, which the line need not write.
            var role = item.Named ? null : TagName.Normalize(item.Role);
            if (item.Named
                    ? !NameValidator.IsName(name, item.EvidenceText)
                        || !NameValidator.IsEvidenceFor(name, item.EvidenceText)
                        || userName is not null && NameValidator.SameName(name, userName)
                        || wearer.Any(w => NameValidator.SameName(name, w))
                    : role is null
                        || !NameValidator.IsRole(role)
                        || !NameValidator.Occurs(NameValidator.RoleWords(role), item.EvidenceText)
                        || NameValidator.IsWearerRole(role, segments))
            {
                failing.Add(item.Id);
            }
        }

        var removed = failing.Count == 0 ? 0 : await suggestions.DeletePendingAsync(failing, ct);
        return new RevalidateResult(pending.Count, removed, pending.Count - removed);
    }
}
