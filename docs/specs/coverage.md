# Coverage report: measuring "Nothing is lost"

**Status: draft.** The milestone in [../vision.md](../vision.md) says a week of wear with the
official app uninstalled loses no audio. This spec makes that measurable. It adds one endpoint,
`GET /api/v1/coverage`, and no migration, no wire format and no app change.

## Done when

1. After a week of wear, one request returns per-day seconds of connected, muted, away and
   unobserved time, the audio that arrived, the audio known lost, and a coverage fraction.
2. Every stretch counted as a loss is listed with a reason and a time, so the owner can match it
   against what happened that day.
3. Muted time, audio the mute filter dropped and silence the pendant never sent are not losses.
4. The report states what it cannot see (see Blind spots), so a clean report is not read as proof.

## What exists, and why it is enough

| Source | Holds | Use |
|---|---|---|
| `diagnostics` samples (v0.2; the app sends one every 10 s while capture runs, when "Send diagnostics to my server" is on; 30 days kept) | link state (`connected`, `muted`, `connecting`, `disconnected`, `refused`), the session id, cumulative `lostNotifications`, `droppedFrames`, `framesQueued`, `lostPackets`, `mutedFrames`, ring positions, queue size | what the phone saw: the time axis and the counters |
| `audio_chunks` rows (kept 7 days after processing) | session, first sequence number, frame count, the time it starts and the time its last frame ends | what arrived |

The pendant stops sending in silence, so the time between chunks cannot tell silence from loss, and
`received seconds / connected seconds` is not a coverage figure. Loss shows only in counters and in
numbering:

- the app numbers every frame it queues in a session from 0 without holes, so a hole in the numbers
  the server holds is a chunk that never arrived (the app parked it after a `400`, `409` or `413`, or
  dropped it);
- the app's `framesQueued` is the number it queued, so frames after the server's last chunk that no
  chunk carries were queued and never arrived;
- `lostNotifications` and `droppedFrames` count Bluetooth notifications and frames lost on the live
  link, `lostPackets` the ring packets the pendant freed before the phone read them.

A server-only join answers the question, so the app needs nothing new. The alternative, an app log
of capture spans, would record the same connection state the samples already carry, add a table and
a second upload path, and could not see the server's side at all.

## Endpoint

`GET /api/v1/coverage?from=&to=&bucket=&limit=`, `admin` scope: it reads the diagnostics samples,
which are admin-only.

| Parameter | Meaning |
|---|---|
| `from`, `to` | ISO 8601 with an offset. `to` defaults to now and never exceeds it; `from` defaults to the start of the local day six days before today. `400` when `from` is not before `to`, or the range is over 62 days (14 for hours) |
| `bucket` | `day` (default) or `hour`, in the server's `user.timeZone` |
| `limit` | gaps returned, default 500, cap 5000; `gapsTotal` counts them all |

Result: `{ from, to, timeZone, bucket, totals, buckets, gaps, gapsTotal, now, warnings }`.

A bucket (and `totals`) is `{ start, connectedS, mutedS, awayS, unobservedS, receivedS, lostS,
droppedByMuteS, coverage }`, all in seconds.

- **Time states** add up to the length of the bucket. A sample says what the link was like until the
  next one. `connected` and `muted` come from the `connection` word; `away` is `connecting`,
  `disconnected` or `refused`; `unobserved` is any stretch with no sample for over 30 seconds
  (the app was not capturing, or its samples have not arrived).
- `receivedS` is the frames in chunk rows times 20 ms, spread over each chunk's time span.
- `lostS` is the sum of the losses below. `coverage = receivedS / (receivedS + lostS)`, null when
  both are 0.
- `droppedByMuteS` is stored audio the mute filter dropped on purpose (`mutedFrames` times 20 ms).
  It is in neither side of `coverage`.

A gap is `{ from, to, reason, loss, audioS }`:

| Reason | `loss` | Found by | `audioS` |
|---|---|---|---|
| `link-loss` | yes | rise of `max(lostNotifications, droppedFrames)` between two samples of a session, times 20 ms | lost audio |
| `ring-lost` | yes | rise of `lostPackets`, times 80 ms (detected when the sync read, so the time is when it was noticed) | lost audio |
| `missing-chunk` | yes | hole in a session's sequence numbers | lost audio |
| `not-arrived` | yes | frames the app queued in an older session beyond the last chunk's | lost audio |
| `pending` | no | the same, in the newest session: still on its way | audio waiting |
| `away` | no | pendant not connected; its stored audio arrives by sync | audio that arrived for that stretch |
| `unobserved` | no | no samples | audio that arrived anyway |
| `muted` | no | link state `muted` | null |

Losses closer than a minute with the same reason are one gap. `now` carries `lastSampleAt`,
`lastAudioAt`, `pendingOnPendantS` (the ring's `writeSeq - readSeq` of the newest sample, times
80 ms) and `queuedChunksOnPhone`. `warnings` says when there are no samples in the range, when the
newest sample is stale, and when the range starts before chunk rows are kept.

Counters are cumulative per capture session (one run of the capture service), so the report takes
differences per session, and reads a counter that falls as a restart at 0.

## Blind spots

- **The pendant drops frames before numbering them** when its link stalls (spec v0.3, Link stalls).
  No counter sees that. A stall shows as `link-loss` only when it also loses notifications that were
  numbered.
- **Unobserved time** is unknown, not clean. Treat a week with hours of it as unmeasured for those
  hours.
- Frame and packet counts become seconds with 20 ms and 80 ms; both are estimates, the second the
  app's own.
- Chunk rows go 7 days after processing, so the report must be read, or saved, within 7 days of the
  first day it covers.

## Pass rule (proposed)

The owner decides; this is the starting point. Over the week, no `missing-chunk`, `not-arrived` or
`ring-lost` gap, `link-loss` under 0.1% of `receivedS`, and `unobservedS` under 1% of the week.
