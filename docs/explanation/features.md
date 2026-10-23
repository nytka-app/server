# What Nytka does with a conversation

Each feature in turn: your voice, people, tags, speech kinds, tasks, memories, digests, calendar briefs, search, Ask and the rest.

## Your voice

Nytka learns your voice once and then marks the segments you said as yours, with any transcription
provider. A line marked yours shows as `Wearer` in the app, in the transcript the language model
reads, and in tasks, memories, Ask and MCP; other lines keep the provider's speaker or the name you
gave that voice. It needs the speaker model, TitaNet-small, which the Docker image carries
(`GET /api/v1/info` lists `voice` under `features` when it is there).

**Enroll.** In the app, read the prompts into the pendant for 30 to 60 seconds, in every language you
speak. The app sends the pendant's Opus frames to `POST /api/v1/voice/enrollment` instead of the
normal upload, so the reading never becomes a conversation. The server finds the speech, cuts it into
windows of 3 to 10 seconds, fingerprints each and keeps their mean as your voiceprint. It refuses
(`422`) a reading with less than 20 seconds of speech, or one whose windows disagree (a second voice or
heavy noise), and says which. `mode=add` blends a new reading into the voiceprint, for example for a
new language; the default `mode=replace` starts over. The audio is never stored.

**Matching.** Each new segment of 1 second (`Nytka__Voice__MinSegmentSeconds`) or longer gets a
similarity to your voiceprint, and is yours at `Nytka__Voice__UserThreshold` (0.38) or above. A shorter
segment gets no label rather than a guess. One over 30 seconds is fingerprinted in 10-second windows and
takes its least-like-you window, so it is yours only when every window is; it never teaches the
voiceprint. Long segments stored before this, while their audio is kept, are matched in the background
(`rematch-voice`). If fingerprinting fails, the transcript is stored anyway. Segments of 2 seconds or longer that match well
(`Nytka__Voice__LearnThreshold`) move the voiceprint towards your voice as the pendant hears it;
`POST /api/v1/voice/reset` goes back to what you enrolled. A threshold change or a new voiceprint
re-labels the stored segments.

**Which label wins.** Your own mark on a segment ("This is me" or "This is not me",
`PATCH /api/v1/segments/{id}`), then Nytka's verdict when Nytka checked the segment, then the
provider's `is_user`. A conversation's segments say which one decided in `isUserSource`, and how sure it is in
`wearerConfidence`: 1 or 0 for your mark, the similarity when Nytka's verdict decides, null otherwise. Marking a
segment of 2 seconds or longer as yours also teaches the voiceprint.

**Forget.** `DELETE /api/v1/voice` deletes the voiceprint, every segment fingerprint, every
[voice group](#voice-grouping) and every similarity and verdict in one go; the provider's labels come back. Your marks stay: they are
statements, not biometrics.

**Choosing the threshold.** `GET /api/v1/voice/segments` lists every segment's similarity and the three
labels, without text. Label a few hundred segments of your own conversations blind, as yours or not,
join them on `segmentId`, and pick the threshold that keeps false "yours" lines rare in each language
you speak. Someone in your household with a voice close to yours may need a higher threshold. The
full procedure is in [docs/specs/your-voice.md](../specs/your-voice.md#how-we-measure-it); your
recordings and labels stay on your side.

## People

Names for the other voices in your conversations, from what was said. After each stored summary, the
server asks the model whether any unnamed voice gave its name: it introduced itself ("I'm Anna",
"мене звати Олена"), or someone addressed it by name in the next line or two. A name that is only
mentioned does not count, and neither does the wearer's own (`Nytka__Memories__UserName`). Each answer
becomes a **suggestion** with the line that shows the name and a confidence of 0.5 to 1; a model
answer below 0.5 is dropped. A suggestion waits for you to accept or reject it, unless the model is sure of it
([auto-applied names](#auto-applied-names)).

- A voice is what the label rule can tell apart: the provider's `speaker_id`, or, for a segment with
  only a `speaker` label, that label within one batch. A segment with neither cannot be named.
- A conversation under 60 words, a repeated summary with no new speech, and a voice that already has
  a name (a person known only by a [role](#roles) is asked for one) or is yours are not asked about. At most one suggestion per voice per run, the most
  confident.
- **Accept** names the voice as `POST /api/v1/people` does, so every segment of that `speaker_id`
  shows the name, in every conversation; a suggestion for a batch's label names that batch's segments
  only, as `PATCH /api/v1/segments/{id}` with `personId` does. A name equal to a person's (any case)
  uses that person. Other pending names for the same voice go. A suggestion for a [voice group](#voice-grouping)
  names the group as a card does, and goes with the group.
- **Accept by name** (`POST /api/v1/people/suggestions/accept-by-name` with `{ "name": "Olena" }`, admin) accepts every pending
  suggestion of that name (any case) for a voice, a label or a voice group, through the same code as accept and in one
  transaction (all or nothing), so one name is one person. Role-only suggestions and `person` targets stay. It answers
  `200 { person, accepted, skipped }`: `skipped` counts a suggestion that can no longer apply (its voice already belongs to
  another person, all its segments have a person, its group is gone), which changes nothing. `404` when no pending
  suggestion has the name, `400` for a missing or blank name, `409` when the pending ones carry different people or the
  name is that of a person known only by a role. `GET /api/v1/people/suggestions` gives each item `sameName`, the number of
  pending suggestions with the same name (its own included), so the app can offer "accept all 16".
- **Reject** keeps the name on record, so it is never suggested again for that voice.
- **Checks.** The server does not trust the model: a name is one to three capitalized words of letters
  (40 characters at most), none a pronoun, answer, interjection, evaluation or generic address
  ("Ти", "Нет", "Прикольно", "Девочка", "girl") unless the line writes it with a capital in mid-sentence,
  and it must occur in the line shown as evidence (any case ending: Діма, Діму, Дімі), which must be at least two words
  long: a name alone as a whole line ("Пока.") is no evidence, and a longer neighbouring line that says it is. The wearer's own
  name (`Nytka__Memories__UserName`, or a name the wearer gave for themselves, "I'm X", "я X") is never
  suggested, nor is a voice that is also the wearer.
- **Limits.** English audio playing nearby is a voice like any other: a name spoken by media can be
  suggested, and you reject it.
- **With `Nytka__Speech__Mode` `on`** ([speech kind](#speech-kind)), a line whose kind is `media` is no voice to
  name, no line that can show a neighbour's name, and is not sent to the model; a `call` line is named as before.
  Voice grouping and the cards skip `media` and `call` lines and wait for a line's guess, and facts and memories
  never read a media line. In `shadow` and `off` nothing here changes (except where your own mark says `media`).

### Auto-applied names

With `Nytka__People__AutoApplyNames` on (the default), a new suggestion whose confidence is at least
`Nytka__People__AutoApplyConfidence` (default 0.85) is accepted as it is made, a self-introduction and being addressed by
name alike, and gets the status `auto_applied` instead of `accepted`. It does exactly what accept does. It stays pending
for you when:

- it is a role alone, or a voice group's;
- the conversation is unsure: another voice of the same run got the same name, or the voice already has a suggestion of
  another name that is pending or applied;
- the name clashes with a person: one known only by a [role](#roles) has it, or, for a person known by role, someone else has
  it (applying would merge the two, which undo could not split);
- the voice already belongs to a person of another name, or every segment of a label already has a person.

`GET /api/v1/people/suggestions?status=auto_applied&limit=20` lists them, newest applied first, with `decidedAt` the time it
was applied. `POST /api/v1/people/suggestions/{id}/undo` (admin) takes one back: the voice it linked and the segments it named
lose the person, a role tag it added goes, a person known by role gets the role name back, and a person the apply made is
deleted when nothing else holds them (no voice, segment, voiceprint or note). Auto-apply never merges or renames a named
person, so nothing was [respelled](../specs/name-spelling.md) and nothing needs respelling back. The suggestion becomes
`rejected`, so the name is never offered or applied for that voice again; `409` when it is not auto-applied. Accept and
reject answer `409` for an auto-applied suggestion, as for any that is no longer pending. Only the `suggest-names` job applies
names; no MCP tool applies one. Suggestions made before this existed stay pending.

### Roles

Someone is often known by what they do before you know their name: the repairman. The same call may
return a **role** for a voice when the transcript ties one to it: the voice says it ("I'm the plumber"), or
another speaker refers to or addresses it so ("the repairman is here", "майстер приїхав", "дякую,
майстре"). The model gives the base form of the word ("майстер"); the server stores what it sent after
normalizing it as a [tag](#tags), with no stemming. The model is told to take nothing from how a voice sounds
and to give no age, gender, health, religion, ethnicity or politics.

- **Checks.** A role is one to three words of letters, none a pronoun, answer or generic address (the name
  stoplist: "ти", "he", "friend", "друже"), is not one the wearer gave for themselves ("I'm the plumber"),
  and occurs in the line shown as evidence or one of the three either side (any case ending). A suggestion
  with neither a valid name nor a valid role is dropped; a name that fails with a valid role leaves the role.
- **A role without a name** is a suggestion shown as "Repairman" (`name`, with `role: "repairman"`
  and `named: false`). **Accept** creates a **new** person, never one found by name (two repairmen are two
  people until you merge them): "Repairman", or "Repairman 2" when that name is taken, with `named: false`
  and the role as a plain [tag](#tags) (`repairman`), and names the voice, label or batch as for any name.
  With a name and a role, accept names the voice as above and the person also gets the role as a tag.
- **A name said later.** The voice of a person known only by role is sent to the model as "Voice A (known
  as: repairman)", and a name it hears for them becomes a suggestion with `target: "person"` and that
  person's `personId`. Accept renames the person (their id, voices, segments and tags stay) and sets
  `named: true`, or, when a person of that name exists, merges the role-only person into them. Renaming by hand
  (`PATCH /api/v1/people/{id}` with `name`) also sets `named: true`. A role-only person is not listed among the
  people already named in the prompt, and gets no role suggestion.
- Nothing matches roles across conversations by text: the same repairman in another conversation is the
  same person once the voice is linked (voice grouping, or by hand).
- Roles are part of the name suggestion: they follow `Nytka__People__SuggestNames`, are rejected the same way
  (a rejected role is never offered again for that voice), and `GET /api/v1/info` lists `roles` under `features`.
  Existing conversations are asked again for roles through `POST /api/v1/people/backfill?force=true` (the name
  checks are now version 3).

A failed run is retried like [memories](#memories): three attempts, then an hour later, three rounds
at most. The text of the conversation goes to your language model endpoint, as for a summary, together
with the names of the people you already have; logs and errors hold neither. Turn it off with
`Nytka__People__SuggestNames=false`; the suggestions already made stay.

### Backfill

Conversations summarized before People existed have no suggestions or facts. `POST /api/v1/people/backfill?limit=`
(admin) queues `suggest-names` and, while `Nytka__People__Facts` is on, `extract-person-facts` for conversations whose summary
is done and that have no finished run of that job, newest first, at most `limit` (default 200, up to 1000). It does not run
the summary again, so titles, summaries, tasks and memories stay as they are. Calling it twice queues nothing twice: a
conversation whose job already waits counts in `skipped`, and `remaining` counts the eligible conversations beyond `limit`,
so call it again until it is `0`. The jobs run in the usual AI lane, behind live summaries, one at a time. With
`Nytka__People__SuggestNames=false` no name job is queued, and without a language model the answer is `409`. Facts the backfill
finds publish `person.fact.created` like any other, so a webhook subscribed to it gets one per backfilled fact.

To read old conversations again after the name checks changed, call `POST /api/v1/people/suggestions/revalidate` (admin; deletes
the pending suggestions that fail the checks and answers `{ checked, removed, kept }`), then `POST /api/v1/people/backfill?force=true`
until `remaining` is `0`: `force` also queues conversations whose name run was made under older checks (version 3: the
two-word evidence line and a longer stoplist). A plain backfill queues
only conversations with no finished run. Accepted and rejected suggestions stay.

## Tags

Short words on conversations and on people (`work`, `family`, `repairman`), only yours: nothing adds a
tag but you. Lists show them, and `?tag=` on conversations, people and [search](#search) keeps the ones that have it.
The MCP tools carry them too: `list_tags`, a `tag` filter on `list_conversations`, `list_people` and `search`, and
`tags` on their items, `get_conversation` and `get_person` ([MCP](../reference/api.md#mcp)).

- A name is normalized: trimmed, one leading `#` dropped, lower case, each run of spaces one `-`. What is
  left is 1 to 32 letters (any script), digits, `-` and `_`, starting with a letter or digit, else `400`.
  `Робота` and `#робота` are one tag; `dog walker` is `dog-walker`; `a/b` is refused.
- At most 20 tags on one conversation or person (`409`). There is no limit on tags in all.
- A tag exists while something holds it: removing its last link, deleting the conversation or person
  that held it, or merging does not leave an empty tag behind. `GET /api/v1/tags` counts from the
  links, so the counts cannot drift.
- Merging two conversations (the same speech arriving late) or two people gives the survivor the tags of
  both. Rename, merge into another tag and delete work on the whole server and need an `admin` token.
- The `conversation.ready` webhook carries `tags`, the conversation's tags when it was summarized. Adding
  a tag sends no event; read `GET /api/v1/conversations/{id}` for the current ones.
- **Proposed tags.** With `Nytka__Tags__Suggest` on (the default) and a [language model](../reference/configuration.md#the-language-model) set, each
  summary may bring up to 3 proposed tags for the conversation, and each run of [facts about people](#facts-about-people)
  up to 3 for each person it lists ("neighbour", "doctor"). A proposal is a name waiting in the
  [review inbox](#review) (kind `tag`) and `GET /api/v1/tags/suggestions`; it tags nothing until you accept it. A
  rejected tag is never proposed again for that conversation or person, and neither is one you accepted and later removed.
  The model does not propose a name of a person in the conversation, an invalid name, or a tag the item has. A conversation
  under 60 words gets none. Accepting is the same add as `PUT`, so it is refused with `409` when the conversation or person
  has 20 tags, and the proposal stays pending. Merging two conversations or two people gives the survivor the proposals of both.
  A person's proposal also names the conversation it came from.
- **Automatic tags.** With `Nytka__Tags__Auto` on (the default) a conversation's proposed tags are applied at once instead
  of waiting, each as the tag it is an alias of (`tag_aliases`), so "ремонт", "ремонти" and "repair" end up as one tag. They
  are recorded as accepted proposals, so a tag you remove is never applied again. Each night after 04:00 local time the
  `tidy-tags` job asks the model, in one call over the tag names and their use counts, which tags mean the same; each group
  is merged into its most used tag and the other names become its aliases. A tag applied automatically that stays on a single
  conversation for 60 days is removed. Tags you added yourself are never removed. Proposals waiting from before are
  applied by the first nightly run. People's proposed tags still wait for you.
  Off, every proposal waits, as before.
- **What the model sees.** With proposals on, the 100 tags in use most (names only) go to the model with each summary and
  each facts request so it reuses them, and the prompts ask for nothing about health, religion, ethnicity, politics or how
  someone sounds (nor age or gender, for a person). The model reads text, never audio. With proposals off no tag name is sent
  and an answer's tags are ignored.
- Tags can be private ("therapy"). No tag name is in a log line or an error message; the server logs no
  request lines (Serilog's `Request starting` and `Request finished`), because they hold the path.

## Voice grouping

Off by default. Your voice only tells you apart from everyone else; this opt-in layer also tells the
others apart, so Nytka can ask "Who is this?" and later suggest the name of someone you already named.
It needs [your voice](#your-voice) enrolled, the speaker model and `Nytka__Audio__RetentionDays` of 1 or
more (with `0` fingerprints die in the transcription transaction, before any grouping runs); then
`GET /api/v1/info` lists `voice-groups` under `features`. The app shows the switch with a sentence on the
legal duty of recording people.

**Grouping.** Every minute, while `Nytka__People__VoiceMatching` is on and a fingerprint waits, the
`group-voices` job takes the fingerprints of segments that are not yours and have no person, in segment
order. One that reaches `Nytka__People__VoiceThreshold` against the voiceprint of a person you confirmed
before joins that person's pending voice match for its conversation. Otherwise it joins the group whose
centroid it reaches the threshold with, which moves as a running mean, or starts a group. No model is
called and no label changes: only you naming a group, or accepting a match, links segments to a person
(the cards below). A group's fingerprints are the ones still held.

**Cards.** `GET /api/v1/people/cards` offers what is waiting for your answer: a group with no person ("Who
is this?") heard in at least `Nytka__People__CardMinConversations` (3) conversations, and a pending match ("Is this Olena?"). A card has one clean stretch of that voice: consecutive
segments in one conversation with no other segment between them, together at least 5 s, with their speech
audio still stored. Its clip is the stretch's first 10 s at most, cut from the stored frames by capture
time (`GET /api/v1/people/cards/{kind}/{id}/clip`, `audio/ogg`), and its `lines` are the segments that
start inside it. A set holds at most 4 cards and at most 2 from one conversation, the newest conversation
first, one card per group or match (its newest stretch); it is empty while voice matching is off. Answer
with `POST /api/v1/people/cards/{kind}/{id}`: `{ personId }` or `{ name }` names a group (a person of that
name, any case, else a new one) as [Confirming](#voice-grouping) says, and confirms a match (the person
must be the one it asks about); `{ skip: true }` hides the card for 7 days; `{ reject: true }` says "not a
person" (the group is deleted and its fingerprints are never grouped again) or "not them" (the match is
kept as rejected and never offered again). A group suggestion from [People](#people) accepts the same way.
A card whose audio has been deleted is not offered and its clip is `404`; a pending match then waits for
an answer without a clip, and confirming it links its segments but adds no sample. The routes need an
admin token and carry no vector.

**Confirming.** Naming a group or accepting a match links its segments to the person, blends the
fingerprints still held into that person's voiceprint (weighted by count, as for yours) and deletes the
group, in one transaction. A person's voiceprint is the one vector of someone else that outlives audio.

**A group never outlives its audio.** Retention deletes the groups its fingerprints leave empty in the
same statement, deleting a conversation does the same, and `DELETE /api/v1/voice` deletes every group
together with your voiceprint. A group that only lost some of its fingerprints keeps its centroid.
`DELETE /api/v1/people/{id}` deletes the person's voiceprint and pending matches with them.

**Forget.** `DELETE /api/v1/people/voiceprints` deletes every group, every person voiceprint and every
pending match. Segment links stay: they are statements, as your marks are. Turning the setting off stops
new work and keeps what is stored until that call.

**Measuring it.** `GET /api/v1/people/voice-eval` lists the fingerprinted segments of other people with
their group, the person they were linked to, a pending match's person and the similarity, without text or
vectors. Label a few hundred of them blind and join on `segmentId`; the procedure and the pass marks are
in [docs/specs/people.md](../specs/people.md#how-we-measure-it). How well the model tells two other
people apart in Ukrainian and Russian is unmeasured until you run it, so the switch starts off.

No voiceprint, group centroid or fingerprint appears in an API answer, export, webhook, MCP answer or
log; `pg_dump` holds them.

## Speech kind

The pendant records everything near it. Some of that is not a person talking to you: a TV, a video, a
podcast, music with vocals, or the far side of a call on speaker. Every line of a transcript can carry a
**speech kind**: `person` (someone near you, you included), `media` (sound from a device) or `call` (the far
side of a call on speaker). `GET /api/v1/info` lists `speech-kind` under `features`.

**Which kind applies.** The first of these: your mark on the line, in every mode; then, only while
`Nytka__Speech__Mode` is `on`, Nytka's guess, with `unsure` read as `person`; then none, and the line is
treated as before. The result is stored on the line, so every reader asks one column. A conversation's
segments carry it as `speechKind`, with `speechGuess`, `speechScore` (0 to 1, how much the line looks like
media), `speechSignals` (what moved the score) and `speechMarked` (true when your mark decides).

**The guess.** A few scheduler ticks after a conversation closes (its transcription done, and some line with a
verdict on whether you spoke it), the `classify-speech` job guesses every line. Your own lines are `person`
with the signal `wearer`. The others are grouped into stretches (consecutive lines with gaps under 4 s, cut at
10 s) and each stretch gets one score from a fixed linear model, `speech_version` 1: how far the stretch lies
from your own lines, how much non-wearer speech surrounds it, your share of the conversation's speech, and
what the audio model YAMNet hears in its first 10 s (`Television`, `Narration, monologue`, `Speech
synthesizer`). The score is the model's logistic, fitted on 133 labelled clips with media and people
weighted equally: it is not a probability of media, and a score of 0.94 means no labelled person scored that
high. `speechSignals` lists what moved it (`far`, `run`, `share`, `tv`, `narr`, `synth`, `phone-media`,
`phone-call`, `partial`). A phone media range on the loudspeaker over half the stretch adds to the score, and
a speaker call range over the stretch with your own line within 3 s makes the guess `call` (see [Context from
the phone](#context-from-the-phone)). A conversation with no line whose wearer is known gets no guess.
`Nytka__Audio__RetentionDays` bounds the audio: older conversations are guessed from structure alone.

**The model file.** YAMNet (3.7 million parameters, 16 MB, ONNX Runtime on the CPU) and its class map come
from `scripts/fetch-audio-tagger.sh`, which the Docker image runs; the files go in `src/Nytka.Audio/Models/`
and are checked by SHA-256. Without them a guess still runs, from structure alone, with the signal
`partial`. Licence: YAMNet is Apache-2.0 (Google; ONNX export by andrelgomes); it was trained on AudioSet,
and no terms for AudioSet-trained weights were found. See [NOTICE](../../NOTICE).

**Guessing again.** `POST /api/v1/speech/backfill` queues `classify-speech` for the conversations with
unguessed lines and answers `{ "queued": 3, "remaining": 0 }` (admin; one call queues at most 500, so call it
again once the jobs ran; nothing is queued while the mode is `off`). With `?force=true` it also takes
conversations guessed by an older `speech_version`.

**Marking.** `PATCH /api/v1/segments/{id}` with `speechKind` (`person`, `media`, `call`, or `null` to
clear your mark) marks one line, alone or with `isUser` and `personId`. Any line takes a mark, yours
included, so a TV voice Nytka took for you can be fixed. `POST /api/v1/conversations/{id}/speech` with
`{ "kind": "media" }` marks every line of a conversation that is not yours, for an evening with the TV on,
and answers `{ "marked": 12 }`; `null` clears those marks. Both need an admin token. A mark survives every
change of the settings.

**Settings.** `Nytka__Speech__Mode` is `shadow` by default: guesses are kept and shown and change
nothing. `off` makes none; `on` lets them set the kind. `Nytka__Speech__MediaThreshold` (0.94, the value no labelled person reached) is the score at
or above which a guess is `media`; a score less than 0.15 below it is `unsure`, a lower one `person`, so a
doubt is never `media`. A change of either queues the `apply-speech` job, which works out the stored guesses
and kinds again from the scores and your marks, with no audio and no model.

**Media conversations.** Each item of `GET /api/v1/conversations` carries `mediaShare`: the share of the
conversation's speech time whose kind is `media`, 0 with none. `media=hide` leaves out the conversations at
0.8 or more, `media=only` keeps just those.

**Noise.** Each item also carries `noise`: true for a closed conversation with fewer than 20 words outside
media lines (a fragment, a cough, a TV left on), the same count under which no summary is made. `noise=hide`
leaves those out, `noise=only` keeps just those; nothing is deleted.

**What media stops feeding.** With `Nytka__Speech__Mode` `on`, a line whose kind is `media` leaves the features
that learn about people. The lines are dropped after they are read, so a conversation whose only new line is
media is not read again and again. A `call` line is named and used for facts like a person's, and only
excluded from voice grouping.

| Feature | What changes for a `media` line |
|---|---|
| [Name suggestions](#people) | it is no voice to name, no evidence for a neighbour's name, and not in the prompt |
| [Voice grouping and cards](#voice-grouping) | it is not grouped or offered (a `call` line neither); a line waits for its guess before it joins a group |
| [Facts about people](#facts-about-people) | it is never a fact's evidence, with no `mentioned` basis either, and not in the prompt |
| [Memories](#memories) | it is not in the prompt |
| Summary, tasks and tags | each run of media lines reaches the model as one `Media: (media playing)` line without its words, so the summary may say media played but lyrics and dialogue never become a summary line, a task or a tag; its words do not count towards the 20 a summary needs, and a conversation that is 0.8 or more media is offered no tags |
| [Digest](#digest) | a conversation that is 0.8 or more media is left out |
| [Ask](#ask) | a media snippet is labelled `Media` in the context |

When the kinds of a conversation change while the mode is `on` (`apply-speech`, or your mark), its summary,
names, facts and memories are queued to run again.

**Measuring it.** `GET /api/v1/speech/eval?since=&until=&limit=` (admin) lists every line with a guess or a
mark, oldest first: `{ segmentId, conversationId, startedAt, durationMs, isUser, guess, score, signals, marked,
kind }`, no text. `nextSince` pages (`limit` defaults to 500, caps at 2000). Your answers to `speech` review items
are marks, so the route joins what Nytka guessed with what you decided; the check before `on` is in
[docs/specs/speech-kind.md](../specs/speech-kind.md#how-we-measure-it).

**Nothing disappears.** A media line stays in transcripts, search, export and MCP. The export's segments
carry `speechKind` and `speechMarked`; MCP's `get_conversation` shows a media line's speaker as `Media` and
a call line's as `<label> (call)`. The design is in [docs/specs/speech-kind.md](../specs/speech-kind.md).

## Review

One list for everything waiting on your answer, so the app needs one screen. `GET /api/v1/review`
merges six queues, newest first:

- **`name`:** a pending [name suggestion](#people), shown with the line that carries the name; it may carry a [role](#roles).
- **`voice`:** a pending [voice match](#voice-grouping) ("Is this Olena?"), shown with its first three
  lines and its similarity, with no clip. None while voice matching is off.
- **`label`:** a segment of the last 14 days that Nytka scored within 0.05 of `Nytka__Voice__UserThreshold`
  and you have not marked, at most 20; the proposal is Nytka's verdict (`isUser`) and the similarity.
- **`tag`:** a pending [proposed tag](#tags) for a conversation or a person, shown with the summary of the conversation it came from; for a person, `proposal.personId` is set (`GET /api/v1/tags/suggestions` adds `personName`).
- **`speech`:** a [stretch](#speech-kind) (consecutive lines that are not yours, gaps under 4 s, cut at 10 s) of a
  conversation of the last 14 days that you have not marked and Nytka guessed `media`, `call` or `unsure`,
  nearest `Nytka__Speech__MediaThreshold` first, at most 3 for each day of your time zone. `id` is the stretch's
  lowest segment id, `text` its lines joined, and the proposal is `speechKind` (the guess) and `lines`:
  `[{ segmentId, startedAt, text }]`. Accept marks every line of the stretch with the guess (`media` for `unsure`),
  reject marks them `person`, both `204`; `409` when a line is already marked.
- **`task`:** a summary's task that may be an open task again ([duplicate tasks](#duplicate-tasks)). `text` is the new task,
  the conversation is its own, and `proposal.task` is the open one: `{ id, text, conversationId, conversationTitle }`, the
  conversation fields `null` for a task made by hand. Accept deletes the new task and links its conversation to the open one
  (`200` with the kept task, as `GET /api/v1/tasks` shows it); reject keeps both (`204`); `409` once it was answered or either
  task is done or deleted. The item leaves the list as soon as either task is done or deleted.

An item is `{ kind, id, conversationId, conversationTitle, at, text, proposal: { name, personId,
confidence, similarity, isUser, tag, role, named, speechKind, lines, task }, clip, context }`; fields that do not belong to the kind are `null`. `id` is a
segment id for a label and a speech item and a guid for the others. `limit` is 1 to 200, default 50.

**Hearing an item.** Every item that rests on speech has the lines it rests on, its evidence: a name's line that says the
name, a voice match's first line, a label's segment, a speech item's stretch. `context` is up to 3 lines before and after the
evidence and the evidence itself, oldest first: `{ segmentId, speaker, personName, isUser, text, startedAt, evidence }`,
`speaker` being the provider's label and `personName` the person the line belongs to. `clip` is `{ url, startMs, endMs }`:
`GET /api/v1/review/{kind}/{id}/clip` (read token) answers the evidence with 3 s either side, at most 20 s, as Ogg Opus cut by
capture time, and `startMs` and `endMs` are the capture times it covers in Unix milliseconds. `clip` is null, and the route
`404`, once retention has deleted the audio (`Nytka__Audio__RetentionDays`, 14 days by default). A `tag` or `task` item has no
clip and an empty `context`. Name suggestions listed by `GET /api/v1/people/suggestions` carry the same `clip`, whatever their status.

`POST /api/v1/review/{kind}/{id}/accept` and `/reject` answer an item. A `name` or `voice` item is
answered as its own routes do (`200` with the person on accept, `204` on reject; `409` when a
suggestion is no longer pending), and a `tag` as `POST /api/v1/tags/suggestions/{id}/accept` does (`200` with the
item's tags; `409` when it is no longer pending or the item has 20 tags), so nothing changes a label or a tag until you accept. For a `label`, accept
stores Nytka's verdict as your mark (`PATCH /api/v1/segments/{id}` with `isUser`) and reject stores the
opposite, both `204`; either way the segment leaves the list, and a mark of yours may teach your
voiceprint as that route does. `404` for an unknown kind or item. The list needs a read token, the
answers an admin one; no answer carries a vector.

## Tasks

A task is something the wearer committed to do, taken from a summary, or added by hand with `POST /api/v1/tasks`
(`{ text, due?, conversationId?, personId? }`; a hand-made task needs no conversation, so its `conversationId`,
`conversationTitle` and `conversationStartedAt` are `null`). A task may have a due date, `due`: ISO 8601 text such as
`2026-10-24T15:00:00Z`, where a bare date means that day at 00:00 UTC, and `null` when nobody gave a deadline. A summary sets
it only when the speaker named a date for that task, and the server drops one that falls before the conversation or more than
366 days after it; it never makes a deadline up. Tick, reopen, edit or delete a task in the Tasks tab, or with
`PATCH` and `DELETE` on `/api/v1/tasks/{id}` (`PATCH` sets or clears `due`). A later summary never adds a deleted task again with the same wording, and
never removes or rewrites one you touched. `GET /api/v1/tasks` lists open or done tasks, newest
first, with the title and day of their conversation.

### Duplicate tasks

A summary's new task is compared with every open (not done, not deleted) task of other conversations and every open task
made by hand, by its fingerprint: the text lower-cased, letters and digits only. A done task never absorbs a new one.

- **Sure:** the same fingerprint, the same kind, no two different people and no two different due dates (a task with no
  person or no due date matches either). The task is not created again: a task's `conversation_id` cannot name a second
  conversation, so the conversation is linked to the open task in `task_duplicates` (`conversation_id`, `task_id`,
  `created_at`, once per pair), and no `task.created` is raised. An open task with no due date takes the new one's; one it
  has is never changed. A conversation stays joined: once its task is done, a later summary of that conversation does not add
  it again.
- **Unsure:** the same fingerprint with another kind, person or due date, or, with the same kind and no two different people,
  texts that share at least 60% of their distinct words, filler words such as "the", "to", "на" or "для" left out, when each
  has 3 such words or more ("Send Ben the quarterly report" and "Send the quarterly report to Ben"). The task is created, and the pair waits in the [review inbox](#review) as a `task` item; the closest open task is
  proposed. Accepting deletes the new task without an event (no task deletion raises one); a pair whose task is done or
  deleted stays in the table but leaves the inbox and answers `409`.
- Anything else is a new task.

Matching is by words, not by the language model: nothing else in Nytka asks the model whether two things are the same, and the
open tasks are few. Word overlap does not see Ukrainian case endings or synonyms, so such a pair stays two tasks unless you
merge them by hand. `task_duplicates` has no API.

`POST /api/v1/tasks` without a conversation answers `409` when an open task made by hand already has the same fingerprint, so
a retried request adds nothing; once that task is done the same text can be added again, and reopening the old one is then
`409` too. Copies made before this rule stay as they were: the oldest counts, the others are left out of it.

A task can name the person it is owed to or who asked for it (`personId`, `personName`). A summary
sets it when it is created, only to a person whose voice you named in that conversation (the wearer
never counts), matching the name ignoring case; any other name leaves it empty. A later summary never
changes it. Set, change or clear it with `PATCH` and `personId` (`null` clears it; an unknown person
is `404`); that counts as touching the task. Deleting the person clears it. What other people owe
the wearer is not a task: it is [waiting on](#waiting-on).

**Kinds.** The summary labels each candidate it finds, in the same model call, with a kind and an owner
([docs/specs/task-kinds.md](../specs/task-kinds.md)):

| Kind | What it is | What Nytka keeps |
|---|---|---|
| `commitment` | the wearer said they will do it, or was asked and did not turn it down | a task |
| `idea` | floated, and nobody took it on | a task row of kind `idea`, hidden from the default list |
| `advice` | a tip or lesson, such as a coach's | a [note](#notes) per conversation and topic |
| `noise` | a remark, a vague or garbled line, anything from media | nothing; the candidate goes to an audit table |

The owner is `wearer` or `other`. An item owned by someone else is not your task: its commitment is kept as
[waiting on](#waiting-on) and anything else of theirs is dropped. `GET /api/v1/tasks` and the MCP `list_tasks`
return commitments unless `kind` is `idea`, `waiting_on` or `all`, and a task carries its `kind`. A later summary may relabel an untouched task: a commitment that turns out to be
advice or noise is removed, and an idea that becomes a commitment is announced as `task.created`. The audit table
`dropped_candidates` (`conversation_id`, `kind`, `owner`, `text`, `created_at`, replaced on every summary) holds what was
dropped, so you can read the noise rate with SQL; it has no API.

Tasks that exist from before this version stay commitments until their conversation is summarized again
(`POST /api/v1/conversations/{id}/enrich`); that run removes the ones the model now calls noise or advice, except tasks
you ticked, edited or deleted.

## Waiting on

What someone else promised you ("I'll send you the file on Friday") is kept as a task of kind `waiting_on`: the
summary labels it a `commitment` with owner `other`. It links to the person who owes it (`personId`, `personName`) when
that person's voice is named in the conversation, as for any task. It is hidden from the default task list and
announced by no webhook: `GET /api/v1/tasks?kind=waiting_on` and the MCP `list_tasks` with `kind` `waiting_on` list them,
`kind=all` includes them, and the [person page](#person-page) shows the person's open ones as `waitingOn`.
Tick one off, edit it or delete it with the task endpoints, like any task. A conversation keeps up to 10, the next
summary replaces the ones you did not touch, and another person's idea, advice or remark is still only audited.
`GET /api/v1/info` lists `waiting-on` under `features`. Tasks from before this version appear after their conversation
is summarized again (`POST /api/v1/conversations/{id}/enrich`).

## Promise ledger

`GET /api/v1/people/{id}/ledger` (MCP `get_ledger`) answers what is open and done between you and one person: `owedByMe` are your
commitments linked to them, `owedToMe` the [waiting on](#waiting-on) items they owe you. Each list has `open` (newest first) and `done`
(most recently done first), up to 100 each, and each entry is a [task](#tasks) with the conversation and the time it was said
(`conversationId`, `conversationTitle`, `conversationStartedAt`). It reads existing rows and changes none; the export already carries them.
A promise that names no known person is not in anyone's ledger. `GET /api/v1/info` lists `ledger` under `features`.
Spec: `docs/specs/promise-ledger.md`.

## Notes

A note is the advice of one conversation on one topic: the tips of a coaching session about table tennis are the
points of one note, `table-tennis`, not nine tasks. A topic is a tag-like name of up to 32 characters; advice with no
topic goes to `general`. A conversation has up to five notes of up to ten points each, and the next summary of the
conversation replaces them. `GET /api/v1/notes` and the MCP `list_notes` list them; deleting the conversation deletes its
notes.

## Paid transcript pass

Every batch gets a draft from the engine the server is set to. A **pass** sends a conversation's stored speech to a paid engine once and swaps its text for the draft lines (spec: [Auto transcription](../specs/auto-transcription.md), steps 5 of 11; Auto itself, which queues passes by itself, comes later). Today a pass starts with `POST /api/v1/conversations/{id}/upgrade`.

- **Engine.** Soniox with `Nytka__Stt__SonioxApiKey`; without it the route answers `409` and nothing is upgradable (the engine behind `stt.url` is the draft engine, and a second paid engine that differs from `stt.liveModel` comes with Auto). Soniox is polled every 5 seconds across job runs, so a slow answer never blocks the Audio lane. The file and the transcription at Soniox are deleted after every outcome.
- **What is sent.** The stored speech of the finished batches no pass covered, cut into stretches of continuous capture time and joined with 300 ms of silence, at most 30 minutes per pass. A longer conversation becomes `mixed`; ask again for the rest. Times of the paid lines go through the pass's offset map, so they are capture times.
- **The swap** is one transaction with the ledger row (`pass_id`, no batch) and the `transcript.upgraded` event: a crash leaves the whole draft or the whole swap. A draft line is replaced when the paid lines cover at least half of its length, and is then kept in `segment_drafts` until its speech audio expires; a draft line they barely touch stays as it is. The voice group of a replaced line's fingerprint moves to the new line's fingerprint (already counted, so the grouping job does not add it again); a group left with no fingerprint is deleted. A paid line takes `is_user_manual`, `speech_manual` and `person_id` when the draft lines it shares at least half a line with agree, and a speaker label takes the `speaker_id` that 60% of its draft time carries. Name suggestions, person facts and voice matches move to the new lines (a rejected name stays rejected); the summary, tasks and memories run again.
- **Money.** A pass does not start when the month's spend is at the budget or its estimate would pass it (`409` from the route; a queued pass waits as `waiting`).
- **Tier.** `final` when every finished batch that still has speech audio is covered and none is pending; `mixed` otherwise. A batch without audio, or a failed one, does not hold it at `mixed`. The route answers `409` with `The speech audio is gone.` or `The draft is still being transcribed.` when nothing is left to send.
- **Cleanup.** The file and transcription at Soniox are deleted after every outcome, including a conversation deleted or merged while its pass is out (the pass row stays until its job has done that and written the ledger row). A file stored without its transcription id is deleted before the pass goes out again.
- **State.** `GET /api/v1/conversations/{id}` has `transcript.tier` (`draft`, `final`, `mixed`) and the latest pass; a pass that fails keeps the draft and can be asked for again.

## Summary templates

A summary can follow a template by the kind of conversation: `meeting` (decisions, owners, open questions), `lecture`
(topic, main points, terms), `doctor` (complaint, findings, advice, what was agreed) and `call` (who, what about, what was
agreed), plus `default`, which is the wording the server has always used for a summary. A template is one line of up
to 600 characters that says what the summary holds; it replaces only that part of the prompt. The title, the tasks, the
notes, the proposed tags, the output language (`llm.outputLanguage`) and the rule that a deadline is never invented stay the
same in every template, and the summary is cut to 1200 characters whatever the template says.

Until you turn it on, nothing changes: every summary follows the `default` template. Editing the default template changes
them all, and takes effect on the next summary. With `Nytka__Summary__Templates__Enabled` on, the template is chosen so:

1. A [tag](#tags) of the conversation named `meeting`, `lecture`, `doctor` or `call` wins (the first of them in that order).
2. Else the model names the kind of the conversation in the same request that writes the summary, and the template of that
   kind applies. A kind that is none of the four (`other`, or anything else) uses `default`.
3. A conversation too short for tasks (under 60 words) always gets a one-sentence summary and no template.

`GET /api/v1/conversations/{id}` says which template wrote the summary in `summaryTemplate` (`null` for a summary made before
this existed, or a brief one). `GET`, `PUT` and `DELETE` on `/api/v1/summary-templates` list, edit and reset the texts; they
are the `Nytka__Summary__Templates__*` settings, so an edit applies to the next job without a restart, and a text set by
the environment cannot be changed there. A text in the environment that breaks the rules (over 600 characters, a line break)
is ignored for the built-in one. A template reaches the model like the rest of the summary prompt, so the same
[privacy](../reference/configuration.md#the-language-model) applies. Re-run a summary with
`POST /api/v1/conversations/{id}/enrich` after changing a template or tag.

## Memories

A memory is a lasting fact about you: who you are, family, friends, home, work, health, habits,
preferences, goals, commitments. It is not a one-off event, a task or another person's affairs. After
each stored summary the server asks the model for up to five new memories of up to 300 characters, in
`llm.outputLanguage`. It sends the title, the transcript, your 200 newest memories and
`Nytka__Memories__UserName`, so facts about other speakers stay out. Without a name, "you" is the
person wearing the pendant.

- Some facts are true only for a while: where you are staying, an illness, a plan with a date. The model may give such a
  fact the last date it holds (`expiresAt` is the start of the next day in `Nytka__User__TimeZone`); from then on
  `GET /api/v1/memories` and the MCP `list_memories` leave it out unless asked with `expired=true`. Search still finds it.
  A date before the conversation or more than two years after it is dropped as a guess.
- Each memory, task and note made by the model carries `salience`: `vital` (shapes your life or your day), `ordinary`,
  or `noise` (trivia, kept but never shown first); null for items made by hand or before this field.
- A fact the server already holds is not added again with the same wording, even one you deleted. Extraction never rewrites
  a memory you edited or added yourself.
- Add, edit and delete memories in the Memories tab, or with `POST`, `PATCH` and `DELETE` on
  `/api/v1/memories`. Adding text you deleted earlier brings the memory back; text a live memory
  already holds is a `409`. Deleting a conversation deletes the memories taken from it.
- `Nytka__Memories__Enabled=false`, or no model, means no extraction. The memories you have stay.
- A failed extraction is tried three times, then again an hour later, three rounds at most.
  Conversations summarized before memory extraction shipped (release 0.4.0) have no memories;
  regenerate a summary to feed one in.

## Person page

`GET /api/v1/people/{id}` is one place for what Nytka holds about a person: `name`, your `note`, when
they were last heard (`lastSeenAt`, the newest segment the [label rule](../reference/configuration.md#transcription-endpoints) gives
them; your own lines never count, and it is null when they were never heard), their `voices`, the newest
10 conversations they spoke in (`{ id, title, startedAt }`), their newest 50 [facts](#facts-about-people)
the open tasks owed to them and, as `waitingOn`, the [open promises](#waiting-on) they made to you (newest 100 each). `hasVoiceprint` and `voiceprintSamples` say whether a
[voiceprint](#voice-grouping) exists and how many segments it was made from; the voiceprint itself
never leaves the database. `404` for an unknown person. The `list_people` and `get_person` [MCP tools](../reference/api.md#mcp)
read the same page, without the two voiceprint fields. `named` is `false` for a person known so far only by a [role](#roles).

## Facts about people

A fact is a short, lasting thing about one person you named: where they live, their work, family,
habits, preferences, commitments. After each stored summary the server asks the model for up to ten
facts of up to 300 characters, in `llm.outputLanguage`, when the conversation is not brief and has
someone to write about: a speaker you confirmed as a person (by naming their voice or marking a line,
see [Speaker labels](../reference/configuration.md#transcription-endpoints)), or a line that names a person you know, as a whole word
in any case. It sends the title, the transcript with a segment id on each line, those people with their
30 newest facts, and `Nytka__Memories__UserName`. Your own lines (`Wearer`) are never a person's; facts
about you stay memories.

- **Each fact names its evidence line and a basis, both set by the server.** `said`: the person's own line.
  `about`: your line or another confirmed person's. `mentioned`: a line of a speaker nobody confirmed that
  names the person. A fact whose evidence line is none of these is dropped, whatever the model said.
- A fact the person already holds is not added again with the same wording, even one you deleted.
  Extraction never rewrites a fact you edited or added. Deleting a conversation deletes the facts taken
  from it; deleting a person deletes theirs; merging two people moves them, and the target's row wins a
  duplicate.
- List, add, edit and delete facts with `/api/v1/people/{id}/facts`. A fact you add has `source: user`
  and no basis; adding text you deleted earlier brings it back, and text a live fact holds is a `409`.
- The same answer may propose up to 3 [tags](#tags) for each listed person; they wait in the [review inbox](#review) and tag
  nobody until you accept them. `Nytka__Tags__Suggest=false` stops them.
- `Nytka__People__Facts=false`, or no model, means no extraction. The facts you have stay.
- A failed extraction is tried three times, then again an hour later, three rounds at most.
- Facts are in [search](#search) but not in Ask or the daily digest, and no webhook payload carries a transcript.

## Export

`GET /api/v1/export` (admin token) streams everything Nytka holds for you as NDJSON, one JSON object
per line, so a long history never sits in memory on either side:

```bash
curl -sS -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" "$NYTKA_URL/api/v1/export" -o nytka-export.ndjson
```

Every line has a `type` first. The order is `header` (`format: "nytka-export"`, `version`, `generatedAt`,
`serverVersion`), `setting` (`key`, `value`), `person` (`id`, `name`, `note`, `voiceprint` as true or false, `voices`, `tags`, `named`, `createdAt`),
`conversation` (`id`, `source`, `externalId`, `startedAt`, `endedAt`, `status`, `title`, `titleEdited`,
`summary`, `tags`, and `segments`: `{ startedAt, endedAt, text, speaker, speakerId, isUser, person, speechKind, speechMarked }`), `task`
(`id`, `conversationId`, `personId`, `text`, `done`, `doneAt`, `createdAt`, `updatedAt`, `kind`), `memory` (`id`, `text`,
`source`, `conversationId`, `createdAt`, `updatedAt`), `person_fact` (`id`, `personId`, `text`, `source`,
`basis`, `conversationId`, `edited`, `createdAt`, `updatedAt`), `bookmark` (`id`, `at`, `note`, `source`,
`createdAt`), `digest` (`id`, `localDate`, `headline`, `overview`, `highlights`, `decisions`,
`openQuestions`, `createdAt`) and `end` (`counts` per type). A file without its `end` line was cut
short. Times are UTC, ids are stable between exports.

- Deleted tasks, memories and person facts are not in it. Voice groups, voiceprints (only `voiceprint:
  true|false` is), name suggestions, voice matches, calendar events, briefs and proposed tags are not either; `tags` holds only the tags you set or accepted. API keys, tokens, webhooks and their secrets, and every
  URL setting are left out; the `setting` lines are the non-secret settings in effect.
- No audio: fetch `GET /api/v1/conversations/{id}/audio` for the conversations you want.
- `POST /api/v1/import/nytka` reads the file back, to restore a server or move to another one; see
  [Restore a Nytka export](../how-to/install.md#restore-a-nytka-export). Every field is specified in
  [docs/specs/export.md](../specs/export.md).

`GET /api/v1/export/obsidian` (admin token) answers a ZIP of Markdown notes for an Obsidian vault:

```bash
curl -sS -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" "$NYTKA_URL/api/v1/export/obsidian" -o nytka-obsidian.zip
```

One note per conversation under `Conversations/` (front matter with `date`, `tags`, `people`; a
heading with the title; the summary; tasks as `- [ ]`/`- [x]` checkboxes; the transcript as
`**HH:mm:ss** <speaker>: <text>` lines, UTC) and one per person under `People/` (front matter with
`tags`; their facts; their conversations as `[[wiki links]]`, newest first). File names are safe on
Windows, macOS and Linux: forbidden characters become spaces, a trailing dot or space is dropped, a
reserved device name is prefixed with `_`, and 8 hex characters of the row's id keep every name
unique. Field by field: [docs/specs/export.md](../specs/export.md#obsidian-export).

## Bookmarks

A bookmark marks a moment: a single tap on the pendant, or a tap in the app, sends `POST
/api/v1/bookmarks` with the time of the tap. The client makes the `id`, so an upload it retries changes
nothing. A bookmark holds no conversation: a conversation lists the bookmarks made from 30 seconds
before it starts to 30 seconds after it ends, so bookmarks follow merges, and one outside every
conversation stays in `GET /api/v1/bookmarks` alone (`conversationId` is then null). Deleting a
conversation keeps its bookmarks. The `bookmark.created` webhook and the `list_bookmarks` MCP tool
carry the same fields.

## Context from the phone

The pendant also hears what your phone plays: a video, a podcast, the far side of a call on speaker. The app can
tell the server when that happened, as a **range** for each stretch in which the phone played sound through its own
loudspeaker (`kind` `media`) or was in a call (`call`). A range the app sends holds the `kind`, the `route` of the sound (`speaker`,
`earpiece`, `headset`, `bluetooth` or `other`) and two times, and nothing else: never an app name, a title or a number.
Android does not tell the app which app plays, and the app does not ask.

The app sends ranges to `POST /api/v1/context/ranges` in batches of up to 500, each with an id it made, so a batch it
retries changes nothing. It sends them while `/api/v1/info` lists `context-ranges` and its "Phone context" switch is on.
`GET /api/v1/context/ranges` lists them. Nothing else reads them yet; they are the context for telling a person near you
from a voice that comes out of a screen or a loudspeaker. The server keeps a range as long as speech audio
(`Nytka__Audio__RetentionDays`; with `0`, a day after it ends) and logs counts only, never a time or an id.

## Daily digest

Once a day the server writes a short account of your day: a headline, an overview of two to four
sentences, highlights (each tied to the conversation it comes from), decisions and open questions.
It is off by default. Set `Nytka__Digest__Enabled=true` and it makes the digest of each local day
after `Nytka__Digest__Hour` (21 by default), in `Nytka__User__TimeZone` and in `llm.outputLanguage`. It
needs the language model.

- **What goes to the model.** One request with that day's summarized conversations (title, local time,
  summary), the tasks and the memories created that day, and no transcript. A day with no summarized
  conversation makes no digest and no request. A busy day sends its newest 80 conversations and says
  so in the prompt. A conversation summarized after the digest was made is not added; the digest of
  a date is made once. The server also makes yesterday's digest if it is missing (after an outage or
  failed runs), and never one for a day without a summarized conversation.
- **Read it.** `GET /api/v1/digests` lists them, newest date first, and `GET /api/v1/digests/{id}`
  returns one. The `digest.ready` webhook carries `{ id, localDate, headline, overview }`, enough for an
  ntfy or n8n message, and the `list_digests` MCP tool returns the list. A highlight whose conversation
  you deleted later keeps its id; treat it as plain text.
- **Make or redo one.** `POST /api/v1/digests/run?date=2026-09-29` (admin) queues a run for that local
  date, today or earlier, that replaces the date's digest with a new one (and sends `digest.ready`
  again). It answers `202 { localDate }`, `400` for a missing, malformed or future date and `409`
  without a model. It works while `Nytka__Digest__Enabled` is `false`.
- **When it fails.** A run makes three attempts; after the third it is tried again an hour later while
  the date is today or yesterday. Logs and errors say only what failed, never the digest or its input.

## Calendar briefs

Before a meeting with someone you named, the server writes a short brief: who they are to you, what to
remember and what is still open. It needs the language model and a calendar feed: set
`Nytka__Calendar__IcsUrl` to the secret ICS address your calendar offers (read-only; Google, Outlook,
Nextcloud and most others have one). Nothing is fetched while it is empty.

- **The feed.** Every 15 minutes the server fetches it (`http` or `https`, no redirect, a 3xx fails the
  fetch, 10 seconds for the whole read, at most 5 MB) and stores the occurrences that run after now and start
  within 48 hours, recurring events expanded in their own time zone (an event with no zone is read in
  `Nytka__User__TimeZone`). All-day and cancelled events are left out. The address may be a private one, as for
  webhooks: only you set it. An event keeps its title and its attendees' display names (`CN`), not their
  addresses or anything else. A fetch that fails keeps what is stored, and the log says only that it failed and why in
  a fixed sentence (such as "HTTP 302"), never the address or the feed. An event goes a day after it ended.
- **Who counts.** An attendee is a person you named when the display name equals the person's name, ignoring
  case. Only events with such a person get a brief, once the event starts within `Nytka__Calendar__BriefMinutes`
  (30 by default).
- **What goes to the model.** One request per brief with the event's title and start, and for each matched
  person their name, your note, up to 20 facts, up to 10 open tasks owed to them and the title and summary of
  their last 5 conversations, never a transcript. Other attendees' names are not sent. The calendar address is
  not sent. A failed run is tried three times and again ten minutes later until the meeting starts.
- **Read it.** `GET /api/v1/briefs/upcoming?minutes=` lists the events not over yet that start within
  `minutes` (1 to 1440, 60 by default, clamped), each with its attendees and the matched person's id and its
  `brief` (`{ id, text, createdAt }`), or null before it is made or when nobody matches. The `brief.ready` webhook
  carries `{ id, title, startsAt, people: [{ id, name }], text }`. A brief goes with its event, and with a
  person you delete.

## Calendar feed

`GET /api/v1/calendar.ics` serves an RFC 5545 calendar back out: the events of your [calendar
feed](#calendar-briefs) that have not ended yet, each as a `VEVENT`, soonest first, and your open
[tasks](#tasks) that carry a due date, each as a `VTODO` with `DUE`. The
calendar is at most as fresh as the feed's own 15-minute fetch. Content type is `text/calendar`; every line
is CRLF-terminated and folded at 75 octets per [RFC 5545 §3.1](https://www.rfc-editor.org/rfc/rfc5545), and
every `SUMMARY` is escaped for a literal backslash, comma, semicolon and newline. Each item carries a `UID`
(stable across fetches, one occurrence of a recurring event included) and a `DTSTAMP` of the moment the feed
was built.

**The query-token risk.** A calendar app cannot set an `Authorization` header, so this one route also accepts
the token as `?token=` in the subscription URL you give it. Only a `read` token works there (an `admin`
one, the environment's included, answers `401`); make one for this feed alone, so you can revoke it
without touching anything else. The token then lives in the calendar app's
own storage and sync, and in the access logs of anything between it and your server (a reverse proxy, your
calendar provider's own servers if it polls the URL on your behalf); treat the URL itself as a secret, the same
as the ICS address in `Nytka__Calendar__IcsUrl`. The response carries `Cache-Control: no-store` and
`Referrer-Policy: no-referrer`, so a cache does not keep the token-bearing URL and a browser never sends it
onward in a `Referer` header.

## Search

`GET /api/v1/search?q=` and the `search` MCP tool search transcripts, titles, summaries, memories, and
people by name or by [fact](#facts-about-people), in Ukrainian and English together.

- Every word of `q` (the first eight, made of letters and digits) must match, exactly or as a prefix,
  in the same place: one transcript segment, the title and summary together, or one memory. `зустріч`
  finds "зустрічами". English words are stemmed, so `running` finds "run", and common ones such as
  "the" are ignored.
- A hit is one conversation, one memory or one person, best first: a title or a person's name counts
  more than a summary, a memory or a fact, and those count more than the transcript. A person's hit
  has `kind: person`, their id, the name as `title`, and a `snippet` of the name or of the best
  matching fact. Deleted items and deleted facts never show. The `snippet` is HTML-escaped,
  and the matches sit in `<mark>` tags, the only tag it holds.
- `tag` (a [tag](#tags) name, normalized as there) keeps only conversations and people that have it and leaves memories out, so
  `kinds=memory` with a `tag` finds nothing. The words of `q` are never matched against tag names: searching by tag is
  the filter, and `q` stays required. An invalid tag is a `400`.
- `kinds` picks any of `conversation`, `memory` and `person` (all three by default). `limit` defaults to 20 and caps at 50; `offset` runs
  from 0 to 500 (a larger one counts as 500), and `nextOffset` is null on the last page and when the
  next page would start past 500. No word, or an unknown kind, is a `400`.
- New text, a new or renamed person and a new or edited fact are searchable a few seconds after they are
  written. After the upgrade that adds search or people in search, or when
  you change the dictionary, the server indexes the archive in the background, so older rows appear
  over the next minutes; a row not yet indexed is simply not found.

### Ukrainian search (optional)

By default (`Nytka__Search__Dictionary=simple`) a Cyrillic word matches exactly or by prefix, so
`зустріч` finds "зустрічами" but `рік` does not find "року". The opt-in Ukrainian Hunspell dictionary
maps word forms to their base forms, so `рік` finds "року" and "роки". Words it does not know, such
as names and slang, still match exactly or by prefix, and it does not link every form: `друг` does
not find "друзі".

1. Fetch the dictionary before the first `docker compose up`, in the folder that holds
   `docker-compose.yml`. (On Linux, Docker creates a missing `tsearch_data` as root, and the script
   then needs `sudo`.)

   ```bash
   curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/scripts/fetch-uk-dictionary.sh
   bash fetch-uk-dictionary.sh --accept-licence ./tsearch_data
   ```

   The script prints the licence caveat below and refuses to run without `--accept-licence`. It
   downloads a pinned release of [dict_uk](https://github.com/brown-uk/dict_uk), checks its SHA-256
   and that the files are UTF-8, and writes `uk_ua.dict` and `uk_ua.affix` (upstream's `uk_UA.dic` and
   `uk_UA.aff`, renamed as Postgres wants them) and upstream's licence notice into the folder. From a
   checkout of this repository, `scripts/fetch-uk-dictionary.sh --accept-licence` fills `./tsearch_data`
   without an argument. An existing pair is left alone unless you add `--force`. It needs `curl`,
   `unzip`, `iconv` and `sha256sum` or `shasum`.

2. Compose mounts `./tsearch_data` read-only into the Postgres container, and a small entrypoint
   links the two files into Postgres's `tsearch_data` when they exist. Missing files never stop
   Postgres. Files fetched later need `docker compose restart postgres`.

3. Set `Nytka__Search__Dictionary=uk_hunspell` in `.env` and run `docker compose up -d`, or set
   `search.dictionary` in the app. The server loads the dictionary in the background and re-indexes:
   rows are found again a few seconds later, or minutes on a large archive.

Check that it loads:

```bash
docker compose exec postgres psql -U nytka -d nytka -c "select ts_lexize('nytka_uk', 'зустрічами');"
```

The answer includes `зустріч`. If Postgres says the dictionary does not exist, the server has not
switched to it: the setting is still `simple`, or the files did not load. `docker compose logs server`
says which.

**Fallback.** Migration 0007 applies with or without the files. Asking for `uk_hunspell` without them
logs a warning, in the server's log and in Postgres's, and search stays on `simple`. Set `simple`
again, or remove the files, and it falls back the same way. Writes never break: only the indexer and
the search queries use the dictionary. The server runs the SQL function `nytka_search_setup(<setting>)`
at every start, whenever the setting changes and when indexing fails; a search that finds the files
gone switches to `simple`. You can call it by hand to debug: `select nytka_search_setup('uk_hunspell');`
answers `uk` when the dictionary loaded and `simple`, with a warning, when it did not. It switches the
configuration as the server does, and when that changes the mapping it clears the search vectors for
the indexer to rebuild. With no argument it means `simple`. The server sets it back to the setting's
value at its next start.

**Your own Postgres.** Copy `uk_ua.dict` and `uk_ua.affix` into the `tsearch_data` folder under
`pg_config --sharedir`, readable by the Postgres user, then set the setting; the server checks again
when it changes. Cyrillic case folding depends on the database's `LC_CTYPE`: the bundled
`postgres:17-alpine` is the tested setup.

**Cost.** Postgres reads the dictionary once per session, on first use: about 2.6 s and 65 MB each on
`postgres:17-alpine`. Search uses two sessions of its own (the indexer and the queries) and no other
connection touches the dictionary, so the price is up to about 130 MB. With the dictionary on, a
search whose words match 5,000 of 100,000 segments took 132 ms.

**Licence caveat.** The dictionary's Hunspell package says GPL 3+, LGPL 2.1+ and MPL 1.1, while the
project README puts the dictionary data under CC BY-NC-SA 4.0, which is non-commercial and
share-alike, and does not say how the two fit. Nytka never ships the files: you fetch them onto your
machine and accept that with the flag. Personal self-hosting is fine either way; ask the upstream
authors before commercial use or redistribution. This is a reading, not legal advice. See
[NOTICE](../../NOTICE).

## Ask

`POST /api/v1/ask { "question": "..." }` and the `ask` MCP tool answer a question from your
conversations and memories, with numbered sources. A `read` token may call it: it changes nothing. It
needs the [language model](../reference/configuration.md#the-language-model) (`503` without one; `504` when the model does not
answer within `llm.timeoutSeconds`; `502` when it fails otherwise).

```json
{ "answer": "You planned to leave on Friday [1].",
  "sources": [{ "n": 1, "kind": "conversation", "id": "...", "conversationId": "...",
                "title": "Weekend trip", "at": "2026-09-29T08:00:00Z", "snippet": "..." }] }
```

The server makes two model calls. The first turns the question into up to three short keyword
queries and, when the question names a period, a range of local days (in `user.timeZone`). The queries
run through the search (any word of a query may match), limited to that range; a question about a
period only takes the conversations of those days, newest first. At most 8 conversations and 10
memories go to the second call, best first. A conversation brings its title, summary, tasks and the
lines of its transcript around the best-matching one, cut so that everything fits
`llm.maxInputChars`. The model answers with `[n]` markers; the server drops a marker that names no
source and returns only the sources it cites, renumbered from 1 in order of first mention. A source
is a `conversation` (`conversationId` is its own id) or a `memory` (`conversationId` is the
conversation it came from, or null); `at` is the conversation's start or the memory's last change.

The question, the sources sent to the model and the answer are not stored and not logged; failures
carry fixed sentences. Both calls go to the model you configured, so what your history holds leaves
the server the way summaries do.
