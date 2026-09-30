-- Omi import (docs/specs/v0.7.md). The columns below take a lock on hot tables; give up rather than
-- queue behind a long transaction.
set local lock_timeout = '5s';

-- source is where a conversation came from: 'nytka' (captured here) or 'omi' (imported from an Omi
-- export). external_id is the id the source gave it, so an import can tell what it already holds.
alter table conversations
    add column source      text not null default 'nytka' check (source in ('nytka', 'omi')),
    add column external_id text null;

-- Nytka's own conversations have no external_id, and nulls never collide.
create unique index conversations_external on conversations (source, external_id);

-- An imported segment has no transcription batch.
alter table segments alter column batch_id drop not null;

alter table memories drop constraint memories_source_check;
alter table memories add constraint memories_source_check check (source in ('ai', 'user', 'omi'));
