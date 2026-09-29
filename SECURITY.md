# Security policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting on this repository (Security tab → "Report a
vulnerability"). Don't open a public issue. Expect an acknowledgement within 7 days.

## Scope notes

The server holds recorded speech and transcripts of the wearer and of the people around them, an
admin token, and possibly a key for a transcription provider. Report any way to reach the API
without the token, any path where audio, a transcript or a token can leak into logs, error
messages, images or files, and any way a chunk upload can read or overwrite data it should not.

## Supported versions

The latest release.
