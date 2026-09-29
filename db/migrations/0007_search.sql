-- Full-text search over transcripts, titles, summaries and memories (docs/specs/v0.4.md, Search).
--
-- The text search configuration `nytka` sends ASCII words through english_stem and everything else
-- through `simple` (exact words, plus prefix matching in the query). Opt-in, the setting
-- `search.dictionary = uk_hunspell` puts the Ukrainian Hunspell dictionary `nytka_uk` in front of
-- `simple` for Cyrillic words; it needs two files Postgres reads from $SHAREDIR/tsearch_data
-- (uk_ua.dict, uk_ua.affix). Postgres loads a dictionary once per session, about 65 MB each, so no
-- ordinary write may touch the configuration: the `search` columns are plain and nullable, and one
-- background indexer in the server, on its own small connection pool, fills them (a row with a null
-- `search` is not searchable yet). This migration applies with or without the files.

-- The columns below take a lock on hot tables; give up rather than queue behind a long transaction.
set local lock_timeout = '5s';

-- Nullable and not generated: writing a segment, a summary or a memory never evaluates `nytka`. Null
-- means "not indexed yet". Weights: title A, summary B, memory B, transcript none (D).
alter table segments add column search tsvector null;
alter table conversations add column search tsvector null;
alter table memories add column search tsvector null;

create index segments_search on segments using gin (search);
create index conversations_search on conversations using gin (search);
create index memories_search on memories using gin (search);

-- What the indexer still has to do, in id order.
create index segments_unindexed on segments (id) where search is null;
create index conversations_unindexed on conversations (id) where search is null;
create index memories_unindexed on memories (id) where search is null;

-- A changed title, summary or memory text is indexed again: the trigger only clears the vector.
create function nytka_search_stale() returns trigger
language plpgsql
as $$
begin
    new.search := null;
    return new;
end
$$;

create trigger conversations_search_stale
    before update of title, ai_title, ai_summary on conversations
    for each row execute function nytka_search_stale();

create trigger memories_search_stale
    before update of text on memories
    for each row execute function nytka_search_stale();

-- `dictionary` is the setting: 'simple' (the default) or 'uk_hunspell'. Returns 'uk' when Cyrillic words
-- go through the dictionary and 'simple' when they match exactly, which is also the answer, with a
-- WARNING, when 'uk_hunspell' is asked for and the files do not load. When the mapping changes every
-- vector was made under the old one, so they are cleared and the indexer makes them again. The server
-- calls it at start, when the setting changes and when indexing fails; by hand:
-- `select nytka_search_setup('uk_hunspell');`.
create function nytka_search_setup(dictionary text default 'simple') returns text
language plpgsql
as $$
declare
    loaded  boolean := false;
    failure text;
    mapped  boolean;
    changed boolean := false;
begin
    -- Two servers starting together must not race to create the configuration.
    perform pg_advisory_xact_lock(hashtext('nytka_search_setup'));

    if not exists (select 1 from pg_ts_config where cfgname = 'nytka' and pg_ts_config_is_visible(oid)) then
        create text search configuration nytka (copy = pg_catalog.simple);
        alter text search configuration nytka
            alter mapping for asciiword, asciihword, hword_asciipart with english_stem;
    end if;

    if dictionary = 'uk_hunspell' then
        begin
            if not exists (select 1 from pg_ts_dict where dictname = 'nytka_uk' and pg_ts_dict_is_visible(oid)) then
                create text search dictionary nytka_uk (template = ispell, dictfile = uk_ua, afffile = uk_ua);
            end if;
            -- Postgres reads the files on first use, so a dictionary that exists proves nothing.
            perform ts_lexize('nytka_uk', 'зустріч');
            loaded := true;
        exception when others then
            failure := sqlerrm;
        end;
    elsif dictionary <> 'simple' then
        raise exception 'Unknown search dictionary: %', dictionary;
    end if;

    select exists (
        select 1 from pg_ts_config_map m join pg_ts_dict d on d.oid = m.mapdict
        where m.mapcfg = 'nytka'::regconfig and d.dictname = 'nytka_uk')
    into mapped;

    if loaded and not mapped then
        alter text search configuration nytka
            alter mapping for word, hword, hword_part with nytka_uk, simple;
        changed := true;
    elsif not loaded then
        if mapped then
            alter text search configuration nytka
                alter mapping for word, hword, hword_part with simple;
            changed := true;
        end if;
        drop text search dictionary if exists nytka_uk;
        if dictionary = 'uk_hunspell' then
            raise warning 'Ukrainian dictionary not loaded: %; Cyrillic words match exactly', failure;
        end if;
    end if;

    if changed then
        update segments set search = null where search is not null;
        update conversations set search = null where search is not null;
        update memories set search = null where search is not null;
    end if;

    return case when loaded then 'uk' else 'simple' end;
end
$$;

select nytka_search_setup();
