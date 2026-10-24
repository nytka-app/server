-- Vector search for Ask and search (NYTKA-66).
--
-- The vectors need pgvector (0.8 or later, for iterative index scans), which the stock postgres images
-- do not have, so this migration does not create the extension or the vector tables: it applies on any
-- Postgres. `nytka_embeddings_setup()` does, at run time, once the server has an embeddings endpoint;
-- it also fixes the vector size, which only the model knows. Without pgvector it answers 'unavailable'
-- and search stays full-text.
--
-- Same shape as 0007: a plain flag says what the background indexer still has to embed, writes never
-- call the model, and a changed title, summary or memory is embedded again.

-- The columns below take a lock on hot tables; give up rather than queue behind a long transaction.
set local lock_timeout = '5s';

alter table segments add column embedded boolean not null default false;
alter table conversations add column embedded boolean not null default false;
alter table memories add column embedded boolean not null default false;

-- What the indexer still has to do, in id order.
create index segments_unembedded on segments (id) where not embedded;
create index conversations_unembedded on conversations (id) where not embedded;
create index memories_unembedded on memories (id) where not embedded;

create function nytka_embedding_stale() returns trigger
language plpgsql
as $$
begin
    new.embedded := false;
    return new;
end
$$;

create trigger segments_embedding_stale
    before update of text on segments
    for each row execute function nytka_embedding_stale();

create trigger conversations_embedding_stale
    before update of title, ai_title, ai_summary on conversations
    for each row execute function nytka_embedding_stale();

create trigger memories_embedding_stale
    before update of text, deleted_at on memories
    for each row execute function nytka_embedding_stale();

-- The model the vectors were made with; one row once set up.
create table embedding_model (
    id         int         primary key check (id = 1),
    model      text        not null,
    dimensions int         not null,
    updated_at timestamptz not null
);

-- Creates pgvector and the three vector tables for `model` and `dimensions`. Returns 'ready', or
-- 'unavailable' (with a WARNING) when pgvector is not installed or may not be created. Another model or
-- size than last time drops the vectors and marks every row to embed again. The server calls it when the
-- embeddings settings change; by hand: `select nytka_embeddings_setup('bge-m3', 1024);`.
create function nytka_embeddings_setup(model text, dimensions int) returns text
language plpgsql
as $$
declare
    known embedding_model%rowtype;
begin
    perform pg_advisory_xact_lock(hashtext('nytka_embeddings_setup'));

    if dimensions is null or dimensions < 1 or dimensions > 2000 then
        raise exception 'Embedding dimensions must be 1 to 2000 for an HNSW index, not %', dimensions;
    end if;

    if not exists (select 1 from pg_extension where extname = 'vector') then
        if not exists (select 1 from pg_available_extensions where name = 'vector') then
            raise warning 'pgvector is not installed in this Postgres; search stays full-text';
            return 'unavailable';
        end if;
        begin
            create extension vector;
        exception when others then
            raise warning 'pgvector could not be created: %; search stays full-text', sqlerrm;
            return 'unavailable';
        end;
    end if;

    select * into known from embedding_model where id = 1;
    if found and known.model = nytka_embeddings_setup.model and known.dimensions = nytka_embeddings_setup.dimensions
       and to_regclass('segment_embeddings') is not null then
        return 'ready';
    end if;

    drop table if exists segment_embeddings, conversation_embeddings, memory_embeddings;
    execute format(
        'create table segment_embeddings (
             segment_id bigint primary key references segments (id) on delete cascade,
             embedding  vector(%s) not null)', dimensions);
    execute format(
        'create table conversation_embeddings (
             conversation_id uuid primary key references conversations (id) on delete cascade,
             embedding       vector(%s) not null)', dimensions);
    execute format(
        'create table memory_embeddings (
             memory_id uuid primary key references memories (id) on delete cascade,
             embedding vector(%s) not null)', dimensions);
    create index segment_embeddings_hnsw on segment_embeddings using hnsw (embedding vector_cosine_ops);
    create index conversation_embeddings_hnsw on conversation_embeddings using hnsw (embedding vector_cosine_ops);
    create index memory_embeddings_hnsw on memory_embeddings using hnsw (embedding vector_cosine_ops);

    update segments set embedded = false where embedded;
    update conversations set embedded = false where embedded;
    update memories set embedded = false where embedded;

    insert into embedding_model (id, model, dimensions, updated_at)
    values (1, nytka_embeddings_setup.model, nytka_embeddings_setup.dimensions, now())
    on conflict (id) do update set model = excluded.model, dimensions = excluded.dimensions, updated_at = excluded.updated_at;
    return 'ready';
end
$$;
