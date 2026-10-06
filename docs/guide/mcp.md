---
title: "MCP reference"
description: "Connect an AI agent to Nytka over MCP: the endpoint, the token it needs and all twelve read-only tools."
order: 4
section: "Reference"
---

Agents read Nytka over the [Model Context Protocol](https://modelcontextprotocol.io) at
`https://<your server>/mcp`. The source is [`src/Nytka.Server/Mcp/`](../../src/Nytka.Server/Mcp/McpTools.cs).

## Endpoint rules

| Rule | Behavior |
|---|---|
| Transport | Streamable HTTP, no session |
| Methods | `POST` only; `GET` and `DELETE` answer `405` |
| Token | `Authorization: Bearer`, scope `read` or `admin` |
| No or bad token | `401`; a revoked token gets `401` too |
| `Origin` header | `403`, because a browser always sends one |
| OAuth | not supported |

Each rule has a test in
[`McpToolTests.cs`](../../tests/Nytka.Server.Tests/Mcp/McpToolTests.cs) and
[`McpGuardTests.cs`](../../tests/Nytka.Server.Tests/Mcp/McpGuardTests.cs), for example
`A_request_with_an_Origin_header_is_refused_with_403` and `Get_and_delete_answer_405`.

## Make a token

Make a `read` token per client so you can revoke one without touching the rest. The server shows
the token once and stores a SHA-256 of it ([README](../../README.md#tokens-and-scopes)).

```bash title="Create a read token"
curl -X POST https://<address>/api/v1/tokens \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "laptop", "scope": "read"}'
```

The answer holds the token (`nyt_` and 43 characters) in `token`. `DELETE /api/v1/tokens/{id}`
revokes it.

## Connect a client

In Claude Code:

```bash title="Add the server"
claude mcp add --transport http nytka https://<address>/mcp \
  --header "Authorization: Bearer nyt_..."
```

Or in a project's `.mcp.json`, with the token in an environment variable:

```json title=".mcp.json"
{
  "mcpServers": {
    "nytka": {
      "type": "http",
      "url": "https://<address>/mcp",
      "headers": { "Authorization": "Bearer ${NYTKA_TOKEN}" }
    }
  }
}
```

Any client that speaks Streamable HTTP and sends a fixed `Authorization` header connects the same
way. Behind a reverse proxy, pass `/mcp` and that header through unchanged.

## Tools

All twelve tools are read-only (`ReadOnly = true` in the source), declare an output schema and
return structured content plus JSON text. The names below match the `McpServerTool` attributes in
`src/Nytka.Server/Mcp/`.

| Tool | Input |
|---|---|
| `list_conversations` | `since?`, `before?`, `tag?`, `limit?` (1 to 50, 20) |
| `get_conversation` | `id`, `transcript?`, `part?` |
| `list_tasks` | `status?`, `conversationId?`, `before?`, `kind?` (`commitment` by default, `idea`, `all`), `limit?` (1 to 200, 50) |
| `list_notes` | `topic?`, `conversationId?`, `before?`, `limit?` (1 to 200, 50) |
| `list_memories` | `before?`, `limit?` (1 to 200, 50) |
| `list_bookmarks` | `before?`, `beforeId?`, `limit?` (1 to 100, 30) |
| `list_digests` | `before?`, `limit?` (1 to 100, 30) |
| `search` | `query`, `kinds?`, `tag?`, `limit?` (1 to 30, 10) |
| `list_people` | `tag?` |
| `get_person` | `id` or `name` |
| `list_tags` | `query?` |
| `ask` | `question` (1 to 500 characters) |

Defaults are in parentheses. Times are ISO 8601 with an offset, or a date.

### Paging

List tools return `nextBefore`. Pass it as `before` to read the next page. `search` has no
`offset`. `list_bookmarks` also returns `nextBeforeId`.

### Transcripts

`get_conversation` writes one line per segment, in UTC: `[HH:mm:ss] Speaker: text`. It cuts the
transcript at a line boundary after 60,000 characters into parts. `part` picks one (from 1),
`parts` counts them and `truncated` is true while a later part exists. Only the first 20 parts
are read. Pass `transcript: false` for the summary and tasks alone; `transcript` is then null.

### Errors

An unknown id is a tool error ("No such conversation."). A malformed id, time, status, kind or
tag is JSON-RPC error `-32602`. `ask` is a tool error when no model is set or it fails.

The REST endpoints behind the tools, and every other route, are in the
[README API table](../../README.md#api).

> [!WARNING]
> A `read` token reads every transcript. Treat it like the admin token: give each client its own
> and revoke what you no longer use.
