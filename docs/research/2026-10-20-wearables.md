# Wearable audio recorders supported by the official Omi app: survey for Nytka multi-device planning

Date: 2026-10-20. Pinned commit of BasedHardware/omi `main`: `afad3b7` (full `afad3b72244fa576d07bd5a33c222f0adadaa94d`). Repo license: MIT (GitHub API `license.spdx_id`, 2026-10-20). Paths below are relative to the repo root; line numbers are at `afad3b7`. "Dir" = `app/lib/services/devices/`.

Device types in the official app: `enum DeviceType { omi, openglass, appleWatch, plaud, bee, fieldy, friendPendant, limitless, raybanMeta }` (`app/lib/backend/schema/bt_device/bt_device.dart:210`). Eight of nine are connectors in `Dir connectors/`; `omi` and `openglass` share the Omi GATT service.

Method note: connector facts come from reading the Dart/Kotlin source at the pin, not from running hardware. Web facts: pages fetched or searched 2026-10-20 unless a date is given.

## Table

| Device | Maker / status | License & protocol source | BLE discovery | Codec / rate | Offline storage: read & free | Buttons / battery / settings / firmware | How to get one to test |
|---|---|---|---|---|---|---|---|
| **Omi pendant** (Omi CV1; also DevKit1/2) | Based Hardware; on sale | MIT code (repo `LICENSE`); firmware + hardware in-repo (`omi/firmware`, `omi/hardware`, `omi/hardware/consumer/LICENSE` MIT) | Service `19b10000-e8f2-537e-4f6c-d104768a1214` (`Dir models.dart:12`) | Codec read from char `19b10002`: 1=PCM8, 20=Opus, 21=OpusFS320 (16 kHz) (`omi_connection.dart:420-433`) | Yes. Ring protocol (fw 3.0.20+): INFO 0x10, READ 0x11, ADVANCE 0x12, ADVANCE_ID 0x15, CLEAR 0x13, CUSTODY_ENABLE 0x14 (`ring_protocol.dart:37-42`). Legacy: LIST 0x10, DELETE_FILE 0x12 (`omi_connection.dart:588-633`) | Button svc `23ba7924`; battery std 0x180F; LED `19b10011` and mic gain `19b10012` (both clamp 0-100 in official app); features `19b10021`; OTA/DFU | omi.me: $179 list, $129 with checkout discount |
| **OmiGlass** (`openglass`) | Based Hardware; dev kit | MIT (`omiGlass/LICENSE`); XIAO ESP32S3 firmware in-repo | Omi service UUID; type chosen after reading device info (`bt_device.dart:434-442`) | Reads same codec char; default Opus (`omiglass_connection.dart:134-153`); JPEG photos | No (`omiglass_connection.dart:483-486`) | WiFi OTA service `19b10010` cmds 0x01-0x05 (`models.dart:76-90`) | omi.me: Glass Dev Kit $499 |
| **Limitless Pendant** | Limitless (acquired by Meta, 5 Dec 2025); no longer sold | Closed firmware; protocol reverse-engineered by Omi (protobuf over BLE); no public docs | Service `632de001-604c-446b-a80f-7963e950f3fb`; or name contains "limitless"/"pendant" (`discovery/native_bluetooth_discoverer.dart:118-121`) | Opus, app labels it `opusFS320` = 16 kHz (`limitless_connection.dart:2067-2069`) | Yes. DownloadFlashPages batch mode, flash pages; ACK msg 7 "up to index" frees pages on the pendant (`limitless_connection.dart:1223-1245`, `LimitlessProtocol.kt:96-98`) | Double press forwarded; short/long ignored (`:1716-1740`); battery std 0x180F; LED brightness msg 26; no mic gain; no firmware update | Used only. Price UNKNOWN |
| **Bee** | Bee, bought by Amazon (announced 22 Jul 2025); new Bee Frontier $74.99 on sale 15 Oct 2026 | Closed; reverse-engineered; fw >= 0.6.1 uses encrypted audio, Omi says unsupported (`bt_device.dart:626-640`, `:691-694`) | Service `03d5d5c4-a86c-11ee-9d89-8f2089a49e7e`; or name contains "bee" (`:100-102`) | AAC (ADTS frames); sample rate UNKNOWN in source (`bee_connection.dart:41`, `:64-84`) | No (`custom_connection.dart:144-147`) | Mute/unmute cmd 0xC006; battery cmd 0xC00F (mV curve); fw cmd 0xC005; no buttons; no settings | Original Pioneer: used only. Frontier $74.99 but fw likely unsupported (UNKNOWN) |
| **Plaud NotePin** | PLAUD; NotePin $159, NotePin S $179 on plaud.ai | Closed; Omi uses reverse-engineered cmds; Plaud now publishes official iOS/Android SDKs (token/key required) | Service `00001910-0000-1000-8000-00805f9b34fb`, or name starts "plaud"/contains "notepin" (`:104-107`) | Opus `opusFS320` rechunked to 80 B (`plaud_connection.dart:204-235`) | Live only: app starts a record session (cmd 20) and syncs it (cmd 28). No stored-audio listing/ACK (`:270-275`) | Start 20, stop 23, battery 9 (poll 60 s), stop sync 30; no buttons, no settings, no firmware | NotePin $159 / NotePin S $179 (plaud.ai) |
| **Fieldy** (Compass / Fieldy 3) | Field Labs; on sale | Closed; reverse-engineered | Service `4fafc201-1fb5-459e-8fcc-c5c9c331914b`, or name "compass"/"fieldy" (`:109-112`) | Opus `opusFS320`, 40-byte frames, 6 per notify, TOC 0xb8 (`fieldy_connection.dart:66-111`) | No (inherits `custom_connection.dart:144-147`) | Std battery 0x180F read; no buttons/settings | UNKNOWN official price; third-party: $179 list / $129 sale |
| **Friend pendant** | friend.com; Friend 2.0 $249 (one outlet, 31 Jul 2026) | Closed; reverse-engineered | Service `1a3fd0e7-b1f3-ac9e-2e49-b647b2c4f8da`, or name starts "friend_" (`:114-116`) | LC3 16 kHz, 10 ms / 30 B frames, 3 per 95 B packet with 5 B footer (`friend_pendant_connection.dart:7-26`) | No (`:69-72`) | Battery is a hard-coded 90 (`:28-43`); no buttons/settings | friend.com |
| **Apple Watch** | Apple | n/a | Not BLE: WatchConnectivity via `WatchTransport` (`apple_watch_connection.dart:12-19`) | UNKNOWN (not read) | UNKNOWN | iOS only | n/a for Android |
| **Ray-Ban Meta** | Meta | Meta Wearables Device Access Toolkit (official SDK) | Not BLE GATT: SDK transport (`rayban_meta_connection.dart:12-25`) | PCM16 mono from Bluetooth HFP mic | No | Battery unknown to SDK (`:57-61`) | Glasses; price UNKNOWN |

## Per-device details

### Omi pendant (DeviceType.omi, openglass)

- Discovery: `_isOmi` matches `omiServiceUuid` in advertised services (`discovery/native_bluetooth_discoverer.dart:123-125`). Anything not matched by another rule falls back to `omi` (`:146-148`).
- GATT constants at `Dir models.dart:12-45`: audio `19b10001`, codec `19b10002`, button service `23ba7924` / char `23ba7925`, storage service `30295780-4301-eabd-2904-2849adfeae43` with data `30295781` and read-control `30295782`, time sync `19b10030`, accel `32403790`, speaker `cab1ab95`.
- Settings service `19b10010`: LED dim `19b10011`, mic gain `19b10012` (`omi_connection.dart:21-23`). The app writes `[value.clamp(0,100)]` for both (`:1194`, `:1233`). Nytka's CLAUDE.md documents gain as levels 0-8; the official app's 0-100 clamp does not contradict that, but the app does not map levels. Treat the 0-8 table as Nytka's own firmware reading.
- Storage: ring protocol constants and acks at `ring_protocol.dart:26-55`. Record size 444 B, 4 B timestamp, 440 B audio. Commands: INFO 0x10, READ 0x11, ADVANCE 0x12, CLEAR 0x13, CUSTODY_ENABLE 0x14, ADVANCE_ID 0x15, STOP 0x03. Caps: APP_ACK_RECLAIM 0x01, LIVE_PERSIST 0x02, ADVANCE_IDEMPOTENT 0x04, RING_ID 0x08.
- Free semantics: `performAdvanceRingCustody` sends ADVANCE (or ADVANCE_ID with ring id) and waits for ack (`omi_connection.dart:735-803`). `performClearRing` sends CLEAR (`:846-883`). Callers of `performClearRing` in `app/lib` are only `device_connection.dart` and `omi_connection.dart` (GitHub code search, 2026-10-20); I did not trace UI call sites, so whether the official app ever sends CLEAR is UNKNOWN.
- Legacy multi-file path: LIST 0x10, DELETE 0x12 (index byte), one-byte result, 0 = ok (`omi_connection.dart:588-633`).
- OTA/DFU: double-click reset to enter DFU per docs (https://docs.omi.me/onboarding/omi-devkit-2.md, accessed 2026-10-20). OmiGlass has a separate WiFi OTA service (`Dir models.dart:76-90`).
- Hardware: MIT licensed hardware directory (`omi/hardware/consumer/LICENSE`).
- Price: https://www.omi.me/pages/pricing (accessed 2026-10-20): pendant $179 list, $50 discount at checkout = $129; Glass Dev Kit $499; Firmware Developer Kit $59.99; 1-year warranty. DevKit 2 price: UNKNOWN (not on the pricing page).

### Limitless Pendant (DeviceType.limitless)

- Status: https://limitless.ai/ (accessed 2026-10-20): "We are no longer selling the Pendant as of December 5, 2025"; support for existing customers "throughout 2026" (at least another year). TechCrunch (https://techcrunch.com/2025/12/05/meta-acquires-ai-device-startup-limitless/, accessed 2026-10-20) confirms sales ended and subscribers moved to the Unlimited plan. No published date after 2026: UNKNOWN. Regional service cutoffs (EU, UK etc.) come from a competitor's post (https://www.hedy.ai/post/meta-acquires-limitless-ai-privacy/) and a search summary; unverified.
- Protocol: protobuf over BLE. Services: `632de001...` with TX `632de002` (write) and RX `632de003` (notify) (`Dir models.dart:60-62`). Every RX notification is a wrapper (1=index, 2=seq, 3=num_frags, 4=payload) and payloads span fragments (`LimitlessProtocol.kt:9-14`).
- Setup sequence: SetCurrentTime (msg 6) then mode command; clock-drift measured from a Type-8 notification first (`limitless_connection.dart:26-29`, `:204-221`, `:315-339`).
- Mode command msg 8 `{batchMode, realTime}`: `{1,0}` download stored flash, `{0,1}` live, `{0,0}` record to flash (`LimitlessProtocol.kt:89-94`). Enable-data-stream also uses msg 8 (`limitless_connection.dart:1215-1221`).
- Audio: Opus frames extracted from flash-page protobuf (audio wrapper 0x1a, data 0x12, Opus TOC validated) (`:807-880`, `:1077`). Codec reported as `opusFS320` (`:2067-2069`), which the app maps to 16 kHz (`bt_device.dart:176-179`). Frame duration for Limitless: UNKNOWN (inferred 20 ms from the enum only).
- Offline storage: GetDeviceStatus (msg 21) returns oldest/newest flash page, session, free/total capture pages (`:1257-1260`, `:1804-1860`). Batch download then ACK: msg 7 with up-to page index; "the pendant frees them" (`LimitlessProtocol.kt:96-98`). The Android native drain engine ACKs only after an fsync barrier (`LimitlessFlashDrainEngine.kt:222-238`). The Dart path also ACKs incrementally and after WAL deletion (`app/lib/services/wals/flash_page_wal_sync.dart:114-123`, `:138-148`, `:489-521`). This is the same custody pattern as Nytka invariant 1: free only after the data is safe.
- Buttons: events arrive in field 0x42 of the payload; only DOUBLE_PRESS is forwarded; LONG_PRESS is the pendant's own record toggle and is ignored (`:1698-1740`).
- Battery: standard 0x180F read + notify (`:2032-2064`). LED brightness: msg 26, 0-100 (`:2136-2162`). Mic gain: not supported (`:2170`). Firmware update: none in the app; device info is a placeholder 1.0.0 (`:2175-2182`).
- Android native code: `app/android/app/src/main/kotlin/com/friend/ios/limitless/LimitlessProtocol.kt` (496 lines, pure codec, tested against `app/test/fixtures/limitless_golden/`) and `LimitlessFlashDrainEngine.kt` (330 lines). These are the closest reusable reference for a Kotlin implementation.
- License of that code: MIT (repo). The pendant firmware is closed: UNKNOWN beyond that.
- Where to get one: not sold new. Used-market price and availability: UNKNOWN (not checked).

### Bee (DeviceType.bee)

- Status: Amazon acquisition announced 22 Jul 2025 (https://siliconangle.com/2025/07/22/amazon-buys-wearable-ai-device-maker-bee-undisclosed-sum/, via search summary). Amazon launched Bee Frontier ($74.99, on sale) and Bee Eclipse (from $99.99, coming soon) on 15 Oct 2026 (https://techcrunch.com/2026/10/15/amazons-bee-launches-two-new-personal-ai-devices-starting-at-75/ and https://letsdatascience.com/news/amazon-launches-bee-frontier-and-eclipse-devices-a8b031b3, accessed 2026-10-20; search summaries). Frontier has dual mics, IP67, USB-C, up to 7 days battery (search summary of the same coverage); raw audio deleted after real-time processing; Bee Pro $11.99/month for extras. No public API mentioned.
- Whether the Frontier or current Pioneer firmware speaks the protocol Omi implements: UNKNOWN. Omi marks fw >= 0.6.1 "encrypted audio that Omi cannot process" and asks users to stay below it (`bt_device.dart:626-640`, `:691-694`). A new-hardware purchase therefore likely will not work with this protocol.
- Protocol: custom control char `05e1f93c-d8d0-5ed8-dd88-379e4c1a3e3e`; commands are 16-bit codes (mute 0xC006 with data 0x01/0x00, battery 0xC00F, fw 0xC005) (`bee_connection.dart:19-35`, `:8`). Responses carry code in the first two bytes; 0x8000 wraps an echoed command (`:44-61`). Audio notifications: strip 2-byte header and reassemble ADTS frames (sync 0xFFF) (`:64-84`).
- Codec: AAC (`:41`). Sample rate and channels: UNKNOWN (the code never reads the ADTS header; the app's catch-all returns 16000, `bt_device.dart:188-189`).
- Battery: 2-byte `[pct, charging]` or 3-byte `[mV_lo, mV_hi, charging]` with an interpolated LiPo curve (`bee_connection.dart:86-162`). No storage listing (`custom_connection.dart:144-147`). No buttons.

### Plaud NotePin (DeviceType.plaud)

- Status and price: NotePin $159, NotePin S $179 on https://www.plaud.ai/products/plaud-notepin-s (accessed 2026-10-20). The page states 20 h continuous recording, a tactile button (long press records, short press highlights) on the S. Offline storage capacity: UNKNOWN. A July 2026 sale put NotePin at $126.99 (search summary).
- Protocol: service `0x1910`, write `0x2BB1`, notify `0x2BB0` (`Dir models.dart:47-49`). Frame format `[1, cmdLo, cmdHi, payload...]`; notifications with first byte 2 are audio chunks `[pos(4) ... len(1) data]`, end marker position 0xFFFFFFFF (`plaud_connection.dart:51-75`, `:77-88`). Commands: battery 9, start record 20, stop record 23, sync file start 28, stop sync 30 (`:10-14`).
- The app does not read the device's stored recordings: it stops any session, starts a new one, then asks to "sync" that session from the start time and treats the stream as live (`:237-268`). Whether the same sync command can pull earlier on-device recordings: UNKNOWN. Whether anything is freed: UNKNOWN (no delete command in the app).
- Codec: reported as `opusFS320` and rechunked to 80-byte units (`:204-235`); the true Plaud frame layout is not verified here.
- Official SDK: Plaud publishes iOS and Android SDKs (scan, connect, record control, file export, Wi-Fi sync) with partner tokens and app keys (https://docs.plaud.ai/plaud-embedded/ios-sdk, https://dev.plaud.ai/device-sdk/, via search summary 2026-10-20; not fetched directly). No public BLE protocol spec found. SDK license and terms: UNKNOWN. This is the route that avoids reverse engineering but needs a partner agreement.
- Firmware: Omi warns that vendor-app firmware updates can break recording (`bt_device.dart:678-681`).

### Fieldy (DeviceType.fieldy)

- Status: Fieldy 3 on https://www.fieldy.ai (accessed 2026-10-20); 150 free transcription min/month; Unlimited $15.99/month billed yearly or $19.99 monthly. Device price is not on the fetched page. Search summaries quote $179 list / $129 sale (https://www.layer3labs.io/guides/fieldy-ai-review, unverified). The Omi app labels it "Compass" for the BLE name, and the Play package is `com.compasswearable`; the link between "Compass" and Fieldy 3 is only inferred from names, not confirmed.
- Protocol: service `4fafc201-1fb5-459e-8fcc-c5c9c331914b`, audio char `82a48422-3ca9-4156-ae67-4170f58666e0` (also used as control) (`Dir models.dart:54-56`, `fieldy_connection.dart:14-15`). No commands are sent: audio simply streams on notify. Each notify holds up to 6 Opus frames of 40 bytes starting with TOC 0xb8 (`:66-111`). The 4fafc201 UUID is the Espressif BLE example UUID, so name or service alone may collide with DIY ESP32 devices.
- Offline storage: none in the app; the Fieldy site says it "doesn't store audio recordings" and needs a phone connection (fetched page, accessed 2026-10-20).
- Battery: standard 0x180F read (`:54-64`). Open API/SDK: none mentioned on site.

### Friend pendant (DeviceType.friendPendant)

- Status: https://www.friend.com returned only the word "friend" when fetched. Price $249 for "Friend 2.0", on sale, optional $10/month for memory beyond 30 days, iPhone companion app and no stated Android support, per https://the-gadgeteer.com/2026/07/31/friend-ai-pendant-voice-wearable-249-price/ (accessed 2026-10-20). Layer3 Labs (5 Aug 2026) still cites $99, treated as stale.
- Whether Friend 2.0 hardware uses the BLE service Omi implements: UNKNOWN. Omi's connector is for the original pendant.
- Protocol: service `1a3fd0e7-b1f3-ac9e-2e49-b647b2c4f8da`, audio char `01000000-1111-1111-1111-111111111111` (`Dir models.dart:57-58`). LC3, 16 kHz, 10 ms frames of 30 bytes; packet = 3 frames + 5-byte footer = 95 B (`friend_pendant_connection.dart:7-26`). Decoding LC3 on Android would need a library (e.g. liblc3); not evaluated here.
- No storage, no battery (placeholder 90), no buttons, no settings.

### Apple Watch and Ray-Ban Meta

- Apple Watch: not a BLE peripheral; uses a platform channel transport with pseudo UUIDs like `watch-audio-service` (`apple_watch_connection.dart:12-19`). Irrelevant to an Android app.
- Ray-Ban Meta: Meta Wearables Device Access Toolkit; mic via Bluetooth HFP as PCM16 mono; camera via the toolkit; battery not exposed (DAT 0.8 in the Omi comment) (`rayban_meta_connection.dart:17-24`, `:57-61`). Meta's FAQ (https://developers.meta.com/wearables/faq, via search summary 2026-10-20) says camera via toolkit and mic/speakers through iOS/Android Bluetooth profiles, Android 10+. Publishing to the public was limited to select partners at the time of coverage; current status UNKNOWN. Supported hardware per summary: Ray-Ban Meta Gen 1 and 2, Optics, Ray-Ban Display.

### Other open hardware

- Omi DevKit1/DevKit2 (XIAO nRF52840 Sense; build presets `prj_xiao_ble_sense_devkitv1*.conf`, `...devkitv2-adafruit.conf`, `omi/firmware/devkit/`) and the production `omi/firmware/omi` target, built on Zephyr (`omi/firmware/readme.md`). MIT. SD-card variants exist as `...-spisd.conf`.
- OmiGlass: XIAO ESP32S3 + camera (PlatformIO env `seeed_xiao_esp32s3`, `omiGlass/firmware`). MIT.
- Price for a bare DevKit or XIAO board: UNKNOWN (not checked). Omi sells a "Firmware Developer Kit" at $59.99 (pricing page above); its contents are UNKNOWN.

## Relevance to Nytka (not asked for, short)

- Only two connectors in the official app expose stored audio with a free/ack command: Omi (ring ADVANCE) and Limitless (ACK up to page index). Plaud, Bee, Fieldy, Friend are live-only. A Nytka design that depends on "free only after server accepted" can only be honored for those two.
- Limitless is the only non-Omi device whose full stored-audio protocol is already written in Kotlin (MIT) in the Omi repo, but the hardware is discontinued and support is promised only through 2026.
- Codec spread to handle if multi-device: Opus (Omi, Limitless, Plaud, Fieldy), AAC (Bee), LC3 (Friend), PCM16 (Ray-Ban Meta).

## Facts I could not verify (UNKNOWN)

1. Limitless: any support or app availability after 2026; frame duration; used-market price and availability.
2. Bee: whether Frontier/Eclipse speak the protocol Omi implements; AAC sample rate and channels; Pioneer used-market status.
3. Plaud: on-device storage capacity; whether the sync command can fetch earlier recordings; any delete/free command; Plaud SDK license/terms; actual Opus frame layout.
4. Fieldy: official device price; offline storage on hardware (site says no stored recordings); confirmation that "Compass" BLE name equals Fieldy 3.
5. Friend: whether Friend 2.0 hardware matches the BLE service in Omi; Android support; `friend.com` product page was not readable.
6. Omi: DevKit 2 price; whether the official app's UI ever sends CLEAR (call-site trace not done); contents of the Firmware Developer Kit.
7. Apple Watch codec and storage (not read). Ray-Ban Meta public-publishing status and exact mic API on Android.
8. All price figures from search summaries (Bee Frontier, Fieldy, Friend 2.0, Plaud sale) were not confirmed on the vendors' own pages unless stated "fetched".
9. Hardware testing: nothing was run on a physical device; all protocol facts are from reading source.

## Sources (accessed 2026-10-20)

- https://github.com/BasedHardware/omi/tree/afad3b72244fa576d07bd5a33c222f0adadaa94d (all code citations)
- https://www.omi.me/pages/pricing
- https://docs.omi.me/onboarding/omi-devkit-2.md
- https://limitless.ai/
- https://techcrunch.com/2025/12/05/meta-acquires-ai-device-startup-limitless/
- https://www.hedy.ai/post/meta-acquires-limitless-ai-privacy/ (competitor, caution)
- https://techcrunch.com/2026/10/15/amazons-bee-launches-two-new-personal-ai-devices-starting-at-75/
- https://letsdatascience.com/news/amazon-launches-bee-frontier-and-eclipse-devices-a8b031b3
- https://siliconangle.com/2025/07/22/amazon-buys-wearable-ai-device-maker-bee-undisclosed-sum/
- https://www.plaud.ai/products/plaud-notepin-s
- https://docs.plaud.ai/plaud-embedded/ios-sdk, https://dev.plaud.ai/device-sdk/ (search results only)
- https://www.fieldy.ai
- https://www.layer3labs.io/guides/fieldy-ai-review (search result only)
- https://the-gadgeteer.com/2026/07/31/friend-ai-pendant-voice-wearable-249-price/
- https://www.friend.com (fetch returned no content)
- https://developers.meta.com/wearables/faq (search result only)
