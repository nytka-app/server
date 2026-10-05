set local lock_timeout = '5s';

-- docs/specs/tags.md. A tag is a normalized name (the server normalizes; the check holds the length only).
-- A tag exists while an item holds it: a statement trigger deletes tags with no link left after any delete
-- on a link table, which covers removing a link, deleting an item (the links cascade) and merging.
create table tags (
    id         uuid        primary key,
    name       text        not null unique check (length(name) between 1 and 32),
    created_at timestamptz not null
);

create table conversation_tags (
    conversation_id uuid        not null references conversations (id) on delete cascade,
    tag_id          uuid        not null references tags (id) on delete cascade,
    created_at      timestamptz not null,
    primary key (conversation_id, tag_id)
);

create index conversation_tags_tag on conversation_tags (tag_id);

create table person_tags (
    person_id  uuid        not null references people (id) on delete cascade,
    tag_id     uuid        not null references tags (id) on delete cascade,
    created_at timestamptz not null,
    primary key (person_id, tag_id)
);

create index person_tags_tag on person_tags (tag_id);

-- skip locked: a tag row an add holds (it upserts the tag before it links) is not deleted under it.
create function delete_unused_tags() returns trigger language plpgsql as $$
begin
    delete from tags where id in (
        select t.id from tags t
        where not exists (select 1 from conversation_tags c where c.tag_id = t.id)
          and not exists (select 1 from person_tags p where p.tag_id = t.id)
        for update skip locked);
    return null;
end
$$;

create trigger conversation_tags_unused after delete on conversation_tags
    for each statement execute function delete_unused_tags();

create trigger person_tags_unused after delete on person_tags
    for each statement execute function delete_unused_tags();
