# Changelog

## [0.8.0](https://github.com/nytka-app/server/compare/v0.7.0...v0.8.0) (2026-09-30)


### Features

* ask a question of your history with numbered sources (v0.8, S-A) ([#46](https://github.com/nytka-app/server/issues/46)) ([2c075ba](https://github.com/nytka-app/server/commit/2c075bab43096a983bba30f5b3ae7a4a07622256))
* bookmarks (v0.8, track S-B) ([#45](https://github.com/nytka-app/server/issues/45)) ([061a400](https://github.com/nytka-app/server/commit/061a40068bd2af5b5acf375d7b4bcef47eddc109))
* import an Omi export (v0.7) ([#47](https://github.com/nytka-app/server/issues/47)) ([72f6c90](https://github.com/nytka-app/server/commit/72f6c9050462406e6b4c181370f6c28aa7f21d79))
* playback audio and index routes (v0.8 S-P) ([#44](https://github.com/nytka-app/server/issues/44)) ([2f63e47](https://github.com/nytka-app/server/commit/2f63e47e6f26e19163aefcfaf07ca7601d892de2))

## [0.7.0](https://github.com/nytka-app/server/compare/v0.6.0...v0.7.0) (2026-09-30)


### Features

* drop audio captured inside mute windows before transcription ([#41](https://github.com/nytka-app/server/issues/41)) ([49f6fb3](https://github.com/nytka-app/server/commit/49f6fb36add948d54788f100176dc8d0162da9f7))

## [0.6.0](https://github.com/nytka-app/server/compare/v0.5.0...v0.6.0) (2026-09-30)


### Features

* **ai:** local time, tighter tasks and memories, brief summaries for short talk ([#39](https://github.com/nytka-app/server/issues/39)) ([1b7d5df](https://github.com/nytka-app/server/commit/1b7d5dffba2784a4c840119ea7f6be6a2f7ce149))
* **server:** list unnamed voices, merge people, forget voiceprints (v0.6) ([#38](https://github.com/nytka-app/server/issues/38)) ([9620c0b](https://github.com/nytka-app/server/commit/9620c0b8685f1306daf8817d82ea0cf0246d761b))


### Bug Fixes

* **audio:** past the 30 s soft cap, close a batch only at a pause of 1 s or more ([#37](https://github.com/nytka-app/server/issues/37)) ([20f6c54](https://github.com/nytka-app/server/commit/20f6c5483a6ca3bb68ca5056dcfe5df4e2cc57c1))

## [0.5.0](https://github.com/nytka-app/server/compare/v0.4.1...v0.5.0) (2026-09-29)


### Features

* **server:** keep speaker_id and is_user, name voices as people (v0.6) ([#35](https://github.com/nytka-app/server/issues/35)) ([9fbf156](https://github.com/nytka-app/server/commit/9fbf156555d314eeb47d5ec76dfaf96fe53df00e))

## [0.4.1](https://github.com/nytka-app/server/compare/v0.4.0...v0.4.1) (2026-09-29)


### Bug Fixes

* **pipeline:** bound process-session runs by audio, and always make progress ([#30](https://github.com/nytka-app/server/issues/30)) ([348b88f](https://github.com/nytka-app/server/commit/348b88fbe5476a0307ba91aa11edd97357403d1e))

## [0.4.0](https://github.com/nytka-app/server/compare/v0.3.0...v0.4.0) (2026-09-29)


### Features

* **server:** full-text search with the Ukrainian dictionary (v0.4 track G) ([#28](https://github.com/nytka-app/server/issues/28)) ([b7cb14f](https://github.com/nytka-app/server/commit/b7cb14fef2b85c1516a7b98787dfde34e64caa0d))
* **server:** memories extraction, API and MCP tool (v0.4 track F) ([#26](https://github.com/nytka-app/server/issues/26)) ([442ea79](https://github.com/nytka-app/server/commit/442ea790bdee2046d2cce93fc7c487454f4527cd))
* **server:** named tokens with scopes and the settings service (v0.2 track A) ([#23](https://github.com/nytka-app/server/issues/23)) ([c959d9f](https://github.com/nytka-app/server/commit/c959d9fb45c42232f59ca3a860d1bc31dcfe8561))
* **server:** offline sync priority, conversation merge, offline-sync feature (v0.3 track 5) ([#25](https://github.com/nytka-app/server/issues/25)) ([a3d864d](https://github.com/nytka-app/server/commit/a3d864d0d897ed92f6f027d098d8a153c5e3caaa))
* **server:** read-only MCP endpoint (v0.2 track C) ([#24](https://github.com/nytka-app/server/issues/24)) ([f351228](https://github.com/nytka-app/server/commit/f351228ef01f974003b5d263e5a2f62a5a868e6a))
* **server:** v0.2 AI layer, tasks API and speaker labels (track B) ([#22](https://github.com/nytka-app/server/issues/22)) ([e4777d8](https://github.com/nytka-app/server/commit/e4777d8fc0cef9ff682f39572c59d2fabdca1e3a))
* **server:** v0.2 S1 server skeleton (hooks, lanes, migrations 0003/0004, seams) ([#18](https://github.com/nytka-app/server/issues/18)) ([24c13f6](https://github.com/nytka-app/server/commit/24c13f6292825bcd4de26073f4f6219e02228036))
* **server:** v0.4 S3 migrations 0006 memories and 0008 webhooks ([#20](https://github.com/nytka-app/server/issues/20)) ([978941b](https://github.com/nytka-app/server/commit/978941b8a71742fdfcb42fbb620af87137a46a32))
* **server:** v0.4 track H outgoing webhooks ([#27](https://github.com/nytka-app/server/issues/27)) ([aa07fc0](https://github.com/nytka-app/server/commit/aa07fc0aa239c7b177715c99b72178c01b21385a))


### Bug Fixes

* **server:** summaries ignore stale results; merges queue a new summary (v0.3 track 5 follow-up) ([#29](https://github.com/nytka-app/server/issues/29)) ([a38c1bb](https://github.com/nytka-app/server/commit/a38c1bb7b6b81d0ea1ec4602844fdbf19c9a673b))

## [0.3.0](https://github.com/nytka-app/server/compare/v0.2.0...v0.3.0) (2026-09-29)


### Features

* **pipeline:** cut batches at pauses instead of at 30 s of speech ([#12](https://github.com/nytka-app/server/issues/12)) ([750cec1](https://github.com/nytka-app/server/commit/750cec10c772ee540307aec921142a970c0e647f))

## [0.2.0](https://github.com/nytka-app/server/compare/v0.1.0...v0.2.0) (2026-09-29)


### Features

* **server:** accept diagnostics samples from the app ([#10](https://github.com/nytka-app/server/issues/10)) ([96e15ba](https://github.com/nytka-app/server/commit/96e15bab68d7912bb92cefba5c9fb125d39a9991))

## 0.1.0 (2026-09-29)


### ⚠ BREAKING CHANGES

* Nytka server v0.1 ([#7](https://github.com/nytka-app/server/issues/7))

### Features

* Nytka server v0.1 ([#7](https://github.com/nytka-app/server/issues/7)) ([83af94a](https://github.com/nytka-app/server/commit/83af94a0993d6cb887f497edaf3e8014670371e5))


### Bug Fixes

* **release:** start at 0.1.0 ([#8](https://github.com/nytka-app/server/issues/8)) ([f98d40a](https://github.com/nytka-app/server/commit/f98d40a5d26c052140b7cc020e33368f43c7f79b))
