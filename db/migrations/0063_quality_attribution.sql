set local lock_timeout = '5s';

-- Attribution metrics (epic nytka-app/server#296): the class of every line with text, its speech seconds, and the distinct
-- provider labels of each conversation (one entry per conversation with a line, ascending), so a 7-day p50 and p90 can be
-- pooled from the days. The wearer's lines are wearer_lines. Null on a day measured before this migration.
alter table quality_days
    add column class_person_lines  int              null,
    add column class_label_lines   int              null,
    add column class_media_lines   int              null,
    add column class_none_lines    int              null,
    add column class_wearer_secs   double precision null,
    add column class_person_secs   double precision null,
    add column class_label_secs    double precision null,
    add column class_media_secs    double precision null,
    add column class_none_secs     double precision null,
    add column labels_per_conversation int[]        null;
