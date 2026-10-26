set local lock_timeout = '5s';

-- Several wearer voiceprints instead of one running mean (NYTKA-159): the wearer's voice is not one
-- cluster (a cold, the pendant moved), so matching keeps several centroids and learning opens or
-- merges them instead of blending everything into voice_profile.centroid. Seeded from the existing
-- row so nothing already learned is lost; centroid and centroid_count then have no reader left.
create table voice_profile_prints (
    id          bigint      generated always as identity primary key,
    profile_id  smallint    not null references voice_profile (id) on delete cascade,
    embedding   bytea       not null,
    line_count  int         not null,
    created_at  timestamptz not null,
    updated_at  timestamptz not null
);

create index voice_profile_prints_profile on voice_profile_prints (profile_id);

insert into voice_profile_prints (profile_id, embedding, line_count, created_at, updated_at)
select id, centroid, centroid_count, enrolled_at, updated_at from voice_profile;

alter table voice_profile
    drop column centroid,
    drop column centroid_count;
