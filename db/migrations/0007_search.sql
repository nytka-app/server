-- Full-text search over transcripts, titles, summaries and memories (docs/specs/v0.4.md, Search).
--
-- The text search configuration `nytka` sends ASCII words through english_stem and Cyrillic words
-- through the Ukrainian Hunspell dictionary `nytka_uk`, then `simple`. The dictionary needs two
-- files Postgres reads from $SHAREDIR/tsearch_data (uk_ua.dict, uk_ua.affix); this migration must
-- still apply without them, so nytka_search_setup() loads the dictionary when it can and falls back
-- to `simple` (with a WARNING) when it cannot. The server calls the function at every start.

-- The columns below take a lock on hot tables; give up rather than queue behind a long transaction.
set local lock_timeout = '5s';

-- Returns 'uk' when Cyrillic words go through the dictionary, 'simple' when they match exactly. When
-- the mapping changes it rebuilds the generated `search` columns that exist, which rewrites those
-- tables: run `select nytka_search_setup();` by hand after mounting or removing the files.
create function nytka_search_setup() returns text
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
        raise warning 'Ukrainian dictionary not loaded: %; Cyrillic words match exactly', failure;
    end if;

    if changed then
        if exists (select 1 from information_schema.columns
                   where table_schema = current_schema() and table_name = 'segments' and column_name = 'search') then
            alter table segments alter column search set expression as (to_tsvector('nytka', text));
        end if;
        if exists (select 1 from information_schema.columns
                   where table_schema = current_schema() and table_name = 'conversations' and column_name = 'search') then
            alter table conversations alter column search set expression as (
                setweight(to_tsvector('nytka', coalesce(title, ai_title, '')), 'A')
                || setweight(to_tsvector('nytka', coalesce(ai_summary, '')), 'B'));
        end if;
        if exists (select 1 from information_schema.columns
                   where table_schema = current_schema() and table_name = 'memories' and column_name = 'search') then
            alter table memories alter column search set expression as (
                setweight(to_tsvector('nytka', text), 'B'));
        end if;
    end if;

    return case when loaded then 'uk' else 'simple' end;
end
$$;

select nytka_search_setup();

-- Generated and stored: the code that writes these rows does not change. Title is the one the user
-- set, else the generated one (weight A); the summary is weight B; a memory is weight B; a segment
-- has no weight (D). nytka_search_setup() repeats these expressions to rebuild the columns.
alter table segments add column search tsvector
    generated always as (to_tsvector('nytka', text)) stored;

alter table conversations add column search tsvector
    generated always as (
        setweight(to_tsvector('nytka', coalesce(title, ai_title, '')), 'A')
        || setweight(to_tsvector('nytka', coalesce(ai_summary, '')), 'B')) stored;

alter table memories add column search tsvector
    generated always as (setweight(to_tsvector('nytka', text), 'B')) stored;

create index segments_search on segments using gin (search);
create index conversations_search on conversations using gin (search);
create index memories_search on memories using gin (search);
