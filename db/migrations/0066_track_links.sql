set local lock_timeout = '5s';

-- Links by meaning (epic nytka-app/server#296, A7): the link-speakers job reads one scene of a conversation with a letter per
-- voice unit (the provider's label within one request) and the model says which letters are one person and which are two. A
-- row is one such answer. segments and other_segments are the lines of the two letters, evidence the lines the model cited; the
-- ids are not foreign keys, so a paid pass that replaces lines leaves a link that no longer matches any line, and the rebuild
-- ignores it. day is the local day of the scene: a link never joins lines of two days. The rebuild of a day follows only the
-- rows of confidence 0.8 or more, and only while tracks.meaning is on. source has one value now; the owner's own answers stay
-- in track_answers.
create table track_links (
    id               uuid        primary key,
    day              date        not null,
    conversation_id  uuid        not null references conversations (id) on delete cascade,
    scene_started_at timestamptz not null,
    kind             text        not null check (kind in ('same', 'different')),
    source           text        not null check (source in ('meaning')),
    confidence       real        not null check (confidence >= 0 and confidence <= 1),
    segments         bigint[]    not null,
    other_segments   bigint[]    not null,
    evidence         bigint[]    not null,
    created_at       timestamptz not null
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index track_links_day on track_links (day);
-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index track_links_scene on track_links (conversation_id, scene_started_at);

-- One row per scene the model was asked about, or skipped, or gave up on: what stops the job from being queued again, and the cost
-- of each call. A model endpoint answers in text, so the cost is the characters sent and received (prompt_chars, answer_chars;
-- both 0 when no call was made); letters is the number of voice units the prompt named, pairs the number of links kept.
create table track_link_runs (
    conversation_id  uuid        not null references conversations (id) on delete cascade,
    scene_started_at timestamptz not null,
    day              date        not null,
    status           text        not null check (status in ('done', 'skipped', 'failed')),
    letters          int         not null,
    pairs            int         not null,
    prompt_chars     int         not null,
    answer_chars     int         not null,
    at               timestamptz not null,
    primary key (conversation_id, scene_started_at)
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index track_link_runs_day on track_link_runs (day);
