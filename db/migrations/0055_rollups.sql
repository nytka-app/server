set local lock_timeout = '5s';

-- Summaries over time (redesign §4c): a week from its daily digests, a month from its weeks, a year from its months, all time
-- from the years, and a week of one topic from the summaries of its conversations. Never from transcripts, so each costs one
-- model call whatever the period holds. changes is what differs from the period before; a period with too little to say is
-- stored locked, with no summary and no call.
create table rollups (
    id            uuid        primary key,
    period        text        not null check (period in ('week', 'month', 'year', 'all')),
    start_date    date        not null,
    end_date      date        not null check (end_date > start_date),
    topic_id      uuid        null references topics (id) on delete cascade,
    locked        boolean     not null,
    have          int         not null,
    need          int         not null,
    summary       text        null,
    changes       text[]      not null default '{}',
    digests       int         not null default 0,
    conversations int         not null default 0,
    created_at    timestamptz not null,
    updated_at    timestamptz not null,
    check (locked = (summary is null)),
    check (topic_id is null or period = 'week')
);

create unique index rollups_period_start on rollups (period, start_date) where topic_id is null;
create unique index rollups_topic_week on rollups (topic_id, start_date) where topic_id is not null;
