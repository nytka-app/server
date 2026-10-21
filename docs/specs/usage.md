# Nytka, usage and budget

Transcription is the one part of Nytka that costs money per hour worn: a pendant worn all day sends 300 to 700 batches of
speech, and a paid engine bills each one. Plane NYTKA-88 (this covers its transcription part; model tokens and storage
come later). The owner sees what transcription costs, what the month will cost, and caps it, without losing audio.

## The ledger

Every answered transcription request writes one row of `transcription_usage` (migration `0036_transcription_usage.sql`)
in the transaction that stores the batch's segments: the batch and its conversation, the engine, the speech sent (`audio_ms`, the WAV's length, which is
speech only), the length of the text that came back and the cost by the price table at that moment. A failed request
writes nothing. A row has no foreign key: money spent stays spent when its conversation is deleted.

The **engine** is the `model` Nytka sent, in lower case, or `auto` when none is sent and the endpoint picks one. The
homelab `stt` service reads `model` as a pinned engine (`soniox`, `parakeet`, ...).

**Backfill.** At every start the server gives each finished batch without a row an estimated one (`estimated = true`):
speech from the batch's offset map, text from its stored answer, engine and price as configured now, because older
batches never recorded which engine answered. Batches finished since the ledger exists always have their row, so after
the first start it adds nothing.

## Prices

`stt.prices` is JSON by engine. A request costs

    requestTokens × textInUsdPerMillionTokens / 1e6
    + audio hours × audioTokensPerHour × audioUsdPerMillionTokens / 1e6
    + text characters / textCharsPerToken × textOutUsdPerMillionTokens / 1e6

A field left out is 0, and an engine the table does not name costs nothing, as Parakeet on the box does. The default is
Soniox async: audio $1.50 per million tokens at about 30,000 tokens an hour, text in and out $3.50 per million. Soniox
bills the context text that `stt` sends with every request (611 characters, `stt` logs it per call), so a request has a
fixed cost. `requestTokens` (390) and `textCharsPerToken` (3) are fitted to a real bill: 4,717 batches, 16 hours of mostly
Ukrainian speech and 811,000 characters cost $8.10 from 27 September to 20 October 2026, and $8.11 by this table. 611
characters are fewer than 390 tokens, so the fixed term also carries some other per-request charge the bill does not
itemize; either way about 80% of the estimate is per request, so fewer, longer batches cut the bill most. Soniox has no usage API, so the ledger is an estimate, not the bill.

## The report

`GET /api/v1/usage?from=&to=` (admin token; [API reference](../reference/api.md)) lists every local day of the range
(the user's time zone, `user.timeZone`; by default the month so far), the month to date and its forecast, and the budget.
Feature flag `usage` in `/api/v1/info` features.

**Forecast**: the month to date plus the mean cost of the last 7 worn days before today (a worn day has any request;
today is left out because it is not over), times the days left after today. It assumes the pendant is worn every
remaining day, so it errs high. With no worn day in the last 62 days, it is the month to date.

## Budget

- `stt.monthlyBudgetUsd`: the budget per calendar month in the user's time zone; empty is no limit.
- `stt.budgetWarningPercent` (default 80) and `stt.budgetModel` (default `parakeet`).

Before each request the server reads the month's spend. Once it is at or past the budget, the batch goes to
`stt.budgetModel` instead of `stt.model`: it is transcribed, never skipped or dropped, its ledger row has
`over_budget`, and `transcription_batches.over_budget` marks it to transcribe again with the usual engine later (its
speech audio stays for `audio.retentionDays`). When the budget model is the usual model nothing changes.

When a recorded request takes the spend to the warning line or the budget, the server records it in `usage_alerts`, once
per month, kind and budget (raising the budget lets the next crossing warn again), and publishes `usage.budget.warning`
or `usage.budget.reached` in the same transaction; webhooks with `*` get them.

## Not here

Transcribing over-budget batches again, model tokens, storage, and the app's screen. The app needs: the `usage` feature
flag, `GET /api/v1/usage` for the chart (days' `costUsd` and `audioMs`) and the month card (`month.costUsd`,
`month.forecast.costUsd`, `budget.state`), and the four `stt.*` settings, which `GET /api/v1/settings` already lists.
