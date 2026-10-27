set local lock_timeout = '5s';

-- Scenes (NYTKA-149): the segment ids where the enrichment model marked a topic shift in a conversation over 20 minutes,
-- each the first line of a new topic. A scene of the Timeline (timeline.scenes) starts at a scene-starting day event
-- (events, 0059) or at one of these lines; scenes are derived on read, conversations stay as stored. Empty for a
-- conversation the model did not split, or one summarized before this column existed.
alter table conversations add column topic_boundaries bigint[] not null default '{}';
