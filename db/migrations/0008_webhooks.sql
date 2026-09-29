-- Outgoing webhooks. The secret signs each delivery, so it is stored as plain text; it is shown
-- once, when the webhook is created.
create table webhooks (
    id          uuid        primary key,
    url         text        not null,
    secret      text        not null,
    events      text[]      not null,
    description text        null,
    active      boolean     not null default true,
    created_at  timestamptz not null,
    updated_at  timestamptz not null
);

-- One row per event and webhook. payload is kept only until the delivery ends (delivered or
-- failed); the log keeps the status, never a response body.
create table webhook_deliveries (
    id               uuid        primary key,
    webhook_id       uuid        not null references webhooks (id) on delete cascade,
    event_id         uuid        not null,
    event_type       text        not null,
    payload          jsonb       null,
    status           text        not null default 'pending' check (status in ('pending', 'delivered', 'failed')),
    attempts         int         not null default 0,
    last_status_code int         null,
    last_error       text        null,
    created_at       timestamptz not null,
    delivered_at     timestamptz null
);

create index webhook_deliveries_by_webhook on webhook_deliveries (webhook_id, created_at desc);
