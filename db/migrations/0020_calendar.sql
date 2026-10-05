set local lock_timeout = '5s';

-- docs/specs/people.md, Pre-meeting brief. The events of the next 48 hours of the owner's ICS feed, one row
-- per occurrence (a weekly meeting is one row per week, with the same uid). Rows are replaced by each sync
-- and deleted a day after they end. attendees holds the display names (CN) only, no address.
create table calendar_events (
    uid        text        not null,
    starts_at  timestamptz not null,
    ends_at    timestamptz not null,
    title      text        not null,
    attendees  text[]      not null default '{}',
    fetched_at timestamptz not null,
    primary key (uid, starts_at)
);

create index calendar_events_by_end on calendar_events (ends_at);

-- One brief per occurrence: the model's text and the people it was made from. It goes with its event.
create table briefs (
    id              uuid        primary key,
    event_uid       text        not null,
    event_starts_at timestamptz not null,
    person_ids      uuid[]      not null,
    text            text        not null,
    created_at      timestamptz not null,
    unique (event_uid, event_starts_at),
    foreign key (event_uid, event_starts_at) references calendar_events (uid, starts_at) on delete cascade
);
