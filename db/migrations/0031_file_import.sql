-- File imports (Plane NYTKA-69): transcript files (SRT, VTT, TXT) land as conversations with
-- source 'import', so AssignAsync's 'nytka' match never merges them with live captures. Audio file
-- imports reuse the pendant's own chunk pipeline and need no new source value.
set local lock_timeout = '5s';

alter table conversations drop constraint conversations_source_check;
alter table conversations add constraint conversations_source_check check (source in ('nytka', 'omi', 'import'));
