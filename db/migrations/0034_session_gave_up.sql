set local lock_timeout = '5s';

-- A session whose stored chunks ProcessSessionHandler cannot read fails every attempt; once it gives
-- up, this stops the scheduler queuing it again every tick forever.
alter table capture_sessions add column gave_up_at timestamptz null;
