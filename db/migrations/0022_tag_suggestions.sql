set local lock_timeout = '5s';

-- docs/specs/tags.md, Proposed tags. A tag the model proposed for a conversation (person_id null) or, later, for a
-- person. The name is text, not a tag id: a proposal neither keeps a tag alive nor needs one to exist, and
-- accepting finds or creates the tag through TagStore. Nothing here links a tag: only accepting a row does.
-- conversation_id is where the proposal came from, so the inbox can open it.
create table tag_suggestions (
    id              uuid        primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    person_id       uuid        null references people (id) on delete cascade,
    name            text        not null check (length(name) between 1 and 32),
    status          text        not null default 'pending' check (status in ('pending', 'accepted', 'rejected')),
    created_at      timestamptz not null,
    decided_at      timestamptz null
);

-- One row per item and name, rejected and accepted ones included, so a rejected or removed tag is never
-- proposed again for that item.
create unique index tag_suggestions_item_name on tag_suggestions ((coalesce(person_id, conversation_id)), name);

create index tag_suggestions_pending on tag_suggestions (created_at desc) where status = 'pending';
create index tag_suggestions_conversation on tag_suggestions (conversation_id);
create index tag_suggestions_person on tag_suggestions (person_id) where person_id is not null;
