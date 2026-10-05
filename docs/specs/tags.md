# Nytka, tags and roles: more ways to find a conversation or a person

The owner's request, in his words: "we could add tags to people and conversations, more ways to track it
all. For example, this morning I had a repairman, which could be named like 'he is' + his name."

This spec reads it as three parts, each usable without the next:

1. **Tags.** Free-form, short, lowercase words on conversations and on people (`repairman`, `work`,
   `family`). A tag has a name and a use count. Tags show in lists, filter lists and search, and are in
   MCP and the export. Only you add or remove them.
2. **Roles.** Someone you know by what they do before you know their name (the repairman). The name
   suggester of the People milestone (layer 1 of [people.md](people.md)) also proposes a role for an
   unnamed voice from how others refer to it ("the repairman is here", "майстер приїхав", "дякую,
   майстре"). Accepting creates a person shown as "Repairman" with the tag `repairman`. When the name
   is said later, the suggestion carries both, and accepting it names that person.
3. **Proposed tags.** The model may propose tags for a conversation (in the summary step) and for a
   person (in fact extraction). A proposal changes nothing until you tap Accept. No tag comes from how a
   voice sounds.

## Done when

1. `PUT` and `DELETE /api/v1/conversations/{id}/tags/{name}` and the same under `/people/{id}` add and
   remove one tag; a name is normalized (below) and an invalid one is `400`.
2. Conversation and person lists, a conversation, the person page, MCP and the export carry `tags`.
3. `GET /api/v1/tags` lists every tag in use with its counts. A tag with no use left is gone.
4. `GET /api/v1/conversations?tag=`, `GET /api/v1/people?tag=` and `GET /api/v1/search?tag=` return only
   items with that tag.
5. A tag can be renamed, merged into another, or deleted everywhere, by an `admin` token only.
6. With `tags.suggest` on (the default) and the model configured, a summarized conversation may get up
   to 3 proposed tags and each involved person up to 3; they wait in the review inbox and are never
   applied without Accept. A rejected tag is never proposed again for that item.
7. A name suggestion can carry a role. Accepting a role without a name creates a person named after the
   role ("Repairman", or "Repairman 2" when taken) who is marked `named: false` and has the role as a
   tag. A later suggestion with a name for that person's voice renames them.
8. No tag name appears in a log line or an error message.

## What exists today

Verified against `main` at `4ef9667`:

- No tags anywhere in the code or the schema. `docs/vision.md` lists "opt-in location tags" under After
  1.0; that is location, not this.
- `people.name` is `not null`, 1 to 80 characters, unique by `lower(name)` (migration 0009,
  `people_name`). Several readers rely on that: `NameSuggestionStore.PeopleByNameAsync` builds a
  dictionary by lower-case name (`ToDictionary` throws on a duplicate), and fact extraction and calendar
  briefs match people by name.
- `name_suggestions.name` is `not null`, 1 to 80; one row per target and `lower(name)`, rejected ones
  included (migration 0015). `target` is `speaker`, `group` or `label`.
- `NameTargets.Find` makes a target only of segments that are not the wearer's and have no person
  (`NameSegment.Unnamed`). A segment that belongs to a person is never suggested for again.
- `NamePrompt.Schema` is strict (`additionalProperties: false`, every property required).
  `ConversationPrompt.Schema` already uses a nullable string (`"person": ["string", "null"]`), so a
  nullable field works with the configured model.
- The app decodes JSON with `coerceInputValues = true` (app `NytkaApi.kt`), and its `NameSuggestion.name`
  is a non-null `String = ""`: a `null` name would show as an empty name in today's app.
- `ConversationStore.MergeIntoAsync` moves batches, segments, audio, tasks and memories of merged
  conversations to the survivor; `PeopleStore.MergeAsync` moves voices, segment links, facts and
  voiceprints. Neither knows about tags yet.
- MCP is read-only (`ReadOnly = true` on every tool); it gets no write tool here either.
- Dapper reads arrays badly into records (comment in `NameSuggestionStore.Pending`); `PeopleStore.ListAsync`
  reads voices in a second query and joins them in C#. Tags are read the same way.
- `ReviewItem.ConversationId` is a non-null `Guid`: every inbox item points at a conversation.

## Tags

**A tag** is a name. Normalizing: trim, drop one leading `#`, lowercase (invariant culture), and turn
each run of spaces into one `-`. What is left must be 1 to 32 characters of letters (any script),
digits, `-` and `_`, starting with a letter or digit. `Repairman` becomes `repairman`, `#Робота`
becomes `робота`, `dog walker` becomes `dog-walker`; `a/b` is `400`. A model's tag that fails is dropped.

**Limits.** At most 20 tags on one conversation or person (`409` "This item has 20 tags."). No limit on
the number of tags in use: one owner, and a list of a few hundred names is small.

**Use count.** `conversations` and `people` are counted from the link tables when read, never stored, so
they cannot drift. `uses` is their sum.

**A tag with no use left is gone.** A statement trigger after a delete on either link table deletes
tags with no link left. It fires for every path: removing a tag, deleting a conversation or a person
(links cascade), merging. A pending proposal names its tag as text, so it does not keep a
tag alive and is not lost when one goes.

**Merges.** A conversation merged into another (`MergeIntoAsync`) gives its tags and pending
proposals to the survivor; a person merged into another (`POST /people/{id}/merge`) gives theirs to
the target. A duplicate is dropped, never an error.

**Storage: tables, not an array.** `tags (id, name unique, created_at)` with link tables
`conversation_tags` and `person_tags`. Against a `text[]` column on `conversations` and `people`:
renaming or merging a tag is one row instead of a rewrite of every item that holds it; foreign keys
with cascade remove links with their item; counts and the vanishing rule are plain SQL; and the
filter is an index lookup on `(tag_id)`. The array would save one join on the list, which reads its
tags in a second query anyway (see What exists today).

## Roles

**Why through name suggestions.** A role is a name you do not know yet. The suggester already reads
every unnamed voice, shows its evidence line and waits for a tap; a role is one more field of the same
suggestion, with the same drop rules, inbox and banner.

**The call.** `NamePrompt.Schema` changes: `name` becomes `["string", "null"]` and each item gains
`role` (`["string", "null"]`). The system message adds: give a role only when the transcript ties one to
the voice (the voice says "I'm the plumber", or another speaker says "the repairman is here" or
addresses it as "майстре"), as the base form of the word in the conversation's language, singular and
lowercase ("майстер", not "майстре"); never from how the voice sounds, and never age, gender, health,
religion, ethnicity or politics. A voice of a person known only by role is sent as `Voice A (known as:
repairman)`, so a name said later is proposed for that person.

**Apply.** The role is normalized as a tag; an invalid one becomes null. A suggestion with neither a
valid name nor a role is dropped. Otherwise the rules of [people.md](people.md) Layer 1 stand.

**What is stored.** `name_suggestions` gains `role text null` and `named boolean`. A role-only
suggestion stores the role's display form in `name` ("Repairman": first letter upper case, `-` as a
space) and `named = false`. So `name` stays non-null, the "rejected name is never offered again" index
works for roles unchanged, and today's app shows "A voice may be Repairman" instead of an empty name.

**A person known only by role.** `people` gains `named boolean not null default true`. A role-only
person has `named = false` and the display form as `name`, numbered when taken ("Repairman 2"), so the
unique name rule and every reader that matches by name stay as they are. Renaming the person (`PATCH
/people/{id}` with a name, or accepting a name for them) sets `named = true`. Their role stays as a tag.

**Accepting**, in the suggestion's one transaction:

| Suggestion | Effect |
|---|---|
| name, no role | as today |
| name and role | as today, and the person gets the role as a tag |
| role, no name | a **new** person (never one found by name: two repairmen are two people until you merge them), `named = false`, the role as a tag, linked as the target says |
| target `person` (new: the voice of a role-only person) with a name | renames that person and sets `named = true`; when a person of that name exists, merges the role-only person into them (`MergeAsync`) |

**Role or plain tag.** The role is a plain tag (`repairman`), not `role:repairman`. One filter then finds
the person and the conversations you tagged `repairman`; a prefix would split them and add `:` to the
allowed characters for one use. Whether a person is known only by role is `named`, not the tag.

**Across conversations.** A role-only person is matched to a later conversation by voice (Layer 2, when
on) or by hand, as anyone else. Nothing matches roles by text across conversations.

## Proposed tags

**Conversations.** `ConversationPrompt.Schema` gains `tags` (an array of strings), required, so the
same strict validation and retries apply. The system message asks for at most 3 topic or context tags
("work", "repair", "doctor"), reusing a name from the `Tags:` line when one fits, never a person's name,
none for a brief conversation, and the same excluded subjects as roles. The user message gets a `Tags:`
line with up to 100 tags in use, most used first. The merge call merges the parts' tags. With
`tags.suggest` off the schema still has `tags`, the line is not sent and the answer is ignored.

**People.** `extract-person-facts`' schema gains `tags: [{ personId, tag }]`, at most 3 per person, only
for a `personId` that was sent, under the same rules. Facts and tags are stored in one transaction.

**Stored as proposals.** `tag_suggestions`: `id`, `conversation_id` (where it came from; cascade),
`person_id` (null for a conversation's tag; cascade), `name`, `status` (`pending`, `accepted`,
`rejected`), `created_at`, `decided_at`. One row per item and name, rejected included, so a rejected or
removed tag is never proposed again for that item. A tag the item already has is not stored.

**Answers.** Accept adds the tag (`409` when the item has 20); reject keeps the row. Both are in
`GET /api/v1/review` as kind `tag`, beside `name`, `voice` and `label`.

## Which label wins

Unchanged. A role-only person's segments show their `name` ("Repairman") by the same three steps.

## Search

`GET /api/v1/search?tag=work` keeps only conversation and person hits whose item has that tag; with
`tag`, memories are left out (they have none). The words in `q` are not matched against tag names:
that would put tags in the `search` vectors, so every link change would clear a vector and
`nytka_search_setup()` would be restated again. Searching by tag is the filter. `q` stays required.

## Who may write

Reads (`GET /tags`, lists, filters, proposals) take `read`. Every add, remove, rename, merge, delete and
answer needs `admin` (invariant 8). MCP gets no write tool.

## Webhooks

No new event. `conversation.ready` gains `tags` (the conversation's tags when it was summarized).
Adding a tag is an edit from the app; an event per tap would need a subject per link, and
`NytkaEvent` carries one id. A receiver that needs current tags reads `GET /conversations/{id}`.

## Export

`conversation` and `person` lines gain `tags` (names, sorted); `person` gains `named`. No `tag` line: a
tag is only its name, and counts follow from the lines. Proposals are working state and are not
exported. The format `version` stays `1`: fields are only added, and a reader ignores what it does not
know.

## Privacy

Tags are the owner's words about people and moments and can be sensitive ("therapy"). No tag name in a
log line or an error message (invariant 5); routes answer fixed sentences. With `tags.suggest` on, up
to 100 tag names go to the language model with each summary and fact request; README's "What it stores"
and "Configuration and security" say so. A tag never comes from audio: the model reads text only, and
the prompts forbid tags about how someone sounds.

## What it stores

Migrations `0021` to `0023`, numbers fixed by the plan:

- `tags`: `id uuid`, `name text` (unique, 1 to 32, checked normalized), `created_at`.
- `conversation_tags`, `person_tags`: (`conversation_id` or `person_id`, `tag_id`) as key, both cascade,
  `created_at`; an index on `tag_id`; the statement trigger that deletes unused tags.
- `tag_suggestions` as above.
- `people.named boolean not null default true`.
- `name_suggestions`: `role text null`, `named boolean not null default true`, `person_id` usable as a
  target key, `target` also `person`; the unique index restated with the person key.

## Settings

| Key | Variable | Default | Meaning |
|---|---|---|---|
| `tags.suggest` | `Nytka__Tags__Suggest` | `true` | The model may propose tags for conversations and people |

Roles follow `people.suggestNames`: they are part of the name suggestion.

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/tags?q=` | read | `{ items: [{ name, conversations, people, uses }] }`, most used first, then by name; `q` keeps names starting with it |
| `PUT /api/v1/conversations/{id}/tags/{name}` | admin | adds; `200 { tags }`; `400` invalid name, `404` no conversation, `409` 20 tags |
| `DELETE /api/v1/conversations/{id}/tags/{name}` | admin | removes; `200 { tags }`, also when it was not there |
| `PUT`, `DELETE /api/v1/people/{id}/tags/{name}` | admin | the same for a person |
| `POST /api/v1/tags/{name}/rename` `{ name }` | admin | `200` with the tag; `404`; `409` when the new name is in use (merge instead) |
| `POST /api/v1/tags/{name}/merge` `{ into }` | admin | moves every link to `into` (created if needed), drops duplicates and the old tag; `200` with `into` |
| `DELETE /api/v1/tags/{name}` | admin | removes the tag from everything; `204` |
| `GET /api/v1/conversations?tag=` | read | as today, only conversations with the tag; items gain `tags` |
| `GET /api/v1/conversations/{id}` | read | gains `tags` |
| `GET /api/v1/people?tag=` | read | as today, only people with the tag; items gain `tags` and `named` |
| `GET /api/v1/people/{id}` | read | gains `tags` and `named` |
| `GET /api/v1/search?tag=` | read | see Search |
| `GET /api/v1/tags/suggestions?status=pending` | read | `{ items: [{ id, conversationId, personId, personName, name, createdAt }] }`, newest 200 |
| `POST /api/v1/tags/suggestions/{id}/accept`, `/reject` | admin | `200 { tags }` of the item, or `204`; `409` when no longer pending or the item has 20 |
| `GET /api/v1/people/suggestions` | read | items gain `role` and `named`; `target` may be `person` |
| `GET /api/v1/review` | read | kind `tag`; `proposal` gains `tag`, `role` and `named` |
| `POST /api/v1/review/tag/{id}/accept`, `/reject` | admin | as the tag suggestion routes |

A tag name in a path is URL-encoded (Cyrillic works) and normalized before lookup, so
`/tags/Robota` finds `robota`.

`/api/v1/info` `features` gains `tags` (T-1), `tag-suggestions` (T-4) and `roles` (T-6), so the app
gates each part on its own flag.

**MCP.** New `list_tags` (as `GET /tags`). `list_conversations`, `list_people` and `search` take `tag`;
their items, `get_conversation` and `get_person` carry `tags`, and people `named`.

## How we measure it

On the owner's server, labels kept outside the repository:

- **Roles.** Accept or reject the first 30 role suggestions. Pass: at least 80% accepted, and no role
  that names how someone sounds. Below that, narrow the prompt.
- **Proposed tags.** Over two weeks, the share accepted of all proposed. Below 30%, the proposals are
  noise: turn `tags.suggest` off by default and say so in the README.

In the repository: a hand-written `ILlmClient` and synthetic names, as today.

## Conflicts with the code

1. **`people.name` is required and unique.** A role-only person needs a name to show and must not
   collide with another "Repairman", so it gets the display form and a number, plus `named = false`.
   Making `name` nullable would change every reader of the label rule, the export, briefs and the app.
2. **`name_suggestions.name` is required and today's app shows a missing name as empty.** A role-only
   suggestion therefore stores the role's display form as `name`, with `role` and `named` beside it.
3. **A person's segments are never a target.** Role-only persons are the exception (target `person`),
   or a name said later could never reach them.
4. **Both strict schemas change.** An answer without `tags` (or `role`) fails validation and retries as
   an old-shape answer does today; fakes in tests change with them.
5. **Merges.** `MergeIntoAsync` and `PeopleStore.MergeAsync` gain the tag moves; missing them would
   cascade-delete links of the merged item.
6. **The review inbox requires a conversation.** A person's proposed tag keeps the conversation it
   came from, which also lets the inbox open it.
7. **Feature flags.** The People milestone learned that one flag does not mark a release; each part
   here gets its own.

## Out of scope

- Tags on tasks, memories, bookmarks or segments; tag colors, icons, groups or hierarchy.
- Several tags in one filter (`tag=a&tag=b`), and matching `q` against tag names.
- Tags or roles from audio, voice traits or location; automatic tags without a tap.
- Matching a role-only person across conversations by text.
- Writing tags over MCP or Ask; Ask and the daily digest do not read tags.
- Webhook events for tag changes.
- Translating tags between languages (`майстер` and `repairman` are two tags; merge them by hand).

## Review focus

1. **Nothing applies itself.** Only accept routes and the owner's `PUT` write a link from a model's
   output; tests compare tags before and after.
2. **No tag text in logs**, errors or problem titles.
3. **Unused tags vanish** on every delete path, cascades included.
4. **Role-only people** never reuse a person by name, and never break name uniqueness.
5. **Migrations** keep their numbers.
