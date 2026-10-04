-- People and their facts in search (docs/specs/people.md, Search; plan task P-5).
--
-- Same design as 0007: nullable plain `search` columns, no generated column, so writing a person or a fact
-- never evaluates the `nytka` configuration; the indexer in the server fills them (null means "not indexed
-- yet"). Weights: a person's name A, a fact B. A renamed person or an edited fact is indexed again.

-- The columns below take a lock on hot tables; give up rather than queue behind a long transaction.
set local lock_timeout = '5s';

alter table people add column search tsvector null;
alter table person_facts add column search tsvector null;

create index people_search on people using gin (search);
create index person_facts_search on person_facts using gin (search);

-- What the indexer still has to do, in id order.
create index people_unindexed on people (id) where search is null;
create index person_facts_unindexed on person_facts (id) where search is null;

-- nytka_search_stale() is 0007's: it only clears the vector.
create trigger people_search_stale
    before update of name on people
    for each row execute function nytka_search_stale();

create trigger person_facts_search_stale
    before update of text on person_facts
    for each row execute function nytka_search_stale();

-- 0007's function with the two new tables in the step that clears every vector when the mapping changes.
create or replace function nytka_search_setup(dictionary text default 'simple') returns text
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
        update people set search = null where search is not null;
        update person_facts set search = null where search is not null;
    end if;

    return case when loaded then 'uk' else 'simple' end;
end
$$;
