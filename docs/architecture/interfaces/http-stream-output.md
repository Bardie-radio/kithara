# HTTP Stream Output

```mermaid
sequenceDiagram
  participant Player as VLC
  participant StreamSrv as Stream_Server
  participant Neck

  Player->>StreamSrv: GET /stream/friday-jazz
  StreamSrv->>StreamSrv: ICY headers + audio
  Neck->>StreamSrv: metadata update StreamTitle
```

`GET /stream/{slug}` — Kithara Stream Server ICY-over-HTTP output.

## Response headers

```
Content-Type: audio/mpeg
icy-name: Friday Night Jazz
icy-genre: Bardie
```

When the client sends `Icy-MetaData: 1` (VLC / most legacy players), also:

```
icy-metaint: 8192
```

Inline metadata blocks: `StreamTitle='Artist - Title';`

Clients that **omit** `Icy-MetaData: 1` (HTML5 `<audio>`, many browsers) receive **plain MP3** with no in-band blocks — browsers decode ICY bytes as audio and stutter.

## Auth by playback mode

| Mode | Request |
|------|---------|
| public | `GET /stream/{slug}` |
| protected | `GET /stream/{slug}?token=...` (MVP) |
| private | Bearer session or 403 for anonymous players |

## Legacy player notes

- Paste full URL including query token into VLC / VRChat
- Listen tokens are Kithara-owned Struna secrets (may appear in logs — rotate when practical)
- Private + OIDC not supported in external players
- Static bots (Beak) use module-managed user credentials or protected Struna with known token

**Related:** [domains/struna-access.md](../domains/struna-access.md) · [ADR 002](../adrs/002-kithara-native-ffmpeg-streaming.md)

**Read next:** [streaming-stack.md](streaming-stack.md)
