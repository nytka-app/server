set local lock_timeout = '5s';

-- A bookmark is a moment the wearer marked, by a tap on the pendant or in the app. It holds no conversation
-- id: a conversation shows the bookmarks near its span when it is read, so merges need no cascade and a
-- bookmark outside every conversation stays listed on its own. The id is the client's, so a retry is a no-op.
create table bookmarks (
    id         uuid        primary key,
    at         timestamptz not null,
    note       text        null check (length(note) between 1 and 200),
    source     text        not null check (source in ('pendant', 'app')),
    created_at timestamptz not null
);

create index bookmarks_at on bookmarks (at desc, id desc);
