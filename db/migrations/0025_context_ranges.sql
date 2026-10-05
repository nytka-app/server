set local lock_timeout = '5s';

-- docs/specs/speech-kind.md, Context from the phone. A stretch in which the owner's phone played sound through its
-- loudspeaker (media) or was in a call (call): a kind, the route the sound took and two times, never an app, a title
-- or a number. The id is the app's, so a retried upload is a no-op. A range is at most 12 hours; retention deletes
-- ranges that ended before the audio cutoff, since they only help the guess a conversation gets.
create table context_ranges (
    id          uuid        primary key,
    kind        text        not null check (kind in ('media', 'call')),
    route       text        not null check (route in ('speaker', 'earpiece', 'headset', 'bluetooth', 'other')),
    started_at  timestamptz not null,
    ended_at    timestamptz not null,
    received_at timestamptz not null,
    constraint context_ranges_ends_after_start check (ended_at >= started_at),
    constraint context_ranges_at_most_12h check (ended_at - started_at <= interval '12 hours')
);

create index context_ranges_started_at on context_ranges (started_at);
create index context_ranges_ended_at on context_ranges (ended_at);
