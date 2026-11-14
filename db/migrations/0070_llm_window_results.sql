-- The foreign key below takes a lock on conversations; give up rather than queue behind a long
-- transaction while the server holds locks on hot tables.
set local lock_timeout = '5s';

-- jobs: a retried enrichment or memory job reuses LLM windows that already succeeded (nytka-app/server#203).
-- EnrichConversationHandler and ExtractMemoriesHandler each call the model once per transcript window; a row here is
-- written as soon as a window's call returns, so a crash or a retry reads it back instead of paying for the window
-- again. handler is the job kind ('enrich-conversation' or 'extract-memories'), window_index the window's position,
-- prompt_version the handler's current prompt version (bumped by hand when the system prompt changes in a way the
-- text itself would not), and input_hash a hash of the exact system and user message sent for that window: either one
-- changing is a different key, so a changed prompt or window text is never read back as a stale answer. The run that
-- finally stores its result deletes its own rows; a conversation whose job never finishes is swept by retention.
create table llm_window_results (
    conversation_id uuid        not null references conversations (id) on delete cascade,
    handler         text        not null,
    window_index    int         not null,
    prompt_version  int         not null,
    input_hash      text        not null,
    result          jsonb       not null,
    created_at      timestamptz not null,
    primary key (conversation_id, handler, window_index, prompt_version, input_hash)
);
