using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmAudio
{
    /// <summary>
    /// Decorator around Jellyfin's default <see cref="IMediaSourceManager"/>.
    ///
    /// Jellyfin only resolves .strm files for video (BaseItem.GetVersionInfo checks
    /// for Video items exclusively). Audio items keep the local .strm path as their
    /// playback source, so ffmpeg/clients end up trying to play a text file.
    ///
    /// This decorator:
    ///  1. populates item.ShortcutPath with the URL from the .strm file before
    ///     probing, so Jellyfin's remote probe (ffprobe) runs against the actual
    ///     stream instead of the text file;
    ///  2. skips the forced re-probe on subsequent playbacks once media info is
    ///     known, because probing a live stream takes many seconds;
    ///  3. rewrites the media source path/protocol to the target URL after the
    ///     sources are built, and shapes the source so the server proxies the
    ///     stream to the client (no redirect, no HLS machinery).
    /// </summary>
    public sealed class StrmAudioMediaSourceManager : IMediaSourceManager
    {
        private readonly IMediaSourceManager _inner;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<StrmAudioMediaSourceManager> _logger;
        private readonly ConcurrentDictionary<Guid, byte> _backgroundProbes = new();

        public StrmAudioMediaSourceManager(
            IMediaSourceManager inner,
            IFileSystem fileSystem,
            ILogger<StrmAudioMediaSourceManager> logger)
        {
            _inner = inner;
            _fileSystem = fileSystem;
            _logger = logger;
            _logger.LogInformation("STRM Audio: decorator active - .strm audio support enabled");
        }

        // ------------------------------------------------------------------
        //  Overridden members
        // ------------------------------------------------------------------

        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            EnsureShortcutPath(item);

            // The core forces a full metadata refresh with a remote content probe on
            // EVERY playback of a .strm item. For live streams that probe takes many
            // seconds, and the web client fires several requests per playback, so
            // first plays time out and fail until the probe has finished. Playback
            // does not actually need the probe (FixSources supplies a synthetic
            // stream and container), so NEVER probe inline: play immediately and,
            // for unknown items, refresh the real media info in the background.
            if (allowMediaProbe && IsStrmAudio(item))
            {
                allowMediaProbe = false;

                if (!HasKnownAudioStream(item))
                {
                    QueueBackgroundProbe(item);
                }
            }

            var sources = await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            FixSources(item, sources);
            return sources;
        }

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
            BaseItem item,
            bool enablePathSubstitution,
            User? user = null)
        {
            EnsureShortcutPath(item);

            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            FixSources(item, sources);
            return sources;
        }

        public async Task<MediaSourceInfo> GetMediaSource(
            BaseItem item,
            string mediaSourceId,
            string liveStreamId,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            EnsureShortcutPath(item);

            var source = await _inner
                .GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            if (source is not null)
            {
                FixSources(item, new[] { source });
            }

            return source!;
        }

        // ------------------------------------------------------------------
        //  STRM logic
        // ------------------------------------------------------------------

        private static bool IsStrmAudio(BaseItem item)
            => item is MediaBrowser.Controller.Entities.Audio.Audio
               && StrmFile.IsStrm(item.Path);

        /// <summary>
        /// Runs the remote content probe for an item in the background (once per
        /// item at a time), so real media info lands in the database without
        /// delaying the first playback.
        /// </summary>
        private void QueueBackgroundProbe(BaseItem item)
        {
            if (!_backgroundProbes.TryAdd(item.Id, 0))
            {
                return;
            }

            _logger.LogInformation("STRM Audio: probing stream info for {Id} in the background", item.Id);

            _ = Task.Run(async () =>
            {
                try
                {
                    await item.RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    _logger.LogInformation("STRM Audio: background probe finished for {Id}", item.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "STRM Audio: background probe failed for {Id}", item.Id);
                }
                finally
                {
                    _backgroundProbes.TryRemove(item.Id, out _);
                }
            });
        }

        /// <summary>
        /// Returns true when the database already contains a probed audio stream
        /// for this item, meaning the expensive remote probe can be skipped.
        /// </summary>
        private bool HasKnownAudioStream(BaseItem item)
        {
            try
            {
                foreach (var stream in _inner.GetMediaStreams(item.Id))
                {
                    if (stream?.Type == MediaStreamType.Audio)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "STRM Audio: failed to read stored media streams for {Id}", item.Id);
            }

            return false;
        }

        /// <summary>
        /// Ensures an audio item with a .strm path has its ShortcutPath populated so
        /// the built-in remote content probe (ffprobe) analyzes the target URL
        /// instead of the text file itself.
        /// </summary>
        private void EnsureShortcutPath(BaseItem item)
        {
            if (!IsStrmAudio(item) || !string.IsNullOrWhiteSpace(item.ShortcutPath))
            {
                return;
            }

            var target = StrmFile.ReadTarget(item.Path!);
            if (string.IsNullOrWhiteSpace(target))
            {
                _logger.LogWarning("STRM Audio: could not read a target from {Path}", item.Path);
                return;
            }

            item.IsShortcut = true;
            item.ShortcutPath = target;

            _logger.LogDebug("STRM Audio: target for {Path} = {Target}", item.Path, Sanitize(target));
        }

        private void FixSources(BaseItem item, IEnumerable<MediaSourceInfo> sources)
        {
            if (!IsStrmAudio(item))
            {
                return;
            }

            var target = item.ShortcutPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = StrmFile.ReadTarget(item.Path!);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var protocol = _inner.GetPathProtocol(target);

            // Same security rule the core applies to video .strm files: never allow
            // local file paths inside .strm files (arbitrary file read).
            if (protocol == MediaProtocol.File)
            {
                _logger.LogWarning(
                    "STRM Audio: ignoring local file path inside {Path} for security reasons",
                    item.Path);
                return;
            }

            foreach (var source in sources)
            {
                if (source is null
                    || source.Protocol != MediaProtocol.File
                    || !StrmFile.IsStrm(source.Path))
                {
                    continue;
                }

                // Route playback through the plugin's internal reconnect proxy so a
                // dropped upstream is retried transparently. The probe still uses the
                // real target (ShortcutPath); only the playback path is rewritten.
                var config = Plugin.Instance?.Configuration;
                if (config is null || config.EnableReconnect)
                {
                    // Sentinel URL intercepted in-process by StrmAudioReconnectHandler
                    // on the default HttpClient. It never touches the network, so it
                    // is immune to "Require HTTPS" redirects and TLS certificates.
                    source.Path = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"http://{StrmAudioReconnectHandler.SentinelHost}/{item.Id:N}?token={ProxySecret.Value}");
                }
                else
                {
                    source.Path = target;
                }

                source.Protocol = MediaProtocol.Http;

                // DELIBERATELY false: with a remote direct-play source the universal
                // audio endpoint issues a 302 redirect so the client connects to the
                // stream itself. With IsRemote=false that redirect is skipped and the
                // endpoint falls back to the progressive route, where the SERVER
                // fetches the stream (protocol stays Http, so the static remote
                // handler uses the internal HttpClient) and forwards the bytes to
                // the client. Credentials and LAN addresses in stream URLs therefore
                // stay hidden from clients.
                source.IsRemote = false;
                source.Size = null;

                if (string.Equals(source.Container, "strm", StringComparison.OrdinalIgnoreCase))
                {
                    source.Container = null;
                }

                // Derive the container from the URL extension (e.g. .mp3) so the
                // player and the transcoder know what to expect.
                var extension = GetUrlExtension(target);
                if (string.IsNullOrEmpty(source.Container) && extension is not null)
                {
                    source.Container = extension;
                }

                // Still unknown (extension-less URL, not yet probed): assume mp3 so
                // the stream builder picks direct play. Clients sniff the actual
                // bytes anyway, and the background probe corrects the metadata for
                // subsequent playbacks.
                if (string.IsNullOrEmpty(source.Container))
                {
                    source.Container = "mp3";
                }

                // Guarantee a known audio stream; without MediaStreams the stream
                // builder cannot produce a play/transcode plan and playback fails.
                var hasAudioStream = false;
                if (source.MediaStreams is not null)
                {
                    foreach (var stream in source.MediaStreams)
                    {
                        if (stream?.Type == MediaStreamType.Audio)
                        {
                            hasAudioStream = true;
                            break;
                        }
                    }
                }

                if (!hasAudioStream)
                {
                    var streams = source.MediaStreams is null
                        ? new List<MediaStream>()
                        : new List<MediaStream>(source.MediaStreams);

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = streams.Count,
                        Codec = GuessAudioCodec(extension)
                    });

                    source.MediaStreams = streams;
                }

                // Direct play ON with a matching container/codec: the stream builder
                // then picks PlayMethod=DirectPlay, and since IsRemote=false there is
                // no redirect - the universal endpoint serves the client through the
                // progressive route where the server fetches and forwards the stream
                // (passthrough, no transcode needed for mp3/aac). Without a known
                // duration the source is marked infinite so clients treat it as live.
                if (source.IsInfiniteStream || !source.RunTimeTicks.HasValue || source.RunTimeTicks.Value <= 0)
                {
                    source.IsInfiniteStream = true;
                    source.RunTimeTicks = null;
                }

                source.SupportsDirectPlay = true;
                source.SupportsDirectStream = true;

                _logger.LogDebug(
                    "STRM Audio: source {Id} rewritten to {Target} ({Protocol})",
                    source.Id,
                    Sanitize(target),
                    protocol.ToString());

                _logger.LogInformation(
                    "STRM Audio: source {Id}: container={Container}, protocol={Protocol}, remote={Remote}, streams={Streams}, infinite={Infinite}, directplay={Dp}, directstream={Ds}, transcoding={St}",
                    source.Id,
                    source.Container ?? "(empty)",
                    source.Protocol.ToString(),
                    source.IsRemote,
                    source.MediaStreams?.Count ?? 0,
                    source.IsInfiniteStream,
                    source.SupportsDirectPlay,
                    source.SupportsDirectStream,
                    source.SupportsTranscoding);
            }
        }

        /// <summary>
        /// Strips the query string (which may contain credentials) from a URL
        /// before it is written to the log.
        /// </summary>
        private static string Sanitize(string target)
        {
            var queryIndex = target.IndexOf('?', StringComparison.Ordinal);
            return queryIndex >= 0 ? target[..queryIndex] + "?..." : target;
        }

        /// <summary>
        /// Extracts the file extension (lowercase, without the dot) from a URL, or null.
        /// </summary>
        private static string? GetUrlExtension(string target)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var extension = System.IO.Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length < 2)
            {
                return null;
            }

            return extension.TrimStart('.').ToLowerInvariant();
        }

        /// <summary>
        /// Guesses the audio codec from the URL extension. Falls back to mp3,
        /// the most common format for (radio) streams.
        /// </summary>
        private static string GuessAudioCodec(string? extension)
            => extension switch
            {
                "mp3" => "mp3",
                "aac" or "m4a" or "m4b" => "aac",
                "flac" => "flac",
                "ogg" or "oga" => "vorbis",
                "opus" => "opus",
                "wav" => "pcm_s16le",
                "wma" => "wmav2",
                _ => "mp3"
            };

        // ------------------------------------------------------------------
        //  Pure pass-through members
        // ------------------------------------------------------------------

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
            => _inner.AddParts(providers);

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
            => _inner.GetMediaStreams(itemId);

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
            => _inner.GetMediaStreams(query);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
            => _inner.GetMediaAttachments(itemId);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
            => _inner.GetMediaAttachments(query);

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
            => _inner.OpenLiveStream(request, cancellationToken);

        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
            => _inner.OpenLiveStreamInternal(request, cancellationToken);

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStream(id, cancellationToken);

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

        public ILiveStream GetLiveStreamInfo(string id)
            => _inner.GetLiveStreamInfo(id);

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
            => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
            => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

        public Task CloseLiveStream(string id)
            => _inner.CloseLiveStream(id);

        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

        public bool SupportsDirectStream(string path, MediaProtocol protocol)
            => _inner.SupportsDirectStream(path, protocol);

        public MediaProtocol GetPathProtocol(string path)
            => _inner.GetPathProtocol(path);

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
            => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
            => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    }
}
