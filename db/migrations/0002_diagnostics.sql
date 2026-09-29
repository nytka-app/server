-- Link, queue and upload health samples from the app (no audio, no transcripts). The whole sample
-- object stays in payload, so the app can add fields without a migration.
create table diagnostics (
    id          uuid        primary key,
    at          timestamptz not null,
    received_at timestamptz not null,
    payload     jsonb       not null
);

create index diagnostics_at on diagnostics (at);
