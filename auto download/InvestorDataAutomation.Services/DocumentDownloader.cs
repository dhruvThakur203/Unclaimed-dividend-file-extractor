using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    public class DocumentDownloader : IDocumentDownloader
    {
        private const int SniffBytes = 1024;

        private readonly HttpClient _httpClient;
        private readonly ILogger<DocumentDownloader> _logger;
        private readonly CrawlOptions _options;

        public DocumentDownloader(HttpClient httpClient, ILogger<DocumentDownloader> logger, IOptions<CrawlOptions>? options = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _options = options?.Value ?? new CrawlOptions();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        /// <summary>
        /// Streams the document to "{target}.part" while hashing it, validates the content, then moves it
        /// into place. A failed, invalid or cancelled download never leaves a file that looks complete.
        /// Cancellation is propagated to the caller; every other failure is returned as an unsuccessful result.
        /// </summary>
        public async Task<DownloadResult> DownloadAsync(InvestorDocument document, string companyStoragePath, CancellationToken cancellationToken = default)
        {
            var url = UrlNormalizer.Redact(document.DocumentUrl);
            string? partialPath = null;
            try
            {
                _logger.LogInformation("Downloading document {Url} for company path {Path}", url, companyStoragePath);

                // Target: InvestorData/{CompanyName}/{DocumentType}/{Company}_{DocumentType}_{Period}.{extension}
                var docTypeDir = Path.Combine(companyStoragePath, SanitizePath(document.DocumentType));
                Directory.CreateDirectory(docTypeDir);

                var companyName = document.Company?.CompanyName ?? "Company";
                var period = !string.IsNullOrEmpty(document.Quarter) ? $"{document.Quarter}_{document.FinancialYear}" : document.FinancialYear;
                if (string.IsNullOrEmpty(period)) period = "General";

                var ext = NormalizeExtension(document.FileExtension);
                var baseName = SanitizePath($"{companyName}_{document.DocumentType}_{period}");

                using var fetch = await HttpFetch.GetAsync(_httpClient, document.DocumentUrl, HttpCompletionOption.ResponseHeadersRead,
                    TimeSpan.FromSeconds(Math.Max(1, _options.DownloadTimeoutSeconds)), _options.MaxRetryCount,
                    TimeSpan.FromMilliseconds(Math.Max(0, _options.RetryBaseDelayMilliseconds)), _logger, cancellationToken);

                if (!fetch.IsSuccess)
                {
                    var error = fetch.Error ?? $"HTTP {fetch.StatusCode}";
                    _logger.LogWarning("Failed to download document {Url} after {Attempts} attempt(s): {Error}", url, fetch.Attempts, error);
                    return Failure(error);
                }

                var mediaType = fetch.Response!.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected document {Url}: server returned {MediaType} instead of a document", url, mediaType);
                    return Failure($"Server returned {mediaType} instead of a document");
                }

                var destinationPath = ReserveDestination(docTypeDir, baseName, ext);
                partialPath = destinationPath + ".part";

                long totalBytes = 0;
                var head = new byte[SniffBytes];
                var headLength = 0;
                string hashString;
                await using (var inputStream = await fetch.Response.Content.ReadAsStreamAsync(fetch.BodyToken))
                await using (var outputStream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await inputStream.ReadAsync(buffer, fetch.BodyToken)) > 0)
                    {
                        if (headLength < SniffBytes)
                        {
                            var take = Math.Min(SniffBytes - headLength, bytesRead);
                            Array.Copy(buffer, 0, head, headLength, take);
                            headLength += take;
                        }
                        sha256.AppendData(buffer, 0, bytesRead);
                        await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), fetch.BodyToken);
                        totalBytes += bytesRead;
                    }
                    hashString = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
                }

                var invalidReason = ValidateContent(ext, head.AsSpan(0, headLength), totalBytes);
                if (invalidReason != null)
                {
                    _logger.LogWarning("Rejected document {Url}: {Reason}", url, invalidReason);
                    return Failure(invalidReason);
                }

                File.Move(partialPath, destinationPath);
                partialPath = null;
                var localFileName = Path.GetFileName(destinationPath);

                _logger.LogInformation("Successfully downloaded {FileName} ({Size:N0} bytes, SHA256: {Hash})", localFileName, totalBytes, hashString);
                return new DownloadResult(true, destinationPath, localFileName, totalBytes, hashString, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Download cancelled: {Url}", url);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to download document {Url}: {Error}", url, ex.Message);
                return Failure(ex is OperationCanceledException ? "Download timed out" : ex.Message);
            }
            finally
            {
                if (partialPath != null) TryDelete(partialPath);
            }
        }

        private static DownloadResult Failure(string error) => new(false, null, null, 0, null, error);

        /// <summary>Rejects empty files and files whose bytes contradict the expected format (e.g. an HTML error page saved as .pdf).</summary>
        internal static string? ValidateContent(string ext, ReadOnlySpan<byte> head, long totalBytes)
        {
            if (totalBytes == 0) return "Empty response body";

            if (ext == ".pdf")
            {
                var text = Encoding.ASCII.GetString(head);
                if (!text.Contains("%PDF-", StringComparison.Ordinal))
                    return "Content is not a PDF (missing %PDF header)";
                return null;
            }

            var start = Encoding.ASCII.GetString(head).TrimStart().ToLowerInvariant();
            if (start.StartsWith("<!doctype html", StringComparison.Ordinal) || start.StartsWith("<html", StringComparison.Ordinal))
                return "Content is an HTML page, not a document";
            return null;
        }

        /// <summary>Picks a file name that does not exist yet (and has no in-flight .part file).</summary>
        private static string ReserveDestination(string directory, string baseName, string ext)
        {
            var candidate = Path.Combine(directory, baseName + ext);
            for (var counter = 1; File.Exists(candidate) || File.Exists(candidate + ".part"); counter++)
            {
                candidate = Path.Combine(directory, $"{baseName}_{counter}{ext}");
            }
            return candidate;
        }

        private static string NormalizeExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return ".pdf";
            ext = ext.Trim().ToLowerInvariant();
            if (!ext.StartsWith('.')) ext = "." + ext;
            return Regex.IsMatch(ext, @"^\.[a-z0-9]{1,5}$") ? ext : ".pdf";
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Could not delete partial download {Path}: {Error}", path, ex.Message);
            }
        }

        /// <summary>Replaces invalid characters and blocks "."/".." so names can never escape their directory.</summary>
        internal static string SanitizePath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Document";
            var invalidChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct();
            foreach (var c in invalidChars)
            {
                input = input.Replace(c, '_');
            }
            input = Regex.Replace(input, @"\s+", "_").Trim('_', '.', ' ');
            if (input.Length > 150) input = input[..150];
            return string.IsNullOrEmpty(input) ? "Document" : input;
        }
    }
}
