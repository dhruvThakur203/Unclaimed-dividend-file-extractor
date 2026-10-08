using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    public class InvestorPageFinder : IInvestorPageFinder
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<InvestorPageFinder> _logger;
        private readonly CrawlOptions _options;

        public InvestorPageFinder(HttpClient httpClient, ILogger<InvestorPageFinder> logger, IOptions<CrawlOptions>? options = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _options = options?.Value ?? new CrawlOptions();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<List<string>> FindInvestorPagesAsync(string websiteUrl, CancellationToken cancellationToken = default)
        {
            var candidates = await FindInvestorPageCandidatesAsync(websiteUrl, cancellationToken);
            return candidates.Select(c => c.Url).ToList();
        }

        /// <summary>
        /// Returns every seed (root, homepage links, standard-path guesses) with source and priority,
        /// de-duplicated by normalized URL keeping the strongest evidence, highest priority first.
        /// </summary>
        public async Task<List<InvestorPageCandidate>> FindInvestorPageCandidatesAsync(string websiteUrl, CancellationToken cancellationToken = default)
        {
            var candidates = new Dictionary<string, InvestorPageCandidate>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(websiteUrl)) return new List<InvestorPageCandidate>();

            var baseUri = GetBaseUri(websiteUrl);
            if (baseUri == null) return new List<InvestorPageCandidate>();

            void Add(InvestorPageCandidate candidate)
            {
                var key = UrlNormalizer.PageKey(candidate.Url);
                if (!candidates.TryGetValue(key, out var existing) || candidate.Priority > existing.Priority)
                    candidates[key] = candidate;
            }

            Add(CrawlPriority.ForSeed(baseUri.ToString(), InvestorPageSource.Homepage));

            // Common URL path patterns for Investor Relations
            var standardPaths = new[]
            {
                "/investors", "/investor", "/investor-relations", "/investorrelations", "/ir",
                "/shareholders", "/shareholder", "/shareholder-information", "/shareholder-info",
                "/corporate-access", "/investor-hub", "/investor-portal",
                "/investor_relations", "/financial-results", "/financials", "/annual-reports",
                "/quarterly-results", "/investor-information", "/disclosures",
                "/stock-info", "/stock-information", "/stock-price", "/stock-quote", "/stock-performance",
                "/historical-stock-price", "/stock-charts",
                "/share-price", "/share-data", "/shareholding-pattern", "/shareholder-structure",
                "/dividends", "/dividend-history", "/dividend-payout", "/unclaimed-dividends",
                "/transfer-agent", "/registrars", "/rtas", "/shareholder-services",
                "/agm", "/annual-general-meeting", "/egm", "/extraordinary-general-meeting",
                "/shareholder-meetings", "/postal-ballot",
                "/investor-faqs", "/contact-ir", "/investor-contacts"
            };

            foreach (var path in standardPaths)
            {
                Add(CrawlPriority.ForSeed(new Uri(baseUri, path).ToString(), InvestorPageSource.StandardPath));
            }

            var scope = SiteScope.FromUrl(baseUri.ToString())!;
            try
            {
                _logger.LogInformation("Scanning homepage for investor links: {WebsiteUrl}", websiteUrl);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));
                using var response = await _httpClient.GetAsync(baseUri, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(timeout.Token);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var aNodes = doc.DocumentNode.SelectNodes("//a[@href]");
                    if (aNodes != null)
                    {
                        var scoredLinks = new List<(string Url, int Score, string Anchor, string Title)>();

                        foreach (var node in aNodes)
                        {
                            var href = node.GetAttributeValue("href", "").Trim();
                            var text = node.InnerText.Trim().ToLowerInvariant();
                            var title = node.GetAttributeValue("title", "").ToLowerInvariant();

                            if (!UrlNormalizer.TryResolveHttp(baseUri, href, out var resolvedUri) || !scope.Contains(resolvedUri))
                                continue;

                            var urlStr = resolvedUri.ToString();
                            int score = InvestorRouteRelevance.ScoreHomepageLink(urlStr, text, title);

                            if (score > 0)
                            {
                                scoredLinks.Add((urlStr, score, HtmlEntity.DeEntitize(node.InnerText).Trim(), node.GetAttributeValue("title", "")));
                            }
                        }

                        var topLinks = scoredLinks.OrderByDescending(x => x.Score)
                                                  .DistinctBy(x => x.Url)
                                                  .Take(15);

                        foreach (var link in topLinks)
                        {
                            Add(CrawlPriority.ForSeed(link.Url, InvestorPageSource.HomepageLink, link.Anchor, link.Title));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to scan homepage links for {WebsiteUrl}; continuing with standard paths only: {Error}", websiteUrl, ex.Message);
            }

            return candidates.Values.OrderByDescending(c => c.Priority).ToList();
        }

        /// <summary>
        /// Picks the page to record as the company's investor page: a linked investor landing
        /// page (e.g. /investor) first, then the strongest linked page, then the website root.
        /// Unvalidated standard-path guesses are never chosen because they may not exist.
        /// </summary>
        public static string? SelectPrimaryInvestorPage(IEnumerable<InvestorPageCandidate> candidates)
        {
            var list = candidates.ToList();
            var linked = list.Where(c => c.Source is InvestorPageSource.HomepageLink or InvestorPageSource.CrawledLink).ToList();

            var hub = linked
                .Where(c => InvestorRouteRelevance.IsInvestorHubPath(c.Url))
                .OrderBy(c => InvestorRouteRelevance.GetPathSegments(c.Url).Count)
                .ThenByDescending(c => c.Priority)
                .FirstOrDefault();
            if (hub != null) return hub.Url;

            var bestLinked = linked.OrderByDescending(c => c.Priority).FirstOrDefault();
            if (bestLinked != null) return bestLinked.Url;

            return list.FirstOrDefault(c => c.Source == InvestorPageSource.Homepage)?.Url
                ?? list.FirstOrDefault()?.Url;
        }

        private static Uri? GetBaseUri(string url)
        {
            try
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = $"https://{url}";
                }
                return new Uri(url);
            }
            catch
            {
                return null;
            }
        }

    }
}
