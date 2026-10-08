using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    public class WebsiteCrawler : IWebsiteCrawler
    {
        /// <summary>HTML bodies larger than this are skipped; investor pages are far smaller.</summary>
        internal const int MaxHtmlBytes = 5 * 1024 * 1024;
        /// <summary>At most this many new child pages are queued from one page (highest priority first).</summary>
        internal const int MaxChildLinksPerPage = 100;
        /// <summary>Pending-queue cap per company, as a multiple of MaxPagesPerCompany.</summary>
        internal const int FrontierCapacityPerPage = 20;
        private const int MaxLinkContextChars = 300;

        private readonly HttpClient _httpClient;
        private readonly IDocumentDetector _documentDetector;
        private readonly IDocumentClassifier _documentClassifier;
        private readonly IFinancialPeriodDetector _periodDetector;
        private readonly ITargetDocumentRelevanceFilter _targetDocumentFilter;
        private readonly IOptions<CrawlOptions> _options;
        private readonly ILogger<WebsiteCrawler> _logger;
        private readonly IDynamicPageRenderer _renderer;

        public WebsiteCrawler(
            HttpClient httpClient,
            IDocumentDetector documentDetector,
            IDocumentClassifier documentClassifier,
            IFinancialPeriodDetector periodDetector,
            ITargetDocumentRelevanceFilter targetDocumentFilter,
            IOptions<CrawlOptions> options,
            ILogger<WebsiteCrawler> logger)
            : this(httpClient, documentDetector, documentClassifier, periodDetector, targetDocumentFilter, options, logger,
                   new PlaywrightPageRenderer(options.Value, logger))
        {
        }

        internal WebsiteCrawler(
            HttpClient httpClient,
            IDocumentDetector documentDetector,
            IDocumentClassifier documentClassifier,
            IFinancialPeriodDetector periodDetector,
            ITargetDocumentRelevanceFilter targetDocumentFilter,
            IOptions<CrawlOptions> options,
            ILogger<WebsiteCrawler> logger,
            IDynamicPageRenderer renderer)
        {
            _httpClient = httpClient;
            _documentDetector = documentDetector;
            _documentClassifier = documentClassifier;
            _periodDetector = periodDetector;
            _targetDocumentFilter = targetDocumentFilter;
            _options = options;
            _logger = logger;
            _renderer = renderer;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public CrawlAuditReport? LastCrawlAudit { get; private set; }

        public Task<List<DiscoveredDocument>> CrawlInvestorPagesAsync(Company company, List<string> investorPages, CancellationToken cancellationToken = default)
        {
            var candidates = investorPages
                .Select(url => new InvestorPageCandidate(url, InvestorPageSource.Provided, 0, "provided without discovery metadata"))
                .ToList();
            return CrawlInvestorPagesAsync(company, candidates, cancellationToken);
        }

        /// <summary>
        /// Best-first crawl: the highest-priority known page is fetched next. Only pages that load
        /// successfully count toward <see cref="CrawlOptions.MaxPagesPerCompany"/>; 404/410 pages are
        /// marked invalid and never expanded. A hard request cap (retries included) keeps the crawl bounded.
        /// </summary>
        public async Task<List<DiscoveredDocument>> CrawlInvestorPagesAsync(Company company, IReadOnlyList<InvestorPageCandidate> candidates, CancellationToken cancellationToken = default)
        {
            var options = _options.Value;
            var run = new CrawlRun(new CrawlAuditReport { CompanyName = company.CompanyName });
            LastCrawlAudit = run.Audit;

            var dynamicPages = new List<string>();
            var processedPages = new List<InvestorPageCandidate>();
            var maxPages = Math.Max(0, options.MaxPagesPerCompany);
            var maxDepth = Math.Max(1, options.MaxCrawlDepth);
            var frontier = new CrawlFrontier(Math.Max(candidates.Count, maxPages * FrontierCapacityPerPage));
            var scope = SiteScope.FromUrl(company.Website) ?? SiteScope.FromUrl(candidates.FirstOrDefault()?.Url);
            var requestTimeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
            var retryDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.RetryBaseDelayMilliseconds));

            foreach (var candidate in candidates)
            {
                if (scope != null && !scope.Contains(candidate.Url))
                {
                    run.Audit.PagesSkippedOffSite++;
                    continue;
                }
                frontier.TryAdd(candidate);
                run.Audit.CandidatePages.Add(new CrawlAuditCandidate(candidate.Url, candidate.Priority, candidate.Source.ToString(), candidate.Reason));
            }

            var maxRequests = maxPages * 2 + frontier.PendingCount;
            var requests = 0;

            _logger.LogInformation("Starting HTTP crawl for company {Company} with {Count} initial pages (page budget {MaxPages}, depth {MaxDepth}, request cap {MaxRequests})",
                company.CompanyName, frontier.PendingCount, maxPages, maxDepth, maxRequests);

            while (processedPages.Count < maxPages && requests < maxRequests && frontier.TryDequeue(out var page))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentUrl = page.Url;
                var depth = page.Depth;

                _logger.LogInformation("[CRAWL] Visiting priority={Priority} source={Source} depth={Depth} url={Url} reason={Reason}",
                    page.Priority, page.Source, depth, UrlNormalizer.Redact(currentUrl), page.Reason);

                using var fetch = await HttpFetch.GetAsync(_httpClient, currentUrl, HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout, options.MaxRetryCount, retryDelay, _logger, cancellationToken);
                requests += fetch.Attempts;

                if (!fetch.IsSuccess)
                {
                    RecordFailedPage(run.Audit, page, fetch);
                    continue;
                }

                var response = fetch.Response!;
                var finalUri = response.RequestMessage?.RequestUri ?? new Uri(currentUrl);
                if (scope != null && !scope.Contains(finalUri))
                {
                    _logger.LogWarning("[CRAWL] {Url} redirected off-site to {FinalUrl}; not parsed", UrlNormalizer.Redact(currentUrl), UrlNormalizer.Redact(finalUri.ToString()));
                    run.Audit.PagesSkippedOffSite++;
                    run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "redirected off-site", null));
                    continue;
                }
                if (UrlNormalizer.PageKey(finalUri.ToString()) != UrlNormalizer.PageKey(currentUrl))
                {
                    if (frontier.IsClosed(finalUri.ToString()))
                    {
                        _logger.LogDebug("[CRAWL] {Url} redirected to already processed {FinalUrl}", currentUrl, finalUri);
                        run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "redirect to visited page", null));
                        continue;
                    }
                    frontier.Close(finalUri.ToString());
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (_documentDetector.IsDocumentUrl(currentUrl, contentType))
                {
                    processedPages.Add(page);
                    run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "document", null));
                    var ext = _documentDetector.ExtrapolateExtension(currentUrl, contentType);
                    run.Audit.HttpDocumentCandidates++;
                    AddDiscoveredDoc(run, "http", currentUrl, currentUrl, string.Empty, ext, string.Empty, null, null);
                    continue;
                }

                if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, $"skipped content type '{contentType}'", null));
                    continue;
                }

                string? html;
                try
                {
                    html = await HttpFetch.ReadTextLimitedAsync(response.Content, MaxHtmlBytes, fetch.BodyToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException)
                {
                    run.Audit.PagesFailed++;
                    _logger.LogWarning("[CRAWL] Failed reading body of {Url}: {Error}", UrlNormalizer.Redact(currentUrl), ex.Message);
                    run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "body read failed", null));
                    continue;
                }
                if (html == null)
                {
                    _logger.LogWarning("[CRAWL] Skipping {Url}: HTML larger than {Limit} bytes", UrlNormalizer.Redact(currentUrl), MaxHtmlBytes);
                    run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "html too large", null));
                    continue;
                }

                processedPages.Add(page);
                var assessment = ProcessHtmlPage(run, page, finalUri, html, depth < maxDepth ? frontier : null, scope);
                run.Audit.VisitedPages.Add(new CrawlAuditPage(currentUrl, page.Priority, page.Source.ToString(), depth, fetch.StatusCode, fetch.Attempts, "processed", assessment.RequiresRendering));

                if (assessment.RequiresRendering && !dynamicPages.Contains(currentUrl, StringComparer.OrdinalIgnoreCase))
                {
                    dynamicPages.Add(currentUrl);
                }
            }

            run.Audit.PagesProcessed = processedPages.Count;
            run.Audit.HttpRequests = requests;
            run.Audit.PagesLeftInQueue = frontier.PendingCount;
            run.Audit.StopReason = processedPages.Count >= maxPages ? "page budget reached"
                : requests >= maxRequests ? "request cap reached"
                : "queue exhausted";
            run.Audit.DynamicPages.AddRange(dynamicPages);

            _logger.LogInformation(
                "HTTP crawl completed for {Company}: {Pages} pages processed, {Invalid} not found, {Failed} failed, {Requests} requests, {Pending} still queued ({StopReason}), found {Docs} documents, {Dynamic} dynamic pages flagged",
                company.CompanyName, processedPages.Count, run.Audit.PagesNotFound, run.Audit.PagesFailed, requests, frontier.PendingCount,
                run.Audit.StopReason, run.Documents.Count, dynamicPages.Count);

            await RenderDynamicPagesAsync(company, run, dynamicPages, processedPages, candidates, cancellationToken);

            return run.Documents.ToList();
        }

        private void RecordFailedPage(CrawlAuditReport audit, InvestorPageCandidate page, FetchResult fetch)
        {
            var url = UrlNormalizer.Redact(page.Url);
            string outcome;
            if (fetch.StatusCode is 404 or 410)
            {
                audit.PagesNotFound++;
                outcome = "not found";
                if (page.IsGuessedPath)
                    _logger.LogDebug("[CRAWL] Guessed path does not exist ({Status}): {Url}", fetch.StatusCode, url);
                else
                    _logger.LogWarning("[CRAWL] Linked page not found ({Status}): {Url} (linked from {Parent})", fetch.StatusCode, url, UrlNormalizer.Redact(page.ParentUrl ?? "seed list"));
            }
            else if (fetch.StatusCode is 401 or 403)
            {
                audit.PagesFailed++;
                outcome = "access denied";
                _logger.LogWarning("[CRAWL] Access denied ({Status}), possibly bot protection; not retried: {Url}", fetch.StatusCode, url);
            }
            else
            {
                audit.PagesFailed++;
                outcome = fetch.Error ?? $"HTTP {fetch.StatusCode}";
                _logger.LogWarning("[CRAWL] Page failed after {Attempts} attempt(s) ({Error}): {Url}", fetch.Attempts, outcome, url);
            }
            audit.VisitedPages.Add(new CrawlAuditPage(page.Url, page.Priority, page.Source.ToString(), page.Depth, fetch.StatusCode, fetch.Attempts, outcome, null));
        }

        /// <summary>Extracts document candidates and child pages from one HTML page and assesses whether it needs rendering.</summary>
        private DynamicPageAssessment ProcessHtmlPage(CrawlRun run, InvestorPageCandidate page, Uri pageUri, string html, CrawlFrontier? frontier, SiteScope? scope)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var pageTitle = HtmlEntity.DeEntitize(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "").Trim();
            var docsBeforePage = run.Documents.Count;
            var children = new List<InvestorPageCandidate>();

            var aNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (aNodes != null)
            {
                foreach (var node in aNodes)
                {
                    if (!UrlNormalizer.TryResolveHttp(pageUri, HtmlEntity.DeEntitize(node.GetAttributeValue("href", "")), out var resolvedUri))
                        continue;

                    var targetUrl = resolvedUri.ToString();
                    var anchorText = HtmlEntity.DeEntitize(node.InnerText).Trim();
                    var titleAttr = HtmlEntity.DeEntitize(node.GetAttributeValue("title", "")).Trim();

                    if (_documentDetector.IsDocumentUrl(targetUrl, anchorText))
                    {
                        run.Audit.HttpDocumentCandidates++;
                        var ext = _documentDetector.ExtrapolateExtension(targetUrl, "");
                        AddDiscoveredDoc(run, "http", targetUrl, page.Url, anchorText, ext, pageTitle, titleAttr, GetLinkContext(node));
                    }
                    else if (frontier != null
                             && (scope?.Contains(resolvedUri) ?? resolvedUri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
                             && !frontier.IsClosed(targetUrl)
                             && InvestorRouteRelevance.ShouldFollowCrawlLink(page.Url, targetUrl, anchorText, titleAttr))
                    {
                        children.Add(CrawlPriority.ForChild(page, targetUrl, anchorText, titleAttr, page.Depth + 1));
                    }
                }
            }

            if (frontier != null)
            {
                foreach (var child in children.OrderByDescending(c => c.Priority).Take(MaxChildLinksPerPage))
                {
                    if (frontier.TryAdd(child))
                        _logger.LogDebug("[CRAWL] Queued priority={Priority} url={Url} reason={Reason}", child.Priority, UrlNormalizer.Redact(child.Url), child.Reason);
                }
            }

            var assessment = DynamicPageAssessor.Assess(page.Url, html, doc);
            if (assessment.RequiresRendering)
            {
                _logger.LogInformation(
                    "[DYNAMIC ASSESSMENT] Page: {Url} HTTP target docs: {HttpDocs} Dynamic: TRUE Reason: {Reasons}",
                    UrlNormalizer.Redact(page.Url), run.Documents.Count - docsBeforePage, string.Join("; ", assessment.Reasons));
            }
            return assessment;
        }

        /// <summary>
        /// Playwright runs only for pages the assessor flagged (capped by MaxDynamicPagesToRender), or — when
        /// HTTP found no target document at all — once on the best validated investor page. Static pages that
        /// already yielded documents never pay the browser cost.
        /// </summary>
        private async Task RenderDynamicPagesAsync(
            Company company,
            CrawlRun run,
            List<string> dynamicPages,
            List<InvestorPageCandidate> processedPages,
            IReadOnlyList<InvestorPageCandidate> candidates,
            CancellationToken cancellationToken)
        {
            var options = _options.Value;
            if (!options.EnablePlaywrightFallback)
            {
                run.Audit.PlaywrightDecision = "disabled by configuration";
                return;
            }

            var pagesToRender = SelectPagesToRender(dynamicPages, run.Documents.Count, processedPages, candidates, options.MaxDynamicPagesToRender, out var decision);
            run.Audit.PlaywrightDecision = decision;
            _logger.LogInformation("[PLAYWRIGHT DECISION] {Company}: {Decision}", company.CompanyName, decision);
            if (pagesToRender.Count == 0) return;

            var docsBeforeRender = run.Documents.Count;
            var stopwatch = Stopwatch.StartNew();
            var batch = await _renderer.RenderAsync(pagesToRender, cancellationToken);
            run.Audit.BrowserLaunches += batch.BrowserLaunches;

            foreach (var rendered in batch.Pages)
            {
                run.Audit.RenderedPages.Add(rendered.Url);
                var pageUri = new Uri(rendered.Url);
                var linkCount = 0;
                var docCandidates = 0;
                var docsBefore = run.Documents.Count;
                foreach (var link in rendered.Links)
                {
                    linkCount++;
                    if (!UrlNormalizer.TryResolveHttp(pageUri, link.Href ?? "", out var resolvedUri)) continue;

                    var absUrl = resolvedUri.ToString();
                    if (!_documentDetector.IsDocumentUrl(absUrl, link.Text ?? "")) continue;

                    docCandidates++;
                    run.Audit.RenderedDocumentCandidates++;
                    var ext = _documentDetector.ExtrapolateExtension(absUrl, "");
                    AddDiscoveredDoc(run, "playwright", absUrl, rendered.Url, link.Text ?? "", ext, rendered.Title,
                        link.Title, TruncateContext(link.Context));
                }

                _logger.LogInformation(
                    "Playwright rendered {Url}: {Links} links, {Candidates} document candidates, {Added} new target documents",
                    UrlNormalizer.Redact(rendered.Url), linkCount, docCandidates, run.Documents.Count - docsBefore);
            }

            run.Audit.DynamicDiscoveryTime = stopwatch.Elapsed;
            _logger.LogInformation(
                "HTTP + Playwright merge for {Company}: {HttpDocs} from HTTP, {RenderedDocs} added by Playwright, {Unique} unique candidates evaluated, {Duplicates} duplicates dropped, {Total} target documents ({Seconds:F1}s in browser)",
                company.CompanyName, docsBeforeRender, run.Documents.Count - docsBeforeRender, run.SeenDocumentKeys.Count,
                run.Audit.DuplicateCandidates, run.Documents.Count, stopwatch.Elapsed.TotalSeconds);
        }

        internal static List<string> SelectPagesToRender(
            IReadOnlyList<string> dynamicPages,
            int httpTargetDocuments,
            IReadOnlyList<InvestorPageCandidate> processedPages,
            IReadOnlyList<InvestorPageCandidate> candidates,
            int maxDynamicPagesToRender,
            out string decision)
        {
            var limit = Math.Max(0, maxDynamicPagesToRender);
            if (dynamicPages.Count > 0)
            {
                var selected = dynamicPages.Take(limit).ToList();
                decision = $"render {selected.Count} of {dynamicPages.Count} dynamic page(s) flagged by assessment"
                           + (dynamicPages.Count > limit ? $" (capped by MaxDynamicPagesToRender={limit})" : "");
                return selected;
            }

            if (httpTargetDocuments > 0)
            {
                decision = $"not needed: no dynamic evidence and HTTP found {httpTargetDocuments} target document(s)";
                return new List<string>();
            }

            if (limit == 0)
            {
                decision = "not run: MaxDynamicPagesToRender=0";
                return new List<string>();
            }

            var fallback = InvestorPageFinder.SelectPrimaryInvestorPage(processedPages)
                ?? candidates.FirstOrDefault(c => c.Source == InvestorPageSource.Homepage)?.Url
                ?? candidates.FirstOrDefault()?.Url;
            if (fallback == null)
            {
                decision = "not run: no page available";
                return new List<string>();
            }

            decision = $"fallback: HTTP found no target documents; rendering best validated page {UrlNormalizer.Redact(fallback)}";
            return new List<string> { fallback };
        }

        private void AddDiscoveredDoc(
            CrawlRun run,
            string origin,
            string url,
            string sourcePageUrl,
            string anchorText,
            string ext,
            string pageTitle,
            string? linkTitle,
            string? surroundingContext)
        {
            if (!run.SeenDocumentKeys.Add(UrlNormalizer.DocumentKey(url)))
            {
                run.Audit.DuplicateCandidates++;
                return;
            }

            var filename = SafeFileName(url);
            var filterResult = _targetDocumentFilter.Evaluate(new TargetDocumentCandidate(
                url,
                filename,
                anchorText,
                linkTitle,
                pageTitle,
                surroundingContext));

            run.Audit.Documents.Add(new CrawlAuditDocument(url, sourcePageUrl, origin, filterResult.Score, filterResult.Action.ToString(), filterResult.Reason));
            switch (filterResult.Action)
            {
                case TargetDocumentFilterAction.Download: run.Audit.FilterDownload++; break;
                case TargetDocumentFilterAction.PossibleCandidate: run.Audit.FilterPossible++; return;
                default: run.Audit.FilterIgnored++; return;
            }

            var docType = _documentClassifier.Classify(filename, url, anchorText, pageTitle).ToString();
            var (fy, quarter, docDate) = _periodDetector.ExtractPeriod(anchorText, filename, url);

            var title = !string.IsNullOrWhiteSpace(anchorText) ? anchorText : filename;
            if (string.IsNullOrWhiteSpace(title)) title = $"{docType} {fy} {quarter}".Trim();

            run.Documents.Add(new DiscoveredDocument(
                Title: title,
                Url: url,
                SourcePageUrl: sourcePageUrl,
                FileExtension: ext,
                DocumentType: docType,
                FinancialYear: fy,
                Quarter: quarter,
                DocumentDate: docDate
            ));
        }

        private static string SafeFileName(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
            var name = Path.GetFileName(uri.AbsolutePath);
            try { return Uri.UnescapeDataString(name); }
            catch (UriFormatException) { return name; }
        }

        /// <summary>
        /// Text local to a link: its list item / table row / paragraph, or its parent when short.
        /// Large containers are ignored so page headings and menus do not leak onto every document.
        /// </summary>
        private static string? GetLinkContext(HtmlNode anchor)
        {
            var container = anchor.Ancestors().FirstOrDefault(n => n.Name is "li" or "tr" or "p" or "dd" or "td")
                            ?? anchor.ParentNode;
            if (container == null) return null;

            // Headings are page/section titles, not link context; script/style are never text.
            var text = string.Join(" ", container.Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Text && !HasExcludedAncestor(n, container))
                .Select(n => n.InnerText));
            text = Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
            return text.Length is > 0 and <= MaxLinkContextChars ? text : null;
        }

        private static bool HasExcludedAncestor(HtmlNode node, HtmlNode container)
        {
            for (var parent = node.ParentNode; parent != null && parent != container; parent = parent.ParentNode)
            {
                if (parent.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "script" or "style") return true;
            }
            return false;
        }

        private static string? TruncateContext(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }

        private sealed class CrawlRun
        {
            public CrawlRun(CrawlAuditReport audit) => Audit = audit;

            public CrawlAuditReport Audit { get; }
            public List<DiscoveredDocument> Documents { get; } = new();
            public HashSet<string> SeenDocumentKeys { get; } = new(StringComparer.Ordinal);
        }
    }
}
