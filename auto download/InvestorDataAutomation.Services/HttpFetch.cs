using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// GET with a per-request timeout and bounded retry. Only transient outcomes are retried:
    /// 408, 429, 500, 502, 503, 504, timeouts and connection failures. Permanent responses
    /// (404, 410, 401, 403 and other 4xx) return immediately so blocked or missing pages are never hammered.
    /// </summary>
    internal static class HttpFetch
    {
        public const int MaxAllowedRetries = 5;
        private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

        public static bool IsTransient(HttpStatusCode status) => (int)status is 408 or 429 or 500 or 502 or 503 or 504;

        public static async Task<FetchResult> GetAsync(
            HttpClient client,
            string url,
            HttpCompletionOption completion,
            TimeSpan timeout,
            int maxRetries,
            TimeSpan retryBaseDelay,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            maxRetries = Math.Clamp(maxRetries, 0, MaxAllowedRetries);
            string? lastError = null;
            int? lastStatus = null;

            for (var attempt = 1; ; attempt++)
            {
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                HttpResponseMessage? response = null;
                TimeSpan? retryAfter = null;
                try
                {
                    response = await client.GetAsync(url, completion, timeoutCts.Token);
                    if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode))
                    {
                        return new FetchResult(response, timeoutCts, attempt, (int)response.StatusCode, null);
                    }

                    lastStatus = (int)response.StatusCode;
                    lastError = $"HTTP {lastStatus}";
                    retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();
                    timeoutCts.Dispose();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    response?.Dispose();
                    timeoutCts.Dispose();
                    throw;
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
                {
                    response?.Dispose();
                    timeoutCts.Dispose();
                    lastStatus = null;
                    lastError = ex is OperationCanceledException ? $"timed out after {timeout.TotalSeconds:F0}s" : ex.Message;
                }

                if (attempt > maxRetries)
                {
                    return new FetchResult(null, null, attempt, lastStatus, lastError);
                }

                var delay = retryAfter is { } ra && ra > TimeSpan.Zero
                    ? (ra < MaxRetryAfter ? ra : MaxRetryAfter)
                    : retryBaseDelay * attempt;
                logger.LogInformation("[HTTP] {Error} for {Url}; retry {Attempt}/{Max} in {Delay} ms",
                    lastError, UrlNormalizer.Redact(url), attempt, maxRetries, (int)delay.TotalMilliseconds);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
        }

        /// <summary>Reads at most <paramref name="maxBytes"/> of the body as text; returns null when the body is larger.</summary>
        public static async Task<string?> ReadTextLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength is long declared && declared > maxBytes) return null;

            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            var charset = content.Headers.ContentType?.CharSet?.Trim('"');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>Outcome of <see cref="HttpFetch.GetAsync"/>. Owns the response and the timeout that also bounds body reads.</summary>
    internal sealed class FetchResult : IDisposable
    {
        private readonly CancellationTokenSource? _timeout;

        public FetchResult(HttpResponseMessage? response, CancellationTokenSource? timeout, int attempts, int? statusCode, string? error)
        {
            Response = response;
            _timeout = timeout;
            Attempts = attempts;
            StatusCode = statusCode;
            Error = error;
        }

        public HttpResponseMessage? Response { get; }
        public int Attempts { get; }
        public int? StatusCode { get; }
        public string? Error { get; }
        public bool IsSuccess => Response?.IsSuccessStatusCode == true;
        public CancellationToken BodyToken => _timeout?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            Response?.Dispose();
            _timeout?.Dispose();
        }
    }
}
