using System;
using System.Text.RegularExpressions;

namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// URL identity rules used for de-duplication. Keys are compared ordinally.
    ///
    /// Both page and document keys:
    ///   - scheme is ignored (http and https of the same URL are one resource);
    ///   - host is lower-cased and a leading "www." is dropped; non-default ports are kept;
    ///   - the fragment is dropped (it never changes what the server returns);
    ///   - percent-encoding is decoded so "%20" and a raw space are equal;
    ///   - the query string is kept verbatim after decoding: parameter order, repetition and
    ///     case can be meaningful to the server, so they are never reordered or collapsed.
    ///
    /// Page keys additionally lower-case the path and trim a trailing slash, because investor menus
    /// link the same page with inconsistent casing and the cost of a merge is one skipped request.
    /// Document keys keep path case: file names on case-sensitive servers are distinct files.
    /// </summary>
    internal static class UrlNormalizer
    {
        public static string PageKey(string url)
        {
            if (!TryParseHttp(url, out var uri)) return url.Trim().TrimEnd('/');
            var path = Decode(uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
            return $"{Authority(uri)}{path}{Decode(uri.Query)}";
        }

        public static string DocumentKey(string url)
        {
            if (!TryParseHttp(url, out var uri)) return url.Trim();
            return $"{Authority(uri)}{Decode(uri.AbsolutePath)}{Decode(uri.Query)}";
        }

        public static bool TryParseHttp(string url, out Uri uri)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                uri = parsed;
                return true;
            }
            uri = null!;
            return false;
        }

        public static bool TryResolveHttp(Uri baseUri, string href, out Uri resolved)
        {
            resolved = null!;
            if (string.IsNullOrWhiteSpace(href)) return false;
            href = href.Trim();
            if (href.StartsWith('#')) return false;
            if (!Uri.TryCreate(baseUri, href, out var candidate)) return false;
            if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) return false;
            resolved = candidate;
            return true;
        }

        private static readonly Regex SecretQueryParam = new(
            @"(?<=[?&](?:api[_-]?key|apikey|key|token|access[_-]?token|auth|signature|sig|password|pwd|secret)=)[^&#]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Masks credential-like query parameters before a URL is written to logs.</summary>
        public static string Redact(string? url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? string.Empty;
            return SecretQueryParam.Replace(url, "***");
        }

        private static string Authority(Uri uri)
        {
            var host = uri.IdnHost.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try { return Uri.UnescapeDataString(value); }
            catch (UriFormatException) { return value; }
        }
    }

    /// <summary>
    /// Same-site policy for crawling HTML pages: the company host (ignoring "www.") and its
    /// subdomains (e.g. investors.company.com). Look-alike hosts such as evil-company.com or
    /// company.com.evil.com are out of scope. Document links found on in-scope pages may be
    /// hosted anywhere (CDNs, registrar portals) — they are evaluated by the target filter
    /// and downloaded, but their hosts are never crawled.
    /// </summary>
    internal sealed class SiteScope
    {
        private readonly string _rootHost;

        private SiteScope(string rootHost) => _rootHost = rootHost;

        public static SiteScope? FromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!url.Contains("://", StringComparison.Ordinal)) url = $"https://{url}";
            return UrlNormalizer.TryParseHttp(url, out var uri) ? new SiteScope(StripWww(uri.IdnHost)) : null;
        }

        public bool Contains(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = StripWww(uri.IdnHost);
            return host == _rootHost || host.EndsWith("." + _rootHost, StringComparison.Ordinal);
        }

        public bool Contains(string url) => UrlNormalizer.TryParseHttp(url, out var uri) && Contains(uri);

        private static string StripWww(string host)
        {
            host = host.ToLowerInvariant().TrimEnd('.');
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
    }
}
