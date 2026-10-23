set local lock_timeout = '5s';

-- docs/guide/webhooks.md. The shape of a webhook's payloads: 'nytka' (the envelope, never a transcript) or 'omi'
-- (Omi's memory-creation payload, which carries the transcript). The format is chosen when the webhook is created
-- and can be changed later; it never changes a delivery already queued.
alter table webhooks
    add column format text not null default 'nytka' check (format in ('nytka', 'omi'));
