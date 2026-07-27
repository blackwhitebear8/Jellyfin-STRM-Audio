using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmAudio
{
    /// <summary>
    /// Per-process secret embedded in the sentinel URL. Defense in depth: only
    /// media sources rewritten by this plugin carry a valid token.
    /// </summary>
    internal static class ProxySecret
    {
        public static readonly string Value = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// DelegatingHandler installed on Jellyfin's default named HttpClient.
    ///
    /// The plugin rewrites .strm audio sources to a sentinel URL
    /// (http://strm-audio.invalid/{itemId}?token=...). When the core's static
    /// remote stream handler fetches that URL through the default HttpClient,
    /// this handler intercepts it IN-PROCESS - no loopback connection, no DNS,
    /// no TLS, and therefore immune to "Require HTTPS" redirects and certificate
    /// validation. It connects to the actual stream target and returns a
    /// response whose body transparently reconnects when the upstream drops.
    /// </summary>
    public sealed class StrmAudioReconnectHandler : DelegatingHandler
    {
        /// <summary>
        /// Reserved, non-resolvable host used to mark plugin-proxied streams.
        /// </summary>
        public const string SentinelHost = "strm-audio.invalid";

        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StrmAudioReconnectHandler> _logger;

        public StrmAudioReconnectHandler(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<StrmAudioReconnectHandler> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null || !string.Equals(uri.Host, SentinelHost, StringComparison.OrdinalIgnoreCase))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (!TryGetQueryValue(uri.Query, "token", out var token)
                || !string.Equals(token, ProxySecret.Value, StringComparison.Ordinal))
            {
                _logger.LogWarning("STRM Audio: sentinel request with invalid token rejected");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            }

            if (!Guid.TryParse(uri.AbsolutePath.Trim('/'), out var itemId))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is not MediaBrowser.Controller.Entities.Audio.Audio || !StrmFile.IsStrm(item.Path))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            var target = item.ShortcutPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = StrmFile.ReadTarget(item.Path!);
            }

            // Only http(s) targets; never local file paths.
            if (string.IsNullOrWhiteSpace(target)
                || !(target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var connectTimeout = TimeSpan.FromSeconds(Math.Max(1, config.UpstreamConnectTimeoutSeconds));

            // Fetching the real target through the same named client is safe: its
            // host differs from the sentinel, so it passes straight through this
            // handler to the network.
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            HttpResponseMessage upstream;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(connectTimeout);
                upstream = await client
                    .GetAsync(target, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "STRM Audio: initial connect to upstream failed for item {ItemId}", itemId);
                return new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = request };
            }

            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "STRM Audio: upstream returned {Status} for item {ItemId}",
                    (int)upstream.StatusCode,
                    itemId);
                upstream.Dispose();
                return new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = request };
            }

            var contentType = upstream.Content.Headers.ContentType;
            var upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var reconnectStream = new ReconnectStream(
                upstream,
                upstreamStream,
                target,
                client,
                config,
                itemId,
                _logger);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            var content = new StreamContent(reconnectStream);
            content.Headers.ContentType = contentType ?? new MediaTypeHeaderValue("audio/mpeg");
            response.Content = content;
            return response;
        }

        private static bool TryGetQueryValue(string query, string key, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var idx = pair.IndexOf('=', StringComparison.Ordinal);
                if (idx <= 0)
                {
                    continue;
                }

                if (string.Equals(pair[..idx], key, StringComparison.OrdinalIgnoreCase))
                {
                    value = Uri.UnescapeDataString(pair[(idx + 1)..]);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Read-only stream over an upstream HTTP stream that transparently
    /// reconnects when the upstream drops or stalls. When the upstream cannot
    /// be restored within the configured reconnect window, an IOException is
    /// thrown so the response copy aborts and the client is cut off cleanly.
    /// </summary>
    internal sealed class ReconnectStream : Stream
    {
        private readonly string _target;
        private readonly HttpClient _client;
        private readonly Guid _itemId;
        private readonly ILogger _logger;
        private readonly TimeSpan _reconnectWindow;
        private readonly TimeSpan _connectTimeout;
        private readonly TimeSpan _readTimeout;

        private HttpResponseMessage? _currentResponse;
        private Stream? _currentStream;
        private DateTime? _downSince;
        private int _attempt;

        public ReconnectStream(
            HttpResponseMessage firstResponse,
            Stream firstStream,
            string target,
            HttpClient client,
            PluginConfiguration config,
            Guid itemId,
            ILogger logger)
        {
            _currentResponse = firstResponse;
            _currentStream = firstStream;
            _target = target;
            _client = client;
            _itemId = itemId;
            _logger = logger;
            _reconnectWindow = TimeSpan.FromSeconds(Math.Max(1, config.ReconnectWindowSeconds));
            _connectTimeout = TimeSpan.FromSeconds(Math.Max(1, config.UpstreamConnectTimeoutSeconds));
            _readTimeout = TimeSpan.FromSeconds(Math.Max(1, config.ReadTimeoutSeconds));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_currentStream is not null)
                {
                    try
                    {
                        int read;
                        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            readCts.CancelAfter(_readTimeout);
                            read = await _currentStream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                        }

                        if (read > 0)
                        {
                            _downSince = null;
                            _attempt = 0;
                            return read;
                        }

                        // Zero bytes on a live stream means the upstream ended/dropped.
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "STRM Audio: upstream read failed for item {ItemId}", _itemId);
                    }

                    DisposeCurrent();
                }

                _downSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _downSince > _reconnectWindow)
                {
                    _logger.LogWarning(
                        "STRM Audio: upstream for item {ItemId} unavailable for more than {Window}s, cutting off client playback",
                        _itemId,
                        (int)_reconnectWindow.TotalSeconds);
                    throw new IOException("Upstream stream unavailable, reconnect window exceeded.");
                }

                _attempt++;
                var delaySeconds = Math.Min(5, _attempt);
                _logger.LogInformation(
                    "STRM Audio: upstream for item {ItemId} dropped, reconnect attempt {Attempt} in {Delay}s",
                    _itemId,
                    _attempt,
                    delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);

                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(_connectTimeout);

                    var response = await _client
                        .GetAsync(_target, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    _currentResponse = response;
                    _currentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "STRM Audio: reconnected to upstream for item {ItemId} after {Attempt} attempt(s)",
                        _itemId,
                        _attempt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "STRM Audio: reconnect attempt failed for item {ItemId}", _itemId);
                }
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCurrent();
            }

            base.Dispose(disposing);
        }

        private void DisposeCurrent()
        {
            try
            {
                _currentStream?.Dispose();
            }
            catch (Exception)
            {
                // Ignore teardown failures on an already broken stream.
            }

            _currentStream = null;

            try
            {
                _currentResponse?.Dispose();
            }
            catch (Exception)
            {
                // Ignore teardown failures on an already broken response.
            }

            _currentResponse = null;
        }
    }
}
