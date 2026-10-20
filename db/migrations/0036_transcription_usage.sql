set local lock_timeout = '5s';

-- docs/specs/usage.md. One row per answered transcription request: its batch and conversation, the engine it went to, the
-- speech sent, the text that came back and the cost by the price table of the moment. No foreign keys: money spent stays
-- spent when its conversation is deleted. estimated marks rows filled in from batches done before the ledger existed.
create table transcription_usage (
    id              bigint        generated always as identity primary key,
    batch_id        bigint        not null,
    conversation_id uuid          null,
    at              timestamptz   not null,
    engine          text          not null,
    audio_ms        bigint        not null,
    text_chars      int           not null,
    cost_usd        numeric(12,6) not null,
    estimated       boolean       not null default false,
    over_budget     boolean       not null default false
);

create unique index transcription_usage_batch on transcription_usage (batch_id);
create index transcription_usage_at on transcription_usage (at);

-- A batch sent to stt.budgetModel because the month's budget was spent, to transcribe again with the usual engine later.
alter table transcription_batches add column over_budget boolean not null default false;

-- The month's budget warnings, one per month, kind and budget: raising the budget lets the next crossing warn again.
create table usage_alerts (
    id         uuid          primary key,
    month      date          not null,
    kind       text          not null check (kind in ('warning', 'reached')),
    budget_usd numeric(12,6) not null,
    spent_usd  numeric(12,6) not null,
    at         timestamptz   not null,
    unique (month, kind, budget_usd)
);
