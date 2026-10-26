set local lock_timeout = '5s';

-- "Earlier" links (Memoket-inspired, redesign §4c): for a conversation, the earlier conversations it is about, each with the
-- moment in it that matches best. Derived from the vectors, shared people and shared rare tags, and recomputed whenever the
-- conversation's vector or tags change. A link is a lead for the reader: nothing reads it to make a fact.
create table related_moments (
    conversation_id uuid        not null references conversations (id) on delete cascade,
    earlier_id      uuid        not null references conversations (id) on delete cascade,
    segment_id      bigint      null references segments (id) on delete set null,
    at              timestamptz not null,
    score           real        not null,
    similarity      real        not null,
    tags            text[]      not null,
    person_ids      uuid[]      not null,
    computed_at     timestamptz not null,
    primary key (conversation_id, earlier_id)
);

create index related_moments_earlier on related_moments (earlier_id);

-- true: the links are to work out (again). Every conversation starts so; a new vector or a change of tags sets it again.
alter table conversations add column related_stale boolean not null default true;

create index conversations_related_stale on conversations (id) where related_stale;

create function nytka_related_stale_on_vector() returns trigger
language plpgsql
as $$
begin
    if new.embedded and not old.embedded then
        new.related_stale := true;
    end if;
    return new;
end
$$;

create trigger conversations_related_stale
    before update of embedded on conversations
    for each row execute function nytka_related_stale_on_vector();

create function nytka_related_stale_on_tags() returns trigger
language plpgsql
as $$
begin
    update conversations set related_stale = true
    where id in (select conversation_id from changed) and not related_stale;
    return null;
end
$$;

create trigger conversation_tags_inserted_related_stale
    after insert on conversation_tags referencing new table as changed
    for each statement execute function nytka_related_stale_on_tags();

create trigger conversation_tags_deleted_related_stale
    after delete on conversation_tags referencing old table as changed
    for each statement execute function nytka_related_stale_on_tags();

create trigger conversation_tags_updated_related_stale
    after update on conversation_tags referencing new table as changed
    for each statement execute function nytka_related_stale_on_tags();
