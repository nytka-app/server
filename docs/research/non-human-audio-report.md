# Non-human audio: what the pendant hears that is not a person

Research for the Speech kind spec ([specs/speech-kind.md](../specs/speech-kind.md)). Measured on the owner's
server on 5 October 2026, from 8 days of recordings (28 September to 5 October; nothing on 3 October). Every
number here is an aggregate. No recording, transcript line, name or label left the server: the harness ran in a
throwaway container next to the database, read it read-only, and printed only counts, minutes and scores.

## Summary

- **Media is rare, not everywhere.** Of other-voice speech time, about 3 to 4% is media (1 of 50 random clips,
  95% interval 0 to 10%; 4.4% weighted over all 100 round-one clips). 91 of 100 round-one clips were people in the
  room. The brief's "66% of conversations have no wearer speech" came from days that had no wearer labels.
- **Where it hides:** 6 of 25 clips more than 60 s from the wearer's own speech were media; 1 of 75 within 60 s.
  Media is almost never mistaken for a conversation partner, and it is mostly in a different script (Latin
  letters in a Ukrainian and Russian household).
- **What works** (leave-one-conversation-out, 27 media and 106 person clips): distance and turn-taking structure
  alone, AUC 0.93; with the light AudioSet tagger YAMNet, AUC 0.94. The safe operating point (no labelled person
  called media) catches 12 of 27 media with both, 8 with structure alone.
- **What does not:** the LLM judge (called 19 of 91 people media or call), replay detectors, mains hum, repeated
  audio, playback capture, spatial cues, voice-cluster recurrence.
- **Recommendation:** a calibrated score from structure, a small AudioSet tagger and the phone's own playback
  state, **in shadow mode first**. With 3 to 4% prevalence, a rule that wrongly flags 2% of people would be wrong
  about half the time it says media, so nothing stops feeding the people features until the owner's own review of
  shadow flags says so.


## Data

| What | Amount |
|---|---|
| Conversations recorded by Nytka (13 Omi imports left out: no audio) | 145, of which 133 have segments |
| Segments | 6,566 |
| Speech audio still stored (14-day retention), decoded for this study | 7.24 h in 2,320 batches |
| Wearer's own speech / other voices / segments with both | 270 / 192 / 65 min |

Wearer labels changed three times in those 8 days, which matters for every number below:

- 28 and 29 September: the transcription adapter returned one unlabelled segment per batch. No segment of those
  days has a wearer verdict.
- From 30 September: the adapter's own diarization and wearer flag (`is_user`; TitaNet-small, threshold 0.38).
- From 4 October 19:40 UTC: Nytka's own voice match (Your voice).

To compare like with like, every segment was re-scored with the same model and centroid the adapter uses
(TitaNet-small through sherpa-onnx, cosine similarity, threshold 0.38; 3 s windows for segments longer than 4 s).
The re-scored verdict agrees with the live label rule on 87% of segments and 94% of their duration. Below,
"wearer" is the live label rule where it has a verdict and the re-scored one elsewhere; a long early segment whose
windows disagree is "mixed".

## The premise, re-measured

The brief's evidence came from a first pass over the database. Three of its numbers do not hold:

| Brief | Re-measured | Why |
|---|---|---|
| 103 of 157 conversations (66%) have no wearer speech, holding 154 of 338 "other" minutes | 38 of 133 recorded conversations have no wearer speech, holding **5.8 min** of other voices; the longest has 4.1 min of speech | 75 conversations of 28–29 September had no wearer verdict at all, so they counted as "no wearer". The 338 minutes also held 149 unlabelled minutes and 15 minutes of Omi imports |
| A 74-minute conversation on 29 September has zero wearer speech | No conversation of an hour or more lacks wearer speech; the long one of 29 September (now 105 min after merges) holds 32 min of the wearer and 37 min of mixed segments | Same: no verdicts that day |
| Up to 52 distinct `SPEAKER_n` labels in one conversation: the provider's diarization is unstable | The labels come from the transcription adapter's own speaker clustering, not from the provider | The adapter matches every segment against all stored clusters at cosine 0.3 and keeps every fingerprint, media voices included, indefinitely: 4,377 fingerprints in 165 non-wearer clusters (58 of one segment, 4 of over 100) |

The problem itself is real: other voices that are not people in the room exist and reach names, groups and facts.
Its size is a share of 192 minutes of other-voice speech, not of 338, and conversations made only of media are rare
and short. The work is therefore mostly about **segments inside conversations** where the wearer is also present
(a TV on while he talks to someone, a video he watches and comments on), which is the harder case.

## Where other voices sit relative to the wearer

Distance from each other-voice segment to the nearest wearer speech in the same conversation:

| Distance | Minutes | Share |
|---|---|---|
| Overlapping wearer speech | 4.9 | 3% |
| Up to 5 s | 80.6 | 42% |
| 5 to 10 s | 22.2 | 12% |
| 10 to 30 s | 31.2 | 16% |
| 30 to 60 s | 17.4 | 9% |
| 1 to 5 min | 21.6 | 11% |
| Over 5 min | 14.3 | 7% |

Language of the same speech (lingua, segments of three words or more):

| Language | Other voices, min | Wearer, min |
|---|---|---|
| Ukrainian | 61.6 | 129.9 |
| Russian | 78.9 | 117.6 |
| English | 19.9 | 12.1 |
| Other | 0.7 | 1.0 |
| Too short to tell | 31.3 | 9.5 |

The wearer speaks English for 12 minutes himself, so "English means media" would call his own English
conversations media.

## The labelled sample

The owner labelled 140 clips of at most 10 s of other-voice speech on a private page served from the box, each
with its transcript lines, an audio player and four buttons: person here, media, call on speaker, can't tell.
Round one: 100 clips, half a simple random sample of all candidate clips (1,564 clips, 166 min) and half spread
over 12 strata (distance to the wearer, script, period). Round two: 40 clips enriched for likely media, to get
enough positives; it is not used for base rates.

| Labels | Round one (100) | Round two (40, enriched) | Total |
|---|---|---|---|
| Person here | 91 | 18 | 109 |
| Media | 7 | 20 | 27 |
| Call on speaker | 0 | 0 | 0 |
| Can't tell | 2 | 2 | 4 |
| "Both audible" ticked | 0 | 0 | 0 |

Round-one clips by distance to the wearer's own speech (strata in the sample, not the population):

| Distance | Clips | Media |
|---|---|---|
| Within 60 s | 75 | 1 |
| More than 60 s | 25 | 6 |

Base rates, from the random half only: media 1 of 50 clips (3.2% of clip time, 95% interval 0 to 9.9%); weighted
over all round-one clips 4.4%. The interval is wide: **the true share could be anywhere from 0 to 10%.**
No call on speaker occurred in the sample; the owner's phone logged 3 calls (2 min) in the 8 days.

The confusion the brief asked about, media against people physically present, is in the next section: it is
a distance and context question, not a voice question. Cases that are truly ambiguous (a call on speaker,
a person speaking over a TV) did not occur in 140 clips: UNKNOWN how often they do.

## The phone's own state (candidate 7), measured

The owner's phone (read over adb, read-only `dumpsys`; only time ranges were kept) holds audio-on ranges in its
battery history from 4 October 14:53 UTC, 98 ranges of 5 s or more in the window where segments also have
verdicts. Other-voice speech against those ranges:

| Other-voice speech | Inside phone audio-on | Outside |
|---|---|---|
| Within 10 s of the wearer's own speech | 0.2 min | 26.0 min |
| More than 10 s from the wearer's own speech | 11.3 min | 24.3 min |

Phone audio-on explains 32% of the far speech, and almost none of the speech that alternates with the wearer, so
as a prior it is precise and incomplete: the rest of the far speech is a TV, a person, or a phone sound the
battery flag misses. The flag is "audio hardware on", not "playing through the speaker": it also covers earbuds
and notification sounds, which the app's own callbacks (usage, route, volume) separate. Only 4 October onward is
available (the battery history is a day long); `dumpsys audio` keeps about two hours. The call log has 3 calls in
the 8 days (2 min) and the battery history one call range: calls are too rare here to measure.

## Candidates

Scores below are the area under the ROC curve (AUC; 0.5 is chance, 1 is perfect) on the 133 clips labelled
person or media, with 95% bootstrap intervals; "far" is the 49 clips more than 60 s from the wearer's own speech
(25 media, 24 person), where structure cannot help much. The enriched round makes recall numbers optimistic for
prevalence and the person count (106) limits false-alarm estimates: zero false alarms in 106 means at most 2.8% at
95% confidence.

| # | Candidate | AUC, all | AUC, far only | Verdict |
|---|---|---|---|---|
| 1 | Structure: distance to the wearer's own speech | 0.94 [0.88, 0.99] | 0.93 [0.84, 0.99] | **Use.** Cheap, uses stored data |
| 1 | Structure: share of the conversation that is the wearer | 0.89 | 0.85 | Use |
| 1 | Structure: length of the run of non-wearer speech | 0.84 | 0.83 | Use (small weight) |
| 1 | Turn count, speech duty cycle, a voice that alternates with the wearer | 0.83, 0.54, 0.76 | 0.50, not run, 0.62 | Only help where distance already does: skip |
| 2 | Channel cues: level, spectral rolloff, tilt, noise floor, modulation, reverberation | best 0.78 (level, inverted) | 0.81 | Weak, partly a proxy for distance to the wearer; the others were at chance. Skip |
| 3 | YAMNet "Television" | 0.80 | 0.84 | **Use** (15.7 MB, Apache-2.0, 12 ms per second of audio on one server thread) |
| 3 | PANNs CNN14 "Television" | 0.88 | 0.89 | Same signal, but 327 MB and ~49 ms per second: YAMNet instead |
| 3 | CLAP 2023 media-minus-person prompts | 0.80 | 0.80 | 690 MB, no clean ONNX: skip |
| 3 | CNN14 embedding with a model trained on turn-taking weak labels, tested on the owner's labels | 0.89 | 0.87 | Promising, no labels needed; weights too large for now |
| 3 | Replay and loudspeaker detectors (AASIST, RawNet2, Void) | not run | | Published weights are for synthetic speech, not replay; replay-to-real-room transfer is near chance in the literature: skip |
| 4 | LLM judge (the pipeline's gpt-4o-mini, ±90 s of transcript), round one only | 0.78 [0.52, 0.96], 7 media | not run | **Rejected**: it called 15 of the 91 round-one person clips media and 4 call; precision on media 5 of 20 |
| 4 | Latin-script share of the text | 0.84 | 0.80 | Cheap proxy; works because this household speaks Ukrainian and Russian. Language-dependent: feature of the household model only |
| 4 | Language differs from the wearer's in that conversation | 0.76 | 0.69 | Weaker than script; skip |
| 5 | Voice recurs, speaker never alternates, cluster size and days | 0.76, 0.73, 0.53 | 0.62 | Skip (the fingerprints fragment: 883 singletons of 1,010 clusters) |
| 6 | The owner's rejects as labels | | | Built into the design: marks are labels, the review inbox collects them |
| 7 | Phone audio-on ranges | see the section above | | **Use as a prior**; precise (0.2 min of near-wearer speech inside), covers a third of far media |

**Combined, leave-one-conversation-out, person first.** A logistic model on the standardized features, with the
threshold set so that the fewest labelled people are called media:

| Features | AUC | People called media: 0 of 106 | 1 of 106 | 2 of 106 |
|---|---|---|---|---|
| Structure (distance, run, wearer share) | 0.93 | catches 8 of 27 media | 16 | 17 |
| YAMNet only (Television, Narration, Speech synthesizer) | 0.82 | 7 | 13 | 15 |
| Structure and YAMNet | 0.94 | **12** | 13 | 16 |

Differences between rows are inside the noise of 27 positives. The recommendation keeps structure and YAMNet
because they fail differently: structure misses a TV while the wearer talks, YAMNet does not need the wearer.
Of the 521 AudioSet classes only three were chosen in advance (Television, Narration monologue, Speech
synthesizer); other classes reach higher AUC on this sample (Telephone, Ringtone, Alarm, 0.8 to 0.9) but picking
by result on 27 positives would overfit.

**What fails, in the owner's household:** a person speaking in Latin script (English speech, names, code) is the
likeliest false alarm for the script feature; a quiet TV with the wearer talking over it is the likeliest miss.
The evaluation in the spec re-measures both from the owner's own taps.

## Measured without labels, and rejected

| Candidate | Measurement | Verdict |
|---|---|---|
| Spatial cues from the pendant (10) | The Omi CV1 has two TDK T5838 microphones, but the firmware averages them to mono before Opus (`omi/firmware/omi/src/mic.c` at `98cb5aa`, `(L+R)>>1`); no level, VAD or channel data travels with the audio | Not possible without new firmware |
| Mains hum, ENF (11) | The encoder is Opus CELT-only at 32 kbps with only a 3 Hz DC filter, so 50 Hz survives in pauses (a synthetic test recovered it), but a TV and a person in the same room share one grid | Cannot tell a TV from a person: rejected |
| Repeated audio, the same video twice (5) | Dense spectral-peak landmarks over 1,337 other-voice segments: the best time-consistent match is 13 hashes, the noise floor; none reaches 20 | No repeat in 8 days: no yield now; worth re-running on months of data |
| Repeated words (13) | Word 5-grams of every segment against speech more than 10 minutes away: no segment shares two | Same: no rewatched video, song or ad in the sample |
| Voices that recur across days (5) | 1,580 other-voice segments, TitaNet-small, average linkage at cosine 0.55: 1,010 clusters, 883 singletons; 25 clusters of 5+ segments; none seen on 3 or more days | TitaNet-small fragments other voices too much to see a "TV presenter every evening" pattern; rejected for now |
| Playback capture as a reference (8) | `AudioPlaybackCaptureConfiguration` needs `RECORD_AUDIO` (the app's invariant 7 forbids it), a MediaProjection consent every session, stops when the screen locks (Android 15 QPR1 onward), never captures calls, and Spotify and Chrome opt out | Rejected |
| The Mac's power log as a "computer is playing" signal (13) | coreaudiod holds an audio assertion whenever an app keeps an audio context open: the union of its ranges covers 41.6 h of a 41.6 h log | Says "an app has audio open", not "sound is playing": rejected |
| Router call detection (`senses`, 9) | Deployed on 5 October; one call range so far | No history for this sample; a usable call label from now on, on this network only |

## Costs and licences

Measured on the server (a container limited to 4 vCPU, beside the live services), per candidate:

| Candidate | Compute | Model, licence | Permissions, privacy |
|---|---|---|---|
| Turn-taking (1) | SQL and C# over a conversation's segments: negligible | none | none: uses what is stored |
| Channel cues (2) | 541 s for 4,958 segments in Python, about 0.1 s each; in C# at transcription time, a few ms per segment | none | numbers only; must run while the batch WAV exists |
| PANNs CNN14 (3) | 531 s for 2,944 segments (10 s cap), 42 G multiply-adds per 10 s | 327 MB, code MIT, weights CC BY 4.0, 32 kHz input | none |
| EfficientAT mn10 (3, not run) | about 5 ms per second of audio | 20 MB, MIT | none |
| CLAP 2023, Microsoft (3) | 399 s for 2,944 segments (7 s cap) | 690 MB, code MIT, weights MS-PL or CC BY 3.0 | none |
| Replay detectors (3, not run) | | AASIST MIT, weights trained on synthetic speech, not replay | none |
| LLM judge (4) | one call per clip here; in production one per conversation, about the transcript's size in tokens | the configured chat model | transcript text to the model enrichment already uses: no new recipient, a second copy of the text |
| Text cues, local (4) | negligible (lingua, Apache-2.0) | none | none |
| Voice clusters (5) | TitaNet fingerprints already made for voice matching | TitaNet-small, CC BY 4.0 | needs strangers' fingerprints, the thing this work wants fewer of |
| Phone context (7) | Android callbacks, no polling | none | no permission; ranges of times only; Google Play data-safety disclosure |
| Router state (9) | the homelab's `senses` service | none | this network only; not a product feature |

## Unknowns

- How often media and the wearer's own speech are mixed (a person talking over a TV). 0 of 140 clips were ticked
  "both audible", but the sample picks other-voice clips, so a TV taken for the wearer is invisible here.
  UNKNOWN; the app's mark on any line, the wearer's included, collects it.
- Calls on speaker: 0 in 140 clips and 3 in the phone's 8-day log. UNKNOWN whether the guess works for them.
- False alarms below 2.8%: 106 person clips cannot show them. UNKNOWN until shadow mode has run for weeks.
- Prevalence in other households and languages: the Latin-script and language-mismatch signals are particular to
  a Ukrainian and Russian household and say nothing about an English-speaking one.
- YAMNet's cost inside the server's own pipeline: 12 ms per second of audio measured in a container on the box
  with onnxruntime for Python; the .NET onnxruntime figure is UNKNOWN.
- Whether the pendant's energy gate (hardware wake at 75 dB, `t5838_aad.c`) drops quiet TV segments before
  recording: UNKNOWN; if it does, quiet media is invisible and already absent from the data.
- The phone's history reaches back one day (battery stats) or two hours (audio events); the share of far media
  the phone explains comes from about 36 h and one phone.
- The transcription adapter keeps every voice fingerprint indefinitely (4,377 fingerprints in 165 clusters): not
  Nytka's to change; a retention policy for it is the one privacy recommendation of this study.
