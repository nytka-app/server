set local lock_timeout = '5s';

-- Identity from context (epic nytka-app/server#296, A6). A call range may carry who was on the other end: contact is the display
-- name the phone showed, contact_hash a SHA-256 of the normalised number as 64 lower-case hex characters (the number itself never
-- reaches the server). Only a call takes either, and a hash needs its name.
alter table context_ranges
    add column contact      text null check (contact is null or length(contact) between 1 and 80),
    add column contact_hash text null check (contact_hash is null or contact_hash ~ '^[0-9a-f]{64}$');
alter table context_ranges
    add constraint context_ranges_contact_call check ((contact is null and contact_hash is null) or (kind = 'call' and contact is not null)) not valid;
alter table context_ranges validate constraint context_ranges_contact_call;

-- A name for a track from context: kind 'call' (the other voice of a phone call) or 'meeting' (an attendee of a calendar event).
-- name is the contact's or attendee's display name; person_id is the person with that name when one exists, else null and
-- accepting creates the person. similarity holds the confidence (0.9 for a call, 0.6 for a meeting) and evidence the range or
-- the event it came from. Neither kind is ever applied on its own.
-- Both dropped checks are replaced by wider ones below, in the same migration, so no guarantee is lost.
-- squawk-ignore ban-drop-constraint
alter table track_suggestions drop constraint track_suggestions_kind_check;
-- squawk-ignore ban-drop-constraint
alter table track_suggestions drop constraint track_suggestions_check;
alter table track_suggestions
    add column name text null check (name is null or length(name) between 1 and 80),
    add column evidence jsonb null;
alter table track_suggestions
    add constraint track_suggestions_kind_check check (kind in ('person', 'track', 'call', 'meeting')) not valid,
    add constraint track_suggestions_shape check (
        (kind = 'person' and person_id is not null and other_track_id is null)
        or (kind = 'track' and other_track_id is not null and person_id is null)
        or (kind in ('call', 'meeting') and other_track_id is null and name is not null)) not valid;
alter table track_suggestions validate constraint track_suggestions_kind_check;
alter table track_suggestions validate constraint track_suggestions_shape;

-- One name per track, rejected ones included, so a rejected name is not offered again. The partial index covers no existing row
-- (no row has kind call or meeting yet), so it builds empty.
-- squawk-ignore require-concurrent-index-creation
create unique index track_suggestions_name on track_suggestions (track_id, lower(name)) where kind in ('call', 'meeting');

-- When the day was last named, to queue the days whose tracks were rebuilt or whose call ranges arrived since.
create table track_name_runs (
    day    date        primary key,
    ran_at timestamptz not null
);
