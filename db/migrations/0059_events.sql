set local lock_timeout = '5s';

-- Day events (NYTKA-149): a day's structure derived from what Nytka already stores, never from new input. day is the
-- owner's local date the event belongs to. at is the capture-time instant it happened. detail holds a kind's own small
-- facts (silence's minutes, a now-playing category); '{}' when a kind needs none. source names what the event came
-- from (segments, context or speech), so a run that recomputes a day never has to guess where a row began.
-- Recomputing a day deletes and rewrites its rows in one transaction (DayEventStore.SaveDayAsync), so this table is
-- always a pure function of the data it reads: never edited by hand, never merged across runs.
create table events (
    id              uuid        primary key,
    day             date        not null,
    kind            text        not null check (kind in (
                                     'person_joined', 'person_left', 'media_started', 'media_stopped',
                                     'call_started', 'call_ended', 'silence', 'alone')),
    at              timestamptz not null,
    conversation_id uuid        null references conversations (id) on delete cascade,
    person_id       uuid        null references people (id) on delete set null,
    voice_label     text        null,
    detail          jsonb       not null default '{}',
    source          text        not null,
    computed_at     timestamptz not null
);

create index events_day on events (day, at);
create index events_conversation on events (conversation_id);
