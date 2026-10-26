set local lock_timeout = '5s';

-- Topic pages (Memoket-inspired, redesign §4c): one evolving view per tag or per named person that recurs. Which conversations
-- belong is read live (the tag's conversations, the conversations the person speaks in); only the running summary is stored,
-- with the conversations already folded into it, so a new conversation costs one model call and nothing is read twice.
create table topics (
    id         uuid        primary key,
    kind       text        not null check (kind in ('tag', 'person')),
    tag_id     uuid        null unique references tags (id) on delete cascade,
    person_id  uuid        null unique references people (id) on delete cascade,
    summary    text        null,
    failures   int         not null default 0,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    check ((kind = 'tag') = (tag_id is not null) and (kind = 'person') = (person_id is not null))
);

-- The conversations a topic's summary has read. A conversation that leaves the topic (a tag removed) stays folded: the summary
-- is a running account, not a view, and the next fold may say what changed.
create table topic_folds (
    topic_id        uuid        not null references topics (id) on delete cascade,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    folded_at       timestamptz not null,
    primary key (topic_id, conversation_id)
);
