set local lock_timeout = '5s';

-- Golden labels (epic nytka-app/server#296, A2): on the days in eval.goldenDays the owner says who spoke each line (kind
-- 'speaker') and whether two adjacent lines belong to one situation (kind 'boundary'), and every attribution and boundary change
-- is scored against the answers. An answer is a label only: it never changes segments, people or voiceprints.
-- segment_id is the line asked about (for a boundary, the later of the two lines); other_segment_id is the line a 'same-as'
-- answer names, or the earlier line of a boundary. There is no foreign key to segments: a paid pass replaces lines, and an
-- answer about one that is gone must stay readable as a record. client_id makes a retried answer a no-op.
create table golden_labels (
    kind             text        not null check (kind in ('speaker', 'boundary')),
    segment_id       bigint      not null,
    answer           text        not null,
    person_id        uuid        null references people (id) on delete set null,
    other_segment_id bigint      null,
    item_id          bigint      null references labeling_items (id) on delete set null,
    client_id        uuid        null unique,
    answered_at      timestamptz not null,
    primary key (kind, segment_id)
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index golden_labels_other on golden_labels (other_segment_id) where other_segment_id is not null;

-- The two golden kinds are labelling tasks of their own, and their items have one reason: they are asked longest speech first.
-- The new check below is a superset and is validated in this migration.
-- squawk-ignore ban-drop-constraint
alter table labeling_items drop constraint labeling_items_task_check;
alter table labeling_items add constraint labeling_items_task_check
    check (task in ('device', 'voice', 'quiet', 'transcript', 'overlap', 'speaker', 'boundary')) not valid;
alter table labeling_items validate constraint labeling_items_task_check;
-- As above, a superset.
-- squawk-ignore ban-drop-constraint
alter table labeling_items drop constraint labeling_items_reason_check;
alter table labeling_items add constraint labeling_items_reason_check
    check (reason in ('uncertain', 'random', 'disagree', 'repeat', 'top', 'golden')) not valid;
alter table labeling_items validate constraint labeling_items_reason_check;

-- The golden scores of the system as it was when the row was measured (nightly quality job); null on a backfilled day and
-- while no golden label exists. The counts behind each score, so the share and the sample size both stay readable:
-- golden_correct of golden_lines answered speaker lines match the system; golden_fragmentation is the mean distinct tracks per
-- golden person per day over golden_persons such person-days; the boundary F1 comes from true and false positives and false
-- negatives over the golden_boundaries answered boundaries.
alter table quality_days
    add column golden_lines         int              null,
    add column golden_correct       int              null,
    add column golden_persons       int              null,
    add column golden_fragmentation double precision null,
    add column golden_boundaries    int              null,
    add column golden_boundary_tp   int              null,
    add column golden_boundary_fp   int              null,
    add column golden_boundary_fn   int              null;
