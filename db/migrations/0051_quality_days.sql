set local lock_timeout = '5s';

-- Second-brain redesign: the quality numbers of one local day, measured from stored rows (no model call), so a
-- regression shows up the next morning. Counts, not rates: a reader divides. A later run of the date replaces the row.
-- voice_groups and singleton_groups are a snapshot of the whole table when the row was measured, null for a date
-- measured later than the day after it (the table keeps no history).
create table quality_days (
    local_date               date        primary key,
    conversations            int         not null,
    fragments                int         not null,
    untitled                 int         not null,
    non_empty                int         not null,
    lines                    int         not null,
    wearer_lines             int         not null,
    wearer_conversations     int         not null,
    voice_scored_lines       int         not null,
    unknown_lines            int         not null,
    raw_label_lines          int         not null,
    stored_raw_label_lines   int         not null,
    media_conversations      int         not null,
    media_line_conversations int         not null,
    tagged_conversations     int         not null,
    items_vital              int         not null,
    items_ordinary           int         not null,
    items_noise              int         not null,
    items_unrated            int         not null,
    unconfirmed_memories     int         not null,
    voice_groups             int         null,
    singleton_groups         int         null,
    measured_at              timestamptz not null
);
