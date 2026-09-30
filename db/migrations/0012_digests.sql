set local lock_timeout = '5s';

-- One digest per local day (the user's time zone at the time it was made). body holds highlights,
-- decisions and open questions; a highlight keeps its conversation id without a foreign key, so
-- deleting a conversation never rewrites a digest. A rerun of the scheduled job leaves the row alone;
-- an on-demand run deletes it and inserts a new one.
create table digests (
    id         uuid        primary key,
    local_date date        not null unique,
    headline   text        not null,
    overview   text        not null,
    body       jsonb       not null,
    created_at timestamptz not null
);
