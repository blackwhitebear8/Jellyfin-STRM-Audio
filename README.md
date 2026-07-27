<h1 align="center">Jellyfin STRM Audio</h1>

<p align="center">
  <img src="logo.png" alt="STRM Audio logo" width="160"/>
</p>

<p align="center">
Makes <code>.strm</code> files playable in Jellyfin <b>music libraries</b>, like in Emby.<br/>
Internet radio and remote audio files just work — the server fetches the stream and forwards it to your clients.
</p>

---

## Features

- Play `.strm` files pointing to internet radio stations (Icecast/Shoutcast) or remote audio files (mp3, aac, flac, ...) from a regular Jellyfin music library.
- **Server-side proxying**: clients only ever talk to your Jellyfin server. Stream URLs, credentials, and LAN addresses stay hidden, and LAN-only streams work remotely.
- **Automatic reconnect**: if the upstream stream drops or stalls (a short internet hiccup on the server), the plugin silently reconnects with backoff. If the stream stays down longer than the configured window (default 30s), the client connection is closed cleanly instead of hanging.
- **Fast start**: the stream is probed once (codec/container); subsequent playbacks skip the probe and start almost instantly.
- Passthrough for mp3/aac — no transcoding, near-zero CPU. ffmpeg transcoding remains available as a fallback for exotic codecs.
- Same security rule as Jellyfin core applies to video strm files: local file paths inside `.strm` files are rejected.

## Installation (via plugin repository — recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and click **+**.
2. Give it a name (e.g. `STRM Audio`) and enter this URL:

   ```
   https://raw.githubusercontent.com/blackwhitebear8/Jellyfin-STRM-Audio/main/manifest.json
   ```

3. Go to **Catalog**, find **STRM Audio** under *General*, and install it.
4. Restart your Jellyfin server.
5. The startup log should show `STRM Audio: decorator active - .strm audio support enabled`.

Updates published to this repository will show up in your dashboard automatically.

## Manual installation

1. Download the latest `strm-audio_<version>.zip` from the [Releases](https://github.com/blackwhitebear8/Jellyfin-STRM-Audio/releases) page.
2. Extract it into your Jellyfin plugin directory, e.g.
   `/config/data/plugins/STRM Audio_<version>/` (Docker) or
   `/var/lib/jellyfin/plugins/STRM Audio_<version>/` (native Linux).
3. Make sure the Jellyfin user can read *and write* the folder
   (Unraid: `chown -R 99:100 <folder>`).
4. Restart Jellyfin.

## Usage

Create a `.strm` file inside a music library folder. The first non-empty line
is the target URL; lines starting with `#` are ignored:

```
# One World Radio
https://example.com/stream/radio.mp3
```

Scan the library and press play. The first playback probes the stream and may
take a few seconds; after that, playback starts almost instantly.

## Configuration

Open **Dashboard -> Plugins -> STRM Audio** for the settings page. Changes
apply immediately to new playback sessions; no restart required.

| Setting | Default | Description |
|---|---|---|
| `EnableReconnect` | `true` | Route playback through the resilient reconnect proxy. When `false`, the core fetches the target URL directly. |
| `ReconnectWindowSeconds` | `300` | How long to keep retrying a dropped upstream before closing the client connection. |
| `UpstreamConnectTimeoutSeconds` | `15` | Timeout for connecting to the upstream stream. |
| `ReadTimeoutSeconds` | `20` | Max time to wait for new data before treating an open connection as stalled. |

## How it works

Jellyfin core only resolves `.strm` files for **video**; audio items keep the
local `.strm` path as their playback source, so playback fails
(see [jellyfin/jellyfin#8201](https://github.com/jellyfin/jellyfin/issues/8201)).

This plugin registers a decorator over Jellyfin's `IMediaSourceManager`
(plugin service registrations run after core registrations, so the last
registration wins). The decorator populates the item's shortcut path so
Jellyfin's remote probe analyzes the real stream, rewrites the media source to
an internal reconnect-capable proxy endpoint, and shapes the source
(container, codec, infinite-stream flag) so the stream builder serves it
through the server's progressive route.

## Compatibility

Built for **Jellyfin 10.11**. Because the plugin decorates a core service, it
must be rebuilt against the matching `Jellyfin.Controller` package after a
major Jellyfin upgrade (e.g. 10.12). On a version mismatch the plugin simply
fails to load and the server keeps working normally.

## Known issues

- Jellyfin's ATL tag reader logs a harmless `Non-negative number required`
  stack trace when it tries to parse the strm text file as audio. This is
  cosmetic and cannot be suppressed from a plugin.

## Building from source

Requires the .NET 9 SDK.

```bash
dotnet publish -c Release -o publish
```

To produce a release zip plus the checksum/timestamp for `manifest.json`:

```bash
./build-release.sh
```

## License

This project is licensed under the [GPL-3.0 License](LICENSE).
