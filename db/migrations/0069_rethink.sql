set local lock_timeout = '5s';

-- Nightly re-think (epic nytka-app/server#296, A10). A derived layer (tracks, links, names, situations, enrichment) is made by a
-- model, a prompt or rules, and settings; a row of derivation_versions is one such combination, as four hashes. A layer's
-- current version is its newest row (the highest id), and the server adds a row whenever any hash changes: model_hash is the model
-- that made the rows, prompt_hash the prompt text or, for a layer with no model call, the layer's rules revision, settings_hash the
-- settings it reads, upstream_hash the current versions of the layers its rows follow. A change in a layer therefore reaches every
-- layer that follows it.
create table derivation_versions (
    id            bigint      generated always as identity primary key,
    layer         text        not null,
    model_hash    text        not null,
    prompt_hash   text        not null,
    settings_hash text        not null,
    upstream_hash text        not null,
    created_at    timestamptz not null
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index derivation_versions_layer on derivation_versions (layer, id desc);

-- Every derived row records the version that made it. The ids are not foreign keys: a version is never deleted, and a row written
-- before this migration has none, which counts as the layer's first version. A day is stale for a layer when a row of the day carries
-- an older version than the layer's current one.
alter table speaker_tracks add column derivation_id bigint null;
alter table track_links add column derivation_id bigint null;
alter table track_link_runs add column derivation_id bigint null;
alter table track_name_runs add column derivation_id bigint null;
alter table situations add column derivation_id bigint null, add column ai_derivation_id bigint null;

-- The spend and the work of each night, one row per layer and day queued for a rebuild: usd is the estimate reserved when the row was
-- written (a model endpoint answers in text, so the cost is the characters sent and received at the price table's rate). A row is
-- also what keeps a layer and day from being queued twice in one night. night is the local date on which the quiet hours began.
create table rethink_days (
    night     date          not null,
    layer     text          not null,
    day       date          not null,
    usd       numeric(12,6) not null check (usd >= 0),
    queued_at timestamptz   not null,
    primary key (night, layer, day)
);

-- How a night ended: idle (nothing stale), capped (the next day would pass a cap) or settled (all that was left had been queued
-- already). A night with no row is still open.
create table rethink_nights (
    night       date        primary key,
    outcome     text        not null check (outcome in ('idle', 'capped', 'settled')),
    finished_at timestamptz not null
);
