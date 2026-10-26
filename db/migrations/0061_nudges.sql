set local lock_timeout = '5s';

-- Nudges (NYTKA-148): what the app is told without asking. A nudge is made once, on the poll that first sees its cause, and keeps its
-- id and text; a repeat poll returns the same row. at is when it may be shown (a nudge made in quiet hours waits for them to end).
-- subject_id is the task a commitment-due nudge is about, so a reader can check the task is still open and visible.
create table nudges (
    id         text        primary key,
    kind       text        not null check (kind in ('commitment-due', 'recap', 'insight-unlocked', 'deviation')),
    subject_id uuid        null,
    title      text        not null,
    body       text        not null,
    deep_link  text        not null,
    at         timestamptz not null,
    created_at timestamptz not null
);

create index nudges_at on nudges (at);
