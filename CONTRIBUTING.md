# Contributing

Issues and pull requests are welcome. For anything larger than a fix, open an issue first so we
can agree on the approach; `docs/vision.md` says what Nytka is and is not.

## Set up

You need the .NET 10 SDK, a running Docker daemon and [gitleaks](https://github.com/gitleaks/gitleaks).

```bash
git config core.hooksPath .githooks
cp .private-terms.example .private-terms   # list what must never appear in this repository
dotnet test
```

The hooks refuse commits whose author or committer is not a GitHub noreply address, scan staged
files and messages for the terms in `.private-terms`, and run gitleaks.

## Rules

- Conventional commits (`feat:`, `fix:`, `docs:` ...); they drive the changelog.
- Every change comes with tests. Pipeline tests use synthetic audio: never commit a recording of a
  real person, not even your own.
- Never edit a migration that has shipped; add a new numbered file under `db/migrations/`.
- Never log audio, transcript text or tokens.
