# Minutes for Windows

**Record your video calls without bots, and get transcripts your AI agents can read.**

The Windows port of [Minutes](https://github.com/xabilarra/minutes). It lives in the notification area, notices when a call starts, records it **without joining the meeting**, transcribes it with Groq when you hang up and leaves everything on your PC as plain files, in the same layout as the Mac app.

> Early build. Recording, transcription, automatic start/stop, the floating pill and launch at login work; meeting video is not recorded yet.

## ⚡ Try it

1. Download **`Minutes.exe`** from the [latest release](https://github.com/xabilarra/minutes-windows/releases/tag/latest).
2. Open it. The file is not signed yet, so Windows SmartScreen may say *"Windows protected your PC"*: click **More info → Run anyway**.
3. Paste your [Groq API key](https://console.groq.com/keys) in the settings window that opens. It is encrypted for your Windows user.
4. Join a call. Minutes starts recording by itself; the pill at the top of the screen shows it.

Requirements: Windows 10 (2004) or Windows 11, 64-bit. Microphone access for desktop apps must be on (*Settings → Privacy & security → Microphone*).

## ✨ What it does

| | |
|---|---|
| 🎙️ **Starts by itself** | Detects Zoom, Teams, Slack, Webex, WhatsApp, Discord and Telegram, and Google Meet / Teams / Zoom / Whereby / Jitsi in Chrome, Edge, Brave, Firefox, Opera, Vivaldi or Arc. Stops 45 s after the app releases the microphone. |
| 🎧 **Two clean tracks** | Your microphone (**Yo**) and everything the PC plays (**Ellos**) are recorded separately, so the transcript knows who said what. |
| 📋 **Ready for your agent** | Markdown and JSON transcripts with timestamps. When it is ready, copy the text or the file path from the pill. |
| 🧹 **Clean transcripts** | Silences are not uploaded, Whisper hallucinations are dropped, the echo of your speakers is removed from your lines and your **Vocabulary** terms come out spelled right. |
| 🔒 **Local and private** | Recordings and transcripts stay on your PC; only the speech is sent to Groq. No account, no telemetry. |

## 📁 What you get

```text
%USERPROFILE%\Reuniones\2026-10-20 10.30 Weekly sync (Google Meet)\
├── yo.m4a               your microphone
├── ellos.m4a            the other participants
├── transcripcion.md     ready to paste into an agent
├── transcripcion.json   segments with exact start/end seconds and speaker
└── meta.json            app, title, start/end, duration, status
```

Folder, file and speaker names are Spanish, as in the Mac app: *Reuniones* = meetings, *transcripción* = transcript, **Yo** = me, **Ellos** = them.

## 🧪 Quick end-to-end check

Run `Minutes.exe --smoke-test 20` from a terminal (close the running Minutes first): it records 20 seconds of whatever is on (talk, play a video), transcribes them and shows the result. If something fails, the diagnostic log is at `%LOCALAPPDATA%\Minutes\logs\minutes.log` (it never contains your transcripts or your key).

## 🧠 How it works

- **Detection** reads the registry Windows keeps for its microphone privacy indicator (`CapabilityAccessManager\ConsentStore\microphone`) every second and matches the apps using the microphone against known meeting apps. Browsers count only when a window title looks like a meeting.
- **Audio** does not depend on how your devices are configured: every 2 seconds Minutes asks Windows which microphones and outputs other apps are using right now (their active audio sessions) and records all of them through WASAPI, mixed into one 16 kHz mono track each. **Yo** is every microphone another app (Teams, Chrome…) is capturing, or the default one when none is; **Ellos** is every output an app is playing to, plus the default output. Switching headphones mid-call is followed, and silences are kept on one clock so both tracks stay aligned.
- **Transcription** runs after the call with Groq `whisper-large-v3`: only the speech of each track is uploaded, in chunks of up to 10 minutes; failed meetings keep their audio and can be retried, and already transcribed chunks are not sent again.
- **Final audio** is encoded to AAC with Media Foundation.

## 👩‍💻 Development

```bash
dotnet test tests/Minutes.Core.Tests     # core logic, runs on Windows, macOS and Linux
dotnet build src/Minutes.App             # the Windows app (builds anywhere, runs on Windows)
dotnet publish src/Minutes.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

`Minutes.Core` is the platform-independent port of the Mac app's logic; `Minutes.App` is the WinForms tray app (NAudio for WASAPI and Media Foundation). Every push to `main` builds and tests on Windows in GitHub Actions and replaces the `latest` release.

## 🔒 Privacy and responsible use

Recording laws differ: in some places **every participant must consent**. Minutes does not announce itself in the call, so tell people you are recording.

## 📄 License

[MIT](LICENSE) © Xabi Larrañaga. The Whisper vocabulary in `src/Minutes.App/Resources` is © OpenAI, MIT (see its LICENSE file).
