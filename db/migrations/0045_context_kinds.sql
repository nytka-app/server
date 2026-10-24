set local lock_timeout = '5s';

-- Second-brain redesign and research Q3/Q4: more of what the phone knows about the room, still never an app, a title or a
-- number. screen (the screen was on), headphones (sound went to headphones, not into the room), activity (still, walking,
-- running, cycling, vehicle), now-playing (a category of what played, mapped on the phone), gain (the pendant's mic gain
-- level in effect, "0" to "8"). detail holds the activity, the category or the level; null for the other kinds.
alter table context_ranges
    drop constraint context_ranges_kind_check,
    add constraint context_ranges_kind_check
        check (kind in ('media', 'call', 'screen', 'headphones', 'activity', 'now-playing', 'gain')),
    add column detail text null check (detail is null or length(detail) between 1 and 32);
