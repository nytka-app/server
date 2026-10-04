set local lock_timeout = '5s';

-- docs/specs/people.md, Layer 1. A name the model read in a conversation for a voice that has none.
-- Nothing here changes a label: accepting a row writes person_voices or segments.person_id.
-- target: speaker (key speaker_id), label (the provider's label within one batch; its segments are in
-- segment_ids) or group (a voice group; the group_id constraint arrives with the groups).
create table name_suggestions (
    id                  uuid        primary key,
    conversation_id     uuid        not null references conversations (id) on delete cascade,
    target              text        not null check (target in ('speaker', 'group', 'label')),
    speaker_id          text        null,
    group_id            uuid        null,
    segment_ids         bigint[]    not null default '{}',
    name                text        not null check (length(name) between 1 and 80),
    person_id           uuid        null references people (id) on delete cascade,
    evidence_segment_id bigint      not null references segments (id) on delete cascade,
    confidence          real        not null,
    status              text        not null default 'pending' check (status in ('pending', 'accepted', 'rejected')),
    created_at          timestamptz not null,
    decided_at          timestamptz null
);

-- One row per target and name, rejected ones included, so a rejected name is never offered again for that
-- voice. A label target is identified by its lowest segment id.
create unique index name_suggestions_target_name on name_suggestions (
    target, (coalesce(speaker_id, group_id::text, segment_ids[1]::text)), lower(name));

create index name_suggestions_pending on name_suggestions (created_at desc) where status = 'pending';
create index name_suggestions_conversation on name_suggestions (conversation_id);
