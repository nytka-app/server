set local lock_timeout = '5s';

-- A failed batch that still holds its WAV is transcribed again: the scheduler on a doubling wait, up to a cap of rounds
-- counted here, and POST /api/v1/transcriptions/retry at once. The partial index keeps the scheduler's scan off done rows.
alter table transcription_batches add column retries int not null default 0;

create index transcription_batches_failed on transcription_batches (finished_at) where status = 'failed';
