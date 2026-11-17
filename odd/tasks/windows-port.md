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
| T2 | S2, S3, S4 | inline | pending | |
| T3 | S1 | inline | pending | |

T1: Minutes.Core (net10.0) port + xUnit tests. T2: Minutes.App (WinForms, NAudio WASAPI, registry mic detection, tray, pill, settings). T3: CI workflow, release, README and Windows test instructions.

## Log

- L1 (2026-11-17, user): "vale si adelante, /goal tener la aplicacion de windows par aprobar en windows, yo entraria desde el otro ordeandor me descargria el exe o el github ya funcionar ocnlo que me digas alli, aunque aqui lo estemos creando en mac, no se si afecta. hazme askuserqustion si necesitas algo de mi"
- L2 (2026-11-17, user): on delivery, "ahora mismo minutes esta publica en github creo, asique podriamos ponerla publica no?" — verified `xabilarra/minutes` is PUBLIC (MIT); new repo will be public.
- L3 (2026-11-17): decisions agreed earlier in the session: C# / .NET (not Rust), separate repo, phased plan (1 record+transcribe, 2 auto-start, 3 video, 4 pill/login/retention).
- L4 (2026-11-17): design. WinForms code-only UI (builds on macOS with EnableWindowsTargeting). NAudio WASAPI capture and loopback capture, resampled to 16 kHz mono 16-bit WAV raw tracks (`yo.wav`, `ellos.wav`) padded with silence against a shared session clock (loopback delivers no packets while nothing plays). Uploads are WAV chunks (10 min = 19.2 MB < 25 MB). Final tracks are AAC m4a through Media Foundation (its AAC encoder only takes 44.1/48 kHz, so 16 kHz is upsampled to 48 kHz; 96 kbps is its minimum). Detection reads `CapabilityAccessManager\ConsentStore\microphone` (LastUsedTimeStop == 0 means in use). Groq key protected with DPAPI. .NET SDK 10.0.404 installed in `~/.dotnet` on the Mac.
- L5 (2026-11-17): Engram mirror `odd/windows-port/tasks` pending: mem_save refused with session_project_mismatch (session belongs to project "minutes"). Resync from a session started in this repo.
