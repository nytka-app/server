set local lock_timeout = '5s';

-- Second-brain redesign, step 4. auto: the tag was applied by enrichment rather than by the owner; only such a link is
-- retired when its tag stays unused.
alter table conversation_tags add column auto boolean not null default false;

-- Other spellings of a tag (a synonym, the same word in another language): a proposed name equal to an alias is applied
-- as its tag. Goes with the tag.
create table tag_aliases (
    alias      text        primary key check (length(alias) between 1 and 32),
    tag_id     uuid        not null references tags (id) on delete cascade,
    created_at timestamptz not null
);

create index tag_aliases_tag on tag_aliases (tag_id);

-- One row per local date the nightly tidy ran (merged synonyms, retired one-off tags), so it runs once a day.
create table tag_tidy_runs (
    local_date date        primary key,
    merged     int         not null,
    retired    int         not null,
    failed     boolean     not null default false,
    created_at timestamptz not null
);
