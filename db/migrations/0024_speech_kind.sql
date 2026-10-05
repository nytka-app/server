set local lock_timeout = '5s';

-- docs/specs/speech-kind.md. Every line gets a guess (person, media, call or unsure) with the score that made it
-- (0 to 1, how much it looks like media) and the signals that moved it; the owner's mark is speech_manual. speech_kind
-- is what applies: the mark, else with speech.mode 'on' the guess (unsure read as person), else null. Every reader asks
-- that one column. speech_version is the classifier's version, so a newer one guesses again.
alter table segments
    add column speech_guess   text     null check (speech_guess in ('person', 'media', 'call', 'unsure')),
    add column speech_score   real     null check (speech_score between 0 and 1),
    add column speech_signals text[]   null,
    add column speech_version smallint null,
    add column speech_manual  text     null check (speech_manual in ('person', 'media', 'call')),
    add column speech_kind    text     null check (speech_kind in ('person', 'media', 'call'));

create index segments_speech_media on segments (conversation_id) where speech_kind = 'media';

-- One row: the speech.mode and speech.mediaThreshold the stored guesses and kinds follow, as
-- voice_profile.applied_threshold does for the wearer's verdicts. The scheduler queues apply-speech when they differ.
create table speech_state (
    id                smallint    primary key check (id = 1),
    applied_mode      text        not null check (applied_mode in ('off', 'shadow', 'on')),
    applied_threshold real        not null,
    updated_at        timestamptz not null
);

insert into speech_state (id, applied_mode, applied_threshold, updated_at) values (1, 'shadow', 0.8, now());
