# Nytka, your voice: segments are labelled as yours with any transcription provider

The roadmap's "Your voice" milestone. Nytka learns the wearer's voice once, fingerprints every
transcribed segment of 1 second or longer, and marks the segments whose fingerprint matches as the
wearer's. It no longer depends on the transcription provider to say which lines are the wearer's.
Telling other people apart stays with the provider and the people API of v0.6.

## Done when

1. The wearer enrolls by reading prompts into the pendant for 30 to 60 seconds; the server answers
   with how much speech it used and whether the samples agree with each other.
2. With a voice enrolled, every new segment of at least `voice.minSegmentSeconds` (default 1 s) and
   at most 30 s gets a similarity to the wearer's voice, and `isUser` follows it; shorter or longer
   segments get no label from Nytka.
3. A provider's `is_user` still applies to segments Nytka did not check, and a person's own "this is
   me / this is not me" on a segment beats both.
4. `DELETE /api/v1/voice` removes the wearer's voiceprint, every segment fingerprint and every
   label derived from them, in one transaction; the provider's labels come back.
5. No voiceprint or fingerprint appears in an API response, a webhook, an MCP answer or a log.
6. The evaluation in "How we measure it" passes on the owner's own recordings, outside the
   repository.

## The model

**TitaNet-small** (NVIDIA NeMo), in the ONNX export that sherpa-onnx publishes
(`nemo_en_titanet_small.onnx` in sherpa-onnx's `speaker-recongition-models` release, 40,257,283
bytes, SHA-256 `ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e`). It turns a
segment into one 192-number fingerprint; two fingerprints are compared by cosine similarity.

- **Licence.** NVIDIA's model page says "License to use this model is covered by the license of the
  NeMo Toolkit", and NeMo is Apache-2.0, like this repository; sherpa-onnx, which converted it, is
  Apache-2.0 too. Redistribution in the Docker image is allowed with attribution in `NOTICE`.
  NVIDIA publishes the larger TitaNet under CC-BY-4.0, which also allows redistribution with
  attribution, so `NOTICE` credits both ways.
- **Why this model.** The maintainer's `stt` service compared three models on necklace audio with
  the wearer's mixed Ukrainian, Russian and English speech against five synthetic voices. Only
  TitaNet-small separated them with a margin (wearer minimum 0.38, others maximum 0.06); WeSpeaker
  ResNet34 and 3D-Speaker CAM++ (VoxCeleb) overlapped. In a real two-person conversation afterwards,
  the wearer's median similarity was 0.67 and the other person's 90th percentile 0.32, and lines of
  2 s or longer were labelled right 94 to 99% of the time.
- **Languages.** TitaNet was trained on English (VoxCeleb, Fisher, Switchboard, LibriSpeech). The
  numbers above are the only evidence that it works for Ukrainian and Russian, and they come from one
  wearer, so enrollment covers every language the wearer speaks, and the evaluation reports each
  language on its own.
- **Cost.** About 50 ms of CPU per 3 s of speech on 2 threads, measured on the maintainer's server.
  At 4 hours of speech a day that is about 4 minutes of CPU.
- **How it runs.** Through the ONNX Runtime package Nytka already uses for Silero VAD, with the
  feature extraction ported to C# (80 log-mel bands, 25 ms frames every 10 ms, Hann window,
  pre-emphasis 0.97, Slaney mel scale, per-band normalization, all read from the model's metadata
  as sherpa-onnx does). A throwaway spike matched sherpa-onnx's own fingerprints at cosine 0.994 to
  1.000 on eight synthetic clips; sherpa-onnx's .NET package works too, but it ships its own
  `libonnxruntime` (1.28.2), which publish silently replaces with Nytka's (1.30.0), so it is the
  fallback only.
- **Where the file lives.** The Docker build downloads it and checks the SHA-256, so the image is
  about 40 MB larger and needs nothing at run time. It is not committed to git. Outside Docker,
  `scripts/fetch-speaker-model.sh` puts it in `src/Nytka.Audio/Models/`. Without the file the server
  runs as before: `/api/v1/info` leaves `voice` out of `features`, the voice routes answer `503`, and
  segments keep the provider's labels.

## Enrollment

**Reading through the pendant.** The app shows short prompts in each language the wearer chooses
(Ukrainian, Russian, English to start), about 15 to 20 seconds of reading per language. It records
the pendant's Opus frames while the screen is open and sends them to `POST /api/v1/voice/enrollment`
instead of the normal chunk upload, so the reading never becomes a conversation. The pendant, not the
phone's microphone, because matching later runs on pendant audio.

The server decodes the frames, finds speech with Silero VAD, cuts it into windows of 3 to 10 s,
fingerprints each, and stores their normalized mean as the voiceprint. It answers `422` when it
found less than 20 s of speech or fewer than 3 windows, or when the samples disagree (a window's
similarity to the mean below 0.5, which means a second voice or heavy noise), with a fixed message
that says which. The audio is not stored; it lives only in the request.

**Again.** `mode=replace` (the default) starts over; `mode=add` blends the new windows into the
current voiceprint, weighted by window count, for example to add a language later. Either way the
server rescores the fingerprints it still holds (below).

**Learning.** A matched segment of 2 s or longer with a similarity of at least
`voice.learnThreshold` (0.5) moves the voiceprint towards itself as a running mean, as the `stt`
service does, so the voiceprint follows the pendant's real acoustics. So does a segment of 2 s or
longer that the wearer marks as theirs. The enrolled mean is kept separately, so
`POST /api/v1/voice/reset` returns to it. `voice.learn` false stops learning.

Marking segments as yours cannot create a voiceprint from nothing: Nytka fingerprints only while a
voiceprint exists ("no voiceprint, no fingerprints", under Privacy).

## Matching

With a voiceprint and the model present, the `transcribe` job (Audio lane) does this after the
provider answers, using the batch WAV it already holds and each segment's start and end in the WAV:

| Segment | Nytka's verdict |
|---|---|
| shorter than `voice.minSegmentSeconds` (1 s) | none: checked, no similarity, no label |
| 1 s to 30 s | fingerprint; `isUser` true when similarity ≥ `voice.userThreshold` (0.38), else false |
| longer than 30 s | none: such a segment is usually the whole batch from a provider without segments, often several voices |

Short segments get no label rather than a borrowed one: in the `stt` service, giving a short line its
neighbour's label marked the other person's sub-second lines as the wearer's 88% of the time, and a
wrong "Wearer" line becomes a task or memory under the wearer's name, while an unlabelled one costs
little. Raising the threshold from 0.38 to 0.5 there cut false "wearer" lines only from 8% to 6% and
lost 4% of the wearer's own, so 0.38 is the default.

Fingerprinting never blocks transcription: if it throws, the segments are stored unchecked and the
log carries the exception type only.

**Rescoring.** Changing `voice.userThreshold` (in the settings API or by an environment variable,
noticed at startup) or the voiceprint (enrollment, reset) queues one `rescore-voice` job. It
recomputes similarities for the fingerprints still held, then re-applies the threshold to every
stored similarity, including segments whose fingerprint has expired.

## Which label wins

One rule, kept where v0.6 keeps it (`SpeakerLabel.Column` for SQL and `SegmentRow.Label()`
in C#):

1. The wearer's own mark on the segment (`PATCH /api/v1/segments/{id}`), true or false.
2. Nytka's verdict, when Nytka checked the segment (a checked short segment means unknown, not the
   provider's guess).
3. The provider's `is_user`.

Then, as in v0.6: `isUser` true labels the line `Wearer`, otherwise the person's name, otherwise the
provider's `speaker`. The provider's `is_user` stays stored unchanged, so turning Nytka's matching
off or forgetting the voice brings it back without reprocessing, and both opinions can be compared.
`GET /api/v1/voices` leaves out voices whose segments the rule makes the wearer's. Segments imported
from Omi have no audio, are never checked, and keep Omi's `is_user`.

## Privacy

A voiceprint is biometric data. The wearer consents by enrolling; the people around them do not, so
Nytka keeps as little about them as it can.

- **No voiceprint, no fingerprints.** Without an enrolled voice, Nytka computes nothing.
- **A fingerprint never outlives the audio it came from.** Segment fingerprints are deleted with
  their batch's speech audio (`audio.retentionDays`, default 14; with 0 they are deleted as soon as
  the segment's verdict is stored). The similarity number and the verdict stay; they cannot be
  turned back into a voice.
- **Nothing leaves the server.** The voiceprint and fingerprints are never sent to the transcription
  endpoint, the language model, a webhook or an MCP client, and never appear in an API response or
  a log. No route exports them: a 192-number vector is useless outside this model, and `pg_dump`
  already contains it for backups. An export feature leaves them out.
- **Forget my voice.** `DELETE /api/v1/voice` deletes the voiceprint and every segment fingerprint,
  clears every similarity and verdict, and queues nothing. The wearer's own marks stay: they are
  statements, not biometrics. Deleting a conversation deletes its segments' fingerprints by cascade.
- **README.** "What it stores" gains the voiceprint, the fingerprints and their lifetime.

## Settings

| Key | Variable | Default | Meaning |
|---|---|---|---|
| `voice.enabled` | `Nytka__Voice__Enabled` | `true` | Fingerprint new segments when a voiceprint exists. False keeps stored verdicts and checks nothing new |
| `voice.userThreshold` | `Nytka__Voice__UserThreshold` | `0.38` | Similarity at or above which a segment is the wearer's, 0.1 to 0.95 |
| `voice.learnThreshold` | `Nytka__Voice__LearnThreshold` | `0.5` | Similarity at or above which a segment of 2 s or longer updates the voiceprint; never below `userThreshold` |
| `voice.learn` | `Nytka__Voice__Learn` | `true` | Whether matches and marks update the voiceprint |
| `voice.minSegmentSeconds` | `Nytka__Voice__MinSegmentSeconds` | `1.0` | Shortest segment that is fingerprinted, 1.0 to 5.0 |

The model path is an option, not a setting (`Nytka__Voice__ModelPath`, default
`Models/nemo_en_titanet_small.onnx` beside the binaries); it is not editable from the app.

## Data

Migration `0013_voice.sql`:

- `voice_profile`: one row at most (`id smallint primary key check (id = 1)`), `model text` (the
  model file's SHA-256), `enrolled bytea` and `enrolled_count int` (the enrolled mean),
  `centroid bytea` and `centroid_count int` (with learning), `applied_threshold real`,
  `enrolled_at`, `updated_at`. A vector is 192 little-endian `float4`s, 768 bytes.
- `segment_fingerprints`: `segment_id bigint primary key references segments on delete cascade`,
  `batch_id bigint references transcription_batches on delete cascade`, `model text`,
  `fingerprint bytea`, `created_at`. Retention deletes rows with their batch's speech audio.
- `segments` gains `voice_checked boolean not null default false`, `voice_similarity real null`,
  `voice_is_user boolean null` and `is_user_manual boolean null`.

A fingerprint whose `model` differs from the voiceprint's is skipped by rescoring; a new model needs a
new enrollment.

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/voice` | admin | `{ enrolled, enrolledAt, updatedAt, enrolledSamples, learnedSegments, modelAvailable }`; never the vector |
| `POST /api/v1/voice/enrollment?mode=replace\|add` | admin | Body: Opus frames in the chunk format (`application/vnd.nytka.frames.v1`) or `audio/wav` (16 kHz mono 16-bit), at most 120 s. `200 { speechSeconds, samples, minAgreement }`; `422` too little speech or samples that disagree; `413` too long; `503` without the model |
| `POST /api/v1/voice/reset` | admin | Back to the enrolled mean, then rescore. `200` as `GET`, `404` without a voiceprint |
| `DELETE /api/v1/voice` | admin | Forget my voice (Privacy). `204`, also when none was enrolled |
| `PATCH /api/v1/segments/{id}` `{ isUser }` | admin | `true`, `false` or `null` (clears the mark). `200` with the segment. `true` on a fingerprinted segment of 2 s or longer also teaches the voiceprint |
| `GET /api/v1/voice/segments?since&until&limit` | admin | For evaluation: `{ items: [{ segmentId, conversationId, startedAt, endedAt, similarity, voiceIsUser, providerIsUser, manualIsUser }], nextSince }`, no text; `limit` defaults to 500, caps at 5000 |
| `GET /api/v1/conversations/{id}` | read | segments gain `isUserSource`: `manual`, `voice`, `provider` or null; `isUser` is the rule's result |

## Limits

- One wearer per server, as the vision says. Other people are not fingerprinted for identity.
- Overlapping speech in one segment gets one label; a provider's segment boundaries decide what a
  segment is.
- A cold, a whisper or shouting lowers similarity; learning absorbs gradual change, re-enrollment a
  lasting one.
- Similar voices can pass the threshold. In the spike, two different synthetic female voices
  (Ukrainian and Russian) scored 0.58 to 0.63 against each other, above 0.38. A household member
  with a voice close to the wearer's needs a higher threshold, found with the evaluation.
- Segments transcribed before enrollment stay with the provider's labels.

## How we measure it

Real voices never enter this repository (invariant 6). The owner runs this on their own server and
keeps the labels and results outside the repository:

1. **Sample.** At least 300 segments from at least 5 conversations recorded with the pendant after
   enrollment: a two-person back-and-forth, a group of three or more, a noisy place, and each of
   Ukrainian, Russian and English.
2. **Label blind.** For each segment, listen to it in the app (playback) or read it in context and
   write `wearer`, `other` or `mixed` in a CSV keyed by segment id, without looking at the labels
   Nytka shows.
3. **Join.** Fetch `GET /api/v1/voice/segments` for the same span and join on the segment id.
4. **Report, by duration (under 1 s, 1 to 2 s, 2 to 4 s, 4 s and more) and by language:** the share
   labelled at all; false wearer (share of `other` segments labelled wearer, the costly error);
   wearer recall (share of `wearer` segments labelled wearer); the same for the provider's
   `is_user` when the provider gives one; and the false-wearer and recall for thresholds from 0.30 to
   0.60 in steps of 0.02, to choose `voice.userThreshold`.
5. **Pass.** For segments of 2 s or longer: false wearer at most 5% and wearer recall at least 90%,
   in every language. For 1 to 2 s: false wearer at most 10%. Under 1 s: never labelled.
6. **Again after two weeks** of learning: the wearer's median similarity must not fall and false
   wearer must not rise; otherwise learning is drifting and `voice.learn` goes off by default.

In the repository, tests use synthetic signals only: the C# feature extraction is checked against
values that `kaldi-native-fbank` computes for a synthetic chirp with noise, and the fingerprint
against sherpa-onnx's for the same signal (stored as JSON next to the test, the model fetched in CI
as the dictionary is).

## Implementation tracks

| Track | Repo | Content | After |
|---|---|---|---|
| S-M | server | model fetch (Dockerfile, script, `NOTICE`), C# features, `SpeakerEmbedder`, parity tests | — |
| S-V | server | migration, matching in `transcribe`, label rule, retention, rescore job | S-M |
| S-A | server | voice routes, segment mark, settings, README | S-V |
| A-E | android | enrollment screen with prompts, pendant frames to the enrollment route | S-A |
| A-M | android | "this is me / not me" on a segment, `isUserSource` | S-A |

## Not in this version

- Telling other people apart by voice in Nytka (the `stt` service clusters them at 0.3): the
  provider's `speaker_id` and the people API stay the way. It would mean keeping fingerprints of
  people who never agreed to it, for longer than their audio.
- Labelling segments transcribed before enrollment from their stored audio.
- Splitting a segment where the voice changes, or labelling segments over 30 s in windows.
- A second wearer, or a voiceprint per pendant.
- Deleting the provider's voiceprint of the wearer: the `stt` service refuses it over HTTP on purpose.
