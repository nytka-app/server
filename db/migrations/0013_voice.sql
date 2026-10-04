set local lock_timeout = '5s';

-- The wearer's voiceprint (docs/specs/your-voice.md). One row at most. A vector is 192 little-endian
-- float4s, 768 bytes; model is the SHA-256 of the model file that made it. enrolled is the mean of the
-- enrollment windows, centroid the voiceprint after learning; applied_threshold is the
-- voice.userThreshold the stored verdicts follow.
create table voice_profile (
    id                smallint    primary key check (id = 1),
    model             text        not null,
    enrolled          bytea       not null,
    enrolled_count    int         not null,
    centroid          bytea       not null,
    centroid_count    int         not null,
    applied_threshold real        null,
    enrolled_at       timestamptz not null,
    updated_at        timestamptz not null
);

-- A segment's fingerprint lives only as long as its batch's speech audio: retention deletes it with
-- the audio, and the similarity and verdict on the segment stay.
create table segment_fingerprints (
    segment_id  bigint      primary key references segments (id) on delete cascade,
    batch_id    bigint      not null references transcription_batches (id) on delete cascade,
    model       text        not null,
    fingerprint bytea       not null,
    created_at  timestamptz not null
);

create index segment_fingerprints_batch on segment_fingerprints (batch_id);

-- voice_checked: Nytka looked at the segment (a checked segment too short or too long has no
-- similarity and no verdict). voice_is_user is Nytka's verdict; is_user_manual the wearer's own mark.
-- is_user stays the provider's.
alter table segments
    add column voice_checked    boolean not null default false,
    add column voice_similarity real    null,
    add column voice_is_user    boolean null,
    add column is_user_manual   boolean null;
