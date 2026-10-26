set local lock_timeout = '5s';

-- Two clean-ups after the first run of topic pages and rollups.
-- A rate limit (429) from the model endpoint counted as a failed fold, so most pages were parked after three of them; give
-- every page its tries back (a 429 now waits and is no failure).
update topics set failures = 0 where failures > 0;

-- The scheduler queued the year before the first recording, which queued its months and weeks: rows locked with nothing in them.
-- Nothing is lost; rollups of periods with data are made as before.
delete from rollups where locked and have = 0 and digests = 0 and conversations = 0;
