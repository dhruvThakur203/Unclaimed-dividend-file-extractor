using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using InvestorDataAutomation.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace InvestorDataAutomation.Services
{
    public class CompanyWebsiteFinder : ICompanyWebsiteFinder
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CompanyWebsiteFinder> _logger;

        private static readonly string[] ExcludedDomains = new[]
        {
            "moneycontrol.com", "screener.in", "finance.yahoo.com", "yahoofinance.com", "yahoo.com",
            "wikipedia.org", "bseindia.com", "nseindia.com", "bloomberg.com", "reuters.com",
            "economictimes.indiatimes.com", "indiatimes.com", "livemint.com", "mint.com",
            "linkedin.com", "facebook.com", "twitter.com", "x.com", "youtube.com", "instagram.com",
            "google.com", "bing.com", "duckduckgo.com", "glassdoor.com", "ambitionbox.com",
            "trendlyne.com", "marketsmojo.com", "tickertape.in", "valueresearchonline.com"
        };

        public CompanyWebsiteFinder(HttpClient httpClient, ILogger<CompanyWebsiteFinder> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<WebsiteSearchResult> FindOfficialWebsiteAsync(string companyName, string? nseSymbol = null, string? bseCode = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(companyName))
            {
                return new WebsiteSearchResult(string.Empty, 0.0, string.Empty);
            }

            _logger.LogInformation("Discovering official website for company: {CompanyName}", companyName);

            // Strategy 1: Direct Domain Guess (e.g. Reliance Industries -> relianceindustries.com / ril.com, TCS -> tcs.com)
            var directCandidates = GenerateDirectCandidates(companyName, nseSymbol);
            foreach (var candidate in directCandidates)
            {
                if (await VerifyWebsiteAliveAsync(candidate, cancellationToken))
                {
                    double conf = CalculateDomainConfidence(candidate, companyName, nseSymbol);
                    if (conf >= 0.80)
                    {
                        _logger.LogInformation("Direct candidate domain verified: {Url} with confidence {Conf:P0}", candidate, conf);
                        return new WebsiteSearchResult(candidate, conf, GetDomainName(candidate));
                    }
                }
            }

            // Strategy 2: Web Search via DuckDuckGo HTML
            try
            {
                var query = Uri.EscapeDataString($"{companyName} official website");
                var searchUrl = $"https://html.duckduckgo.com/html/?q={query}";

                var response = await _httpClient.GetAsync(searchUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var linkNodes = doc.DocumentNode.SelectNodes("//a[@class='result__url'] | //a[contains(@class,'result__a')]");
                    if (linkNodes != null)
                    {
                        foreach (var node in linkNodes)
                        {
                            var href = node.GetAttributeValue("href", "");
                            var rawUrl = ExtractActualUrl(href);
                            if (string.IsNullOrWhiteSpace(rawUrl)) continue;

                            var domain = GetDomainName(rawUrl);
                            if (IsExcludedDomain(domain)) continue;

                            double conf = CalculateDomainConfidence(rawUrl, companyName, nseSymbol);
                            if (conf > 0.5)
                            {
                                var cleanUrl = EnsureHttpsUrl(rawUrl);
                                _logger.LogInformation("Web search identified official website: {Url} with confidence {Conf:P0}", cleanUrl, conf);
                                return new WebsiteSearchResult(cleanUrl, conf, domain);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web search failed for {CompanyName}, falling back to best direct candidate.", companyName);
            }

            // Fallback: Return best direct candidate if available
            if (directCandidates.Count > 0)
            {
                var fallback = directCandidates.First();
                return new WebsiteSearchResult(fallback, 0.60, GetDomainName(fallback));
            }

            return new WebsiteSearchResult(string.Empty, 0.0, string.Empty);
        }

        private static List<string> GenerateDirectCandidates(string companyName, string? nseSymbol)
        {
            var list = new List<string>();
            var cleanName = Regex.Replace(companyName.ToLowerInvariant(), @"\b(limited|ltd|inc|corp|corporation|industries|technologies|services|bank|group|india|pvt|private)\b", "").Trim();
            cleanName = Regex.Replace(cleanName, @"[^a-z0-9]", "");

            if (!string.IsNullOrEmpty(cleanName))
            {
                list.Add($"https://www.{cleanName}.com");
                list.Add($"https://www.{cleanName}.in");
                list.Add($"https://{cleanName}.com");
                list.Add($"https://{cleanName}.in");
            }

            if (!string.IsNullOrWhiteSpace(nseSymbol))
            {
                var sym = nseSymbol.ToLowerInvariant().Trim();
                list.Add($"https://www.{sym}.com");
                list.Add($"https://www.{sym}.in");
            }

            return list.Distinct().ToList();
        }

        private async Task<bool> VerifyWebsiteAliveAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                try
                {
                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    return response.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static double CalculateDomainConfidence(string url, string companyName, string? nseSymbol)
        {
            var domain = GetDomainName(url).ToLowerInvariant();
            if (IsExcludedDomain(domain)) return 0.0;

            var cleanCompany = Regex.Replace(companyName.ToLowerInvariant(), @"[^a-z0-9]", "");
            var cleanDomain = Regex.Replace(domain, @"[^a-z0-9]", "");

            if (cleanDomain.Contains(cleanCompany) || cleanCompany.Contains(cleanDomain))
            {
                return 0.95;
            }

            // Check key company tokens
            var tokens = companyName.ToLowerInvariant()
                .Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2 && !new[] { "limited", "ltd", "inc", "corp", "industries", "bank", "group", "india", "pvt" }.Contains(t))
                .ToList();

            int matchedTokens = tokens.Count(t => domain.Contains(t));
            if (tokens.Count > 0 && matchedTokens > 0)
            {
                double score = (double)matchedTokens / tokens.Count;
                return Math.Min(0.90, 0.60 + (score * 0.30));
            }

            if (!string.IsNullOrWhiteSpace(nseSymbol) && domain.Contains(nseSymbol.ToLowerInvariant()))
            {
                return 0.85;
            }

            return 0.65;
        }

        private static bool IsExcludedDomain(string domain)
        {
            return ExcludedDomains.Any(excluded => domain.EndsWith(excluded, StringComparison.OrdinalIgnoreCase) || domain.Contains(excluded, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetDomainName(string url)
        {
            try
            {
                var uri = new Uri(EnsureHttpsUrl(url));
                var host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);
                return host;
            }
            catch
            {
                return url;
            }
        }

        private static string ExtractActualUrl(string ddgHref)
        {
            if (string.IsNullOrWhiteSpace(ddgHref)) return string.Empty;
            if (ddgHref.Contains("uddg="))
            {
                var match = Regex.Match(ddgHref, @"uddg=([^&]+)");
                if (match.Success)
                {
                    return Uri.UnescapeDataString(match.Groups[1].Value);
                }
            }
            return ddgHref.StartsWith("http") ? ddgHref : $"https://{ddgHref.TrimStart('/')}";
        }

        private static string EnsureHttpsUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://{url}";
            }
            return url;
        }
    }
}
