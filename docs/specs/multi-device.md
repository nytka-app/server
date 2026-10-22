# Nytka, more than one kind of recorder: the server side

The app learns to speak to recorders other than the Omi pendant (nytka-app/android
[`docs/specs/multi-device.md`](https://github.com/nytka-app/android/blob/main/docs/specs/multi-device.md):
models, drivers, stored audio). This spec says what the server must do so that a recorder with another
codec can never lose audio, and what it would take to decode one. The device survey behind both specs:
[`docs/research/2026-10-20-wearables.md`](../research/2026-10-20-wearables.md).

Verified against `main` at `b6d9fd1` on 2026-10-20.

Status: step 1 built (NYTKA-120): codec registry, `codecs` in `/api/v1/info`. Step 2 waits for the Friend pendant.

## Done when

1. `/api/v1/info` lists the chunk codecs this server decodes, as `codecs: [21]`.
2. A chunk with a codec the server does not list is refused with `400`, as today, with the codec in the
   message.
3. Every chunk stored before this change reads back unchanged: codec 21 and format version 1 keep their
   meaning and layout.
4. Adding a codec later is one registry entry plus a decoder, with no change to the wire layout.

## What exists today

- The chunk header already carries a codec byte at offset 5 (`docs/specs/v0.1.md`, "Wire format"), and
  `Chunk` keeps it (`src/Nytka.Audio/Frames/Chunk.cs:4`). `ChunkFormat.Read` refuses anything but
  `OpusFs320 = 21` (`src/Nytka.Audio/Frames/ChunkFormat.cs:47-50`) and stamps 21 on what it returns
  (`:108`).
- The body is stored as received and parsed again when a session is processed
  (`src/Nytka.Server/Pipeline/ProcessSessionHandler.cs:78`), for playback (`Api/AudioEndpoints.cs:74`), for
  card clips, speech classification and voice enrollment. No table has a codec column, so there is nothing
  to migrate.
- 20 ms frames at 16 kHz are fixed in `Frame.DurationMs` (`Frames/Frame.cs:6`),
  `Timeline.SampleRate`/`SamplesPerFrame` (`Decoding/Timeline.cs:13-14`), `OpusFrameDecoder` (always 320
  samples, else silence, `Decoding/OpusFrameDecoder.cs:17-31`) and `PlaybackMap`. Playback copies Opus
  packets into Ogg without decoding (`Api/AudioEndpoints.cs:42`, `People/CardClip.cs:22`).
- The app deletes a live chunk the server refuses with `400` (app CLAUDE.md, invariant 9). So a refused
  codec is lost audio unless the app knows before it captures.

## Design

### The codec registry

`ChunkFormat` gets a registry of codec values. Values are Nytka's own; 21 equals Omi's BLE value for the
same codec by history, and no other value is borrowed from Omi.

| Value | Codec | Frame | Rate | Accepted |
|---|---|---|---|---|
| 21 | Opus, one packet per frame | 20 ms | 16 kHz output | Yes (since v0.1) |
| 40 | LC3 | 10 ms | 16 kHz | Reserved: Friend pendant, after a decoder ships |
| 50 | AAC in ADTS | per frame header | UNKNOWN | Reserved: Bee, only if its audio stops being encrypted |

A reserved value is refused like any unknown value until its row says "Yes". The format version stays 1:
the layout does not change, only the set of accepted values. A version 2 is needed only if a codec needs
per-frame data the record header lacks.

Limitless, Plaud NotePin and Fieldy send Opus at 16 kHz and need no new value, provided the app delivers
20 ms packets. The app joins 10 ms Opus frames in pairs (RFC 6716 §3.2) rather than adding a value, so
`OpusFrameDecoder`, `Timeline` and playback stay as they are for every recorder planned in stage 2.

### `/api/v1/info`

`InfoResponse` gains `Codecs` (`IReadOnlyList<int>`), built from the registry's accepted rows. A server
without the field is read by the app as `[21]`; the app decodes JSON with `ignoreUnknownKeys`, so older
apps are unaffected. Before it captures from a recorder, the app checks the recorder's codec against this
list and refuses the connection with a fixed sentence when it is missing.

### When a second codec is accepted

Not part of the first PR. What has to change, so its size is known:

- `Frame.DurationMs` becomes a property of the codec; `Timeline.Decode` picks the decoder by `chunk.Codec`
  and builds frame starts from the codec's frame length.
- Playback and card clips are Ogg Opus. For a non-Opus codec, decode to PCM and re-encode with
  `OpusFrameEncoder` (it exists, `Decoding/OpusFrameEncoder.cs`), or serve WAV; pick one in that PR.
- A session holds one codec. `ChunkStore` refuses a chunk whose codec differs from the session's earlier
  chunks with `400` ("A capture session uses one codec."). The app never mixes, so this only guards bugs.
- LC3 has no managed .NET decoder known to us (UNKNOWN); it would mean a native `liblc3` build in the
  image. That cost is why Friend comes last.

## Roadmap

"More pendants" names Plaud and Bee. Bee firmware 0.6.1 and later encrypts audio, and Omi's connector says
it cannot read it (`bt_device.dart:626-640` in `BasedHardware/omi` at `afad3b7`), so ROADMAP.md now names
Plaud and Fieldy, both Opus, both on sale. Bee comes back only if that changes.

## Steps

1. Codec registry and `codecs` in `/api/v1/info`; refusal message names the codec; tests that a stored
   v1 chunk still reads. No hardware.
2. (Later, with the Friend pendant) per-codec frame length, LC3 decoder, playback for non-Opus, one codec
   per session.
