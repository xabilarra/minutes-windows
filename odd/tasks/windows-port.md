# Minutes for Windows

Objective: a Windows port of Minutes (xabilarra/minutes) that records calls without bots and writes the same meeting folders. Branch: `main` of the new public repo `xabilarra/minutes-windows`; delivery: work-unit commits pushed to `main`, GitHub Actions builds `Minutes.exe` on Windows and publishes it to the `latest` release. Test runner: `dotnet test` (xUnit, Core tests run on macOS and Windows).

## Specs

- S1 — "tener la aplicacion de windows par aprobar en windows, yo entraria desde el otro ordeandor me descargria el exe o el github ya funcionar ocnlo que me digas alli": a downloadable `Minutes.exe` built by CI on Windows, with Spanish instructions to try it.
- S2 — Phase 1 ("Grabar y transcribir"): manual start/stop, two tracks (microphone = "Yo", system audio = "Ellos"), Groq `whisper-large-v3` transcription, and the same folder and Markdown/JSON format as the Mac app (`~/Reuniones/<yyyy-MM-dd HH.mm> <title> (<app>)/` with `yo.m4a`, `ellos.m4a`, `transcripcion.md`, `transcripcion.json`, `meta.json`).
- S3 — Phase 2 ("Arranque automático"): start when a known meeting app (or a browser showing a meeting) takes the microphone for 3 s; stop 45 s after it releases it.
- S4 — Phase 4 subset: tray icon, floating pill (recording time + stop, countdown after hanging up, transcript ready with copy text / copy path), launch at login, Groq key stored encrypted for the user.
- S5 — Port the Mac core logic with its behaviour: speech-only uploads, chunking, retries and rate-limit waits, hallucination filter, echo removal, vocabulary correction, Whisper token decoding, retryable failures with chunk cache.
- Assumption (not requested, out of scope for this goal): video recording (phase 3) comes later; `hasVideo` is false.

## Tasks

| ID | Specs | Route | Status | Evidence |
|----|-------|-------|--------|----------|
| T1 | S5 | inline | done | 61 xUnit tests pass on macOS (dotnet test); tests written with the port, not RED-first (behaviour already specified by the Mac code) |
| T2 | S2, S3, S4 | inline | done (runtime pending) | d966336 + 70df516; builds on macOS with 0 warnings; independent verifier: no blockers, 4 should-fix applied in 70df516; native review approved and acknowledged (lineage review-3f13098eac9f7e27); review warnings fixed in follow-up; runtime on Windows pending Xabi |
| T3 | S1 | inline | done | 25c6986 + 70df516; CI run 44135515291 green on windows-latest (62/62 tests, Minutes.exe 49.9 MB) and `latest` prerelease published |

T1: Minutes.Core (net10.0) port + xUnit tests. T2: Minutes.App (WinForms, NAudio WASAPI, registry mic detection, tray, pill, settings). T3: CI workflow, release, README and Windows test instructions.

## Log

- L1 (2026-11-17, user): "vale si adelante, /goal tener la aplicacion de windows par aprobar en windows, yo entraria desde el otro ordeandor me descargria el exe o el github ya funcionar ocnlo que me digas alli, aunque aqui lo estemos creando en mac, no se si afecta. hazme askuserqustion si necesitas algo de mi"
- L2 (2026-11-17, user): on delivery, "ahora mismo minutes esta publica en github creo, asique podriamos ponerla publica no?" — verified `xabilarra/minutes` is PUBLIC (MIT); new repo will be public.
- L3 (2026-11-17): decisions agreed earlier in the session: C# / .NET (not Rust), separate repo, phased plan (1 record+transcribe, 2 auto-start, 3 video, 4 pill/login/retention).
- L4 (2026-11-17): design. WinForms code-only UI (builds on macOS with EnableWindowsTargeting). NAudio WASAPI capture and loopback capture, resampled to 16 kHz mono 16-bit WAV raw tracks (`yo.wav`, `ellos.wav`) padded with silence against a shared session clock (loopback delivers no packets while nothing plays). Uploads are WAV chunks (10 min = 19.2 MB < 25 MB). Final tracks are AAC m4a through Media Foundation (its AAC encoder only takes 44.1/48 kHz, so 16 kHz is upsampled to 48 kHz; 96 kbps is its minimum). Detection reads `CapabilityAccessManager\ConsentStore\microphone` (LastUsedTimeStop == 0 means in use). Groq key protected with DPAPI. .NET SDK 10.0.404 installed in `~/.dotnet` on the Mac.
- L5 (2026-11-17): Engram mirror `odd/windows-port/tasks` pending: mem_save refused with session_project_mismatch (session belongs to project "minutes"). Resync from a session started in this repo.
- L6 (2026-11-17): RDD assess unavailable for the root commit b54f6fd (no base); app range assessed high (process boundary, workflow shell, escalated item 2). Independent verifier: no blockers; fixed release race, PR write token, empty folder on failed start, float subtype, multimedia device changes, COM leak, resume-before-detect, per-byte upsampling queue. Remaining risk: NAudio/Media Foundation behaviour only provable on a real Windows run; Communications vs Console default devices may leave one track silent (logged).
- L7 (2026-11-17, user): "usamos en ese ordenador teams para las videollamadas sibretodo" — call apps now record the default communications devices (Teams' default), browsers and manual recordings the default device; the log names the device recorded.
- L8 (2026-11-17, user): "y no puede hacer minutes-windows detectar autoamticamente cual esta emitiendo sonido y asi no depende de la configuracion? No hay una cosa que me dé más rabia que perder una grabación porque no había puesto bien la configuración." — tracks now follow active audio sessions: Yo = every capture endpoint another process is using (fallback default), Ellos = every render endpoint with another process's active session plus the default outputs; mixed with TrackMixer (Core, tested). Per-process loopback (Windows 11 process loopback API) kept as the next step if this misses audio.
- L9 (2026-11-17): verifier on 4c1cdfe: no blockers; should-fix applied: devices used by the meeting app's process tree (Active, else Inactive/muted) win over other apps' devices (no wrong mic, no doubled voice through noise-suppression chains); Stop waits for a running scan; per-device errors logged once a minute; no double notification on a failed start.
- L10 (2026-11-17): verifier recheck on 97d320f: findings 1, 3, 5 fixed, 2 fixed for meeting-app recordings; leftover fixed: while the app is muted only one of its Inactive devices is kept (already recorded, else default, else first).
