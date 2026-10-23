set local lock_timeout = '5s';

-- NYTKA-82. The summary template that wrote ai_summary: default, meeting, lecture, doctor or call. Null before this column, and
-- for a conversation too short for a template.
alter table conversations add column ai_template text null;
