-- Named API tokens. A token is shown once, when it is created; the table keeps its SHA-256 and the
-- last four characters as a hint. The environment's admin token is never a row.
create table api_tokens (
    id           uuid        primary key,
    name         text        not null,
    scope        text        not null check (scope in ('admin', 'read')),
    token_hash   bytea       not null unique,
    hint         text        not null,
    created_at   timestamptz not null,
    last_used_at timestamptz null,
    revoked_at   timestamptz null
);

-- A name is unique among active tokens, ignoring case; revoking a token frees its name.
create unique index api_tokens_active_name on api_tokens (lower(name)) where revoked_at is null;

-- Server settings the app edits, as the strings an environment variable would carry. A key's
-- environment variable, when set, wins over its row; API keys never land here.
create table settings (
    key        text        primary key,
    value      text        not null,
    updated_at timestamptz not null
);
