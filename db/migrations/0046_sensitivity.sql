set local lock_timeout = '5s';

-- Second-brain redesign: what a conversation or an item is about when it is private. none, health, therapy, intimate or
-- money, as the model judged it; null until a summary or an extraction run classified it. Readers that are not trusted
-- (MCP, webhooks, digests, exports, read tokens) leave out the classes privacy.hide names.
alter table conversations add column sensitivity text null check (sensitivity in ('none', 'health', 'therapy', 'intimate', 'money'));
alter table tasks add column sensitivity text null check (sensitivity in ('none', 'health', 'therapy', 'intimate', 'money'));
alter table notes add column sensitivity text null check (sensitivity in ('none', 'health', 'therapy', 'intimate', 'money'));
alter table memories add column sensitivity text null check (sensitivity in ('none', 'health', 'therapy', 'intimate', 'money'));

-- A token or a webhook the owner trusted with sensitive content, set when it is made. Existing ones are not.
alter table api_tokens add column sensitive boolean not null default false;
alter table webhooks add column sensitive boolean not null default false;
