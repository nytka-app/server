# Your voice: implementation plan

**Spec:** [`docs/specs/your-voice.md`](../specs/your-voice.md). Read it first; this plan only orders the
work and names the files and tests. Nothing here starts before the owner accepts the spec.

**Shape.** Three server PRs in order (S-M, S-V, S-A), then two app PRs. Each PR passes
`dotnet test` with the model present (CI fetches it) and skips the model tests cleanly without it.

## S-M: the model and fingerprints (`Nytka.Audio`)

1. `scripts/fetch-speaker-model.sh`: downloads `nemo_en_titanet_small.onnx` from sherpa-onnx's
   `speaker-recongition-models` release into `src/Nytka.Audio/Models/`, checks SHA-256
   `ad4a1802…a789e` (full value in the spec), leaves an existing good file alone. `.gitignore` the
   file.
2. `Dockerfile`, build stage: run the script before `dotnet publish`, so the file lands in `/app/Models`.
   `NOTICE`: TitaNet-small (NVIDIA, NeMo licence, Apache-2.0; NVIDIA publishes TitaNet-L under
   CC-BY-4.0) converted by sherpa-onnx (Apache-2.0), with both URLs.
3. `Nytka.Audio/Voice/NemoFeatures.cs`: log-mel features as sherpa-onnx computes them for NeMo
   models (`speaker-embedding-extractor-nemo-impl.h`, `kaldi-native-fbank`): 400-sample frames every
   160, edges snipped, no DC removal, pre-emphasis 0.97 before a periodic Hann window, 512-point FFT,
   power spectrum, 80 Slaney-scale bands from 0 to 7,600 Hz with Slaney normalization, natural log
   floored at `float` epsilon, then per-band mean and standard deviation normalization (+1e-5).
   Frame count, sample rate, band count and window come from the model's metadata; anything else
   (`framework` other than `nemo`) refuses to load.
4. `Nytka.Audio/Voice/SpeakerEmbedder.cs`: one `InferenceSession` (2 intra-op threads, a setting of
   the option), input `audio_signal` `[1, 80, T]` with the real frame count `T` (sherpa-onnx grows its
   buffer to a multiple of 16 but never feeds the padding; feeding it drops cosine to 0.998) and `length`
   `[T]`, output `embs`, normalized to unit length. Thread-safe (features per call, the session's
   `Run` is), so the enrollment request and the Audio lane can share it. `Embed(ReadOnlySpan<float>)`
   and `Cosine`.
5. Tests (`Nytka.Audio.Tests/Voice`): features of a synthetic chirp with noise match
   `kaldi-native-fbank` values stored as JSON (max abs error < 1e-3); the fingerprint of the same
   signal matches sherpa-onnx's within cosine 0.999 (the spike reached 0.994 to 1.000 on speech;
   find the gap before relaxing the bar); skip with a reason when the model file is absent, fail
   when `NYTKA_REQUIRE_SPEAKER_MODEL=1` (CI). The generator script for the JSON lives in
   `tests/fixtures/README` with exact package versions, not in the build.
6. Measure inside the container on the target box: RSS before and after loading, ms per 3 s window
   at 1 and 2 threads. Put the numbers in the PR. The spike's numbers came from a laptop with a load
   average near 350 and are not a basis.

## S-V: matching in the pipeline

1. `db/migrations/0013_voice.sql` as the spec's Data section.
2. `Nytka.Storage/VoiceStore.cs`: profile read and write, fingerprints insert, verdict update,
   rescore queries, forget (one transaction).
3. `TranscribeHandler`: after `client.TranscribeAsync`, when `voice.enabled`, a profile exists and
   the model loaded, fingerprint each segment from `batch.Wav` by its WAV-relative `Start`/`End`
   (before the offset map turns them into capture times), apply the spec's table, learn as the spec
   says, and pass verdicts and fingerprints to `BatchStore.CompleteAsync` so they land in its
   transaction (invariant 2). A throw from fingerprinting is caught, logged by type and leaves the
   segments unchecked.
4. Label rule: `SpeakerLabel.Column` and `SegmentRow.Label()` read
   `coalesce(is_user_manual, case when voice_checked then voice_is_user else is_user end)`; the two
   `is_user is not true` filters in `PeopleStore` use the same expression (one constant).
   `ConversationStore`, `McpQueries`, the prompts and Ask pick it up through those.
5. Retention: `DeleteSpeechAudioEndedBeforeAsync` and the `RetentionDays=0` path in `CompleteAsync`
   delete `segment_fingerprints` of the same batches.
6. `rescore-voice` job (Audio lane, dedupe key `rescore-voice`): recompute similarity where the
   fingerprint's model matches, then `voice_is_user = voice_similarity >= threshold`, then store
   `applied_threshold`. Queued by enrollment, reset, a settings PATCH that touches
   `voice.userThreshold`, and at startup when `applied_threshold` differs from the setting.
7. Tests (`Nytka.Server.Tests`, Testcontainers, a fake `ISpeakerEmbedder` returning set vectors):
   a 0.6 s segment is checked with no label; a 1.2 s segment at similarity 0.40 is the wearer and at
   0.37 is not; a 45 s segment is unchecked by voice; provider `is_user` true on an unchecked segment
   still shows `Wearer`, on a checked one loses to Nytka's false; a manual false beats both;
   retention deletes fingerprints with the audio and keeps similarity; a threshold change rescores
   expired segments from their similarity; a fingerprinting exception still stores the transcript;
   no voiceprint means no fingerprint rows.

## S-A: routes, settings, README

1. Settings group `voice` (`SettingDefinition`s, validators with ranges, `learnThreshold` not below
   `userThreshold`), variables in `.env.example` (commented) and `docker-compose.yml` (empty
   defaults); `Nytka__Voice__ModelPath` in `NytkaOptions`.
2. `Api/VoiceEndpoints.cs`: the spec's routes. Enrollment decodes frames with the existing chunk
   reader and Concentus, or reads a WAV with the existing WAV code; Silero VAD; windows of 3 to 10 s;
   the `422` rules; nothing stored but the vectors; a 120 s cap checked before decoding.
3. `PATCH /api/v1/segments/{id}`; `isUserSource` on conversation segments; `/api/v1/info` features
   gain `voice` when the model loaded.
4. README: a "Your voice" section (enroll, matching, thresholds, forget, the evaluation in short),
   the API table rows, "What it stores", and the stale "Speaker labels" paragraph that still says
   Nytka does not match voices. MCP is unchanged apart from the labels.
5. Tests: enrollment with a synthetic two-tone signal and the fake embedder returns `422` for
   disagreeing samples and `200` for agreeing ones; `DELETE /api/v1/voice` leaves no row in
   `voice_profile` or `segment_fingerprints` and no similarity; no route's JSON contains a vector
   (assert on response size and keys); a read token gets `403` on every voice route.

## App (android, after S-A)

- A-E: enrollment screen in settings: language picker, 3 prompts per language, a level meter, live
  upload paused while the screen records, the `422` messages shown as they come.
- A-M: long-press on a segment: "This is me", "This is not me", "Clear"; the label shows its source.

## Review focus

1. **A wrong "Wearer" is the costly error.** Every path that can set `isUser` true (Nytka, provider,
   manual) goes through the one rule, and short segments never borrow.
2. **Biometrics stay put.** Grep the diff for every place a vector is serialized; only Postgres
   gets one. Fingerprints die with their audio.
3. **Transcription never waits on, or fails because of, the voice code.**
4. **Times.** Fingerprints cut the WAV by the provider's WAV-relative times; only the offset map
   turns them into capture times (invariant 1).

## Spike record (not in the repository)

A throwaway console project on 2026-10-04, outside the repository, with eight synthetic macOS voices
(two clips each of an English female, an English male, a Ukrainian female and a Russian female):

| Check | Result |
|---|---|
| C# features plus ONNX Runtime 1.30.0 vs sherpa-onnx 1.13.8 (Python) on the same clips | cosine 0.9935 to 1.0000 |
| sherpa-onnx's .NET package in the same process as ONNX Runtime 1.30.0 (macOS arm64) | works, cosine 1.0000 to sherpa-onnx Python |
| `dotnet publish -r linux-x64` with both packages | succeeds; the output keeps Microsoft's `libonnxruntime.so` (28,985,152 bytes) and drops sherpa-onnx's 1.28.2 build without a warning |
| TitaNet-small, same voice, other sentence | 0.84 to 0.95 |
| TitaNet-small, different voices | 0.01 to 0.63 (the two synthetic female voices in Ukrainian and Russian 0.58 to 0.63) |
| 3D-Speaker CAM++ zh-en "advanced" (28 MB), same pairs | same voice 0.86 to 0.94, different 0.04 to 0.66; 2.6 times slower than TitaNet in the same run |
| Time per 3 s of audio, 2 threads, laptop under load average ~350 | 127 to 319 ms in C#, of which features 7 to 41 ms; sherpa-onnx Python 253 ms |
