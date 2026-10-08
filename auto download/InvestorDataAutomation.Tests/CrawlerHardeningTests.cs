using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using InvestorDataAutomation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace InvestorDataAutomation.Tests
{
    /// <summary>Scriptable website: per-path responders, request log, everything else 404.</summary>
    internal sealed class ScriptedSite : HttpMessageHandler
    {
        public const string Root = "https://company.com";
        private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Requests { get; } = new();

        public ScriptedSite Html(string path, string html) => Route(path, (req, _) => Task.FromResult(Ok(req, html, "text/html")));

        public ScriptedSite Route(string path, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _routes[UrlNormalizer.PageKey(path.StartsWith("http") ? path : Root + path)] = responder;
            return this;
        }

        public ScriptedSite Status(string path, HttpStatusCode status)
            => Route(path, (req, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = req }));

        public int Count(string path) => Requests.Count(r => UrlNormalizer.PageKey(r) == UrlNormalizer.PageKey(path.StartsWith("http") ? path : Root + path));

        public static HttpResponseMessage Ok(HttpRequestMessage req, string body, string mediaType)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType), RequestMessage = req };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Enqueue(url);
            return _routes.TryGetValue(UrlNormalizer.PageKey(url), out var responder)
                ? responder(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    internal sealed class FakeRenderer : IDynamicPageRenderer
    {
        private readonly Func<string, RenderedPage?> _render;
        public List<string> Requested { get; } = new();
        public int Calls { get; private set; }

        public FakeRenderer(Func<string, RenderedPage?>? render = null)
            => _render = render ?? (url => new RenderedPage(url, "", Array.Empty<RenderedLink>()));

        public Task<RenderBatchResult> RenderAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
        {
            Calls++;
            Requested.AddRange(urls);
            var pages = urls.Select(_render).Where(p => p != null).Cast<RenderedPage>().ToList();
            return Task.FromResult(new RenderBatchResult(pages, 1));
        }

        public static RenderedLink Link(string href, string text, string? context = null) => new() { Href = href, Text = text, Context = context };
    }

    internal static class CrawlerFactory
    {
        public static CrawlOptions Options(int maxPages = 50, int maxDepth = 4, int maxDynamic = 5, int retries = 0, bool playwright = true) => new()
        {
            MaxPagesPerCompany = maxPages,
            MaxCrawlDepth = maxDepth,
            MaxDynamicPagesToRender = maxDynamic,
            MaxRetryCount = retries,
            RetryBaseDelayMilliseconds = 0,
            RequestTimeoutSeconds = 5,
            EnablePlaywrightFallback = playwright,
            TargetDocumentScoreThreshold = 70,
            TargetDocumentPossibleCandidateMinScore = 40
        };

        public static WebsiteCrawler Create(HttpMessageHandler handler, CrawlOptions options, IDynamicPageRenderer? renderer = null)
        {
            var opts = Microsoft.Extensions.Options.Options.Create(options);
            return new WebsiteCrawler(
                new HttpClient(handler),
                new DocumentDetector(),
                new DocumentClassifier(),
                new FinancialPeriodDetector(),
                new TargetDocumentRelevanceFilter(opts, NullLogger<TargetDocumentRelevanceFilter>.Instance),
                opts,
                NullLogger<WebsiteCrawler>.Instance,
                renderer ?? new FakeRenderer());
        }

        public static List<InvestorPageCandidate> Seeds(params string[] paths)
        {
            var seeds = new List<InvestorPageCandidate> { CrawlPriority.ForSeed(ScriptedSite.Root + "/", InvestorPageSource.Homepage) };
            seeds.AddRange(paths.Select(p => CrawlPriority.ForSeed(ScriptedSite.Root + p, InvestorPageSource.HomepageLink, p.Trim('/').Replace('-', ' '))));
            return seeds;
        }

        public static Company Company() => new() { Id = 1, CompanyName = "Company Ltd", Website = ScriptedSite.Root };
    }

    public class DynamicDiscoveryPatternTests
    {
        private const string UnclaimedPath = "/investor/unclaimed-dividend";

        private static DynamicPageAssessment Assess(string url, string html)
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            return DynamicPageAssessor.Assess(url, html, doc);
        }

        private static async Task<(List<DiscoveredDocument> Docs, FakeRenderer Renderer, WebsiteCrawler Crawler)> CrawlAsync(
            ScriptedSite site, FakeRenderer? renderer = null, CrawlOptions? options = null, params string[] seeds)
        {
            renderer ??= new FakeRenderer();
            var crawler = CrawlerFactory.Create(site, options ?? CrawlerFactory.Options(), renderer);
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds(seeds));
            return (docs, renderer, crawler);
        }

        [Fact]
        public async Task PatternA_StaticPdfLinksOnly_ShouldStayHttpOnly()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed Dividend</title></head><body><ul>
                    <li><a href="/files/unclaimed-dividend-2019-20.pdf">Unclaimed Dividend 2019-20</a></li>
                    <li><a href="/files/iepf-transfer-2020.pdf">Shares transferred to IEPF 2020</a></li>
                    </ul></body></html>
                    """);

            var (docs, renderer, crawler) = await CrawlAsync(site, seeds: UnclaimedPath);

            Assert.Equal(2, docs.Count);
            Assert.Equal(0, renderer.Calls);
            Assert.StartsWith("not needed", crawler.LastCrawlAudit!.PlaywrightDecision);
        }

        [Fact]
        public async Task PatternB_VueBindingsAndReportContainer_ShouldRenderThatExactPage()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed Dividend and Shares</title></head><body>
                    <div id="vue-Reports" data-reporttype="unclaimed-dividend"><a :href="pdf.fullpath">PDF</a></div>
                    </body></html>
                    """);

            var (_, renderer, _) = await CrawlAsync(site, seeds: UnclaimedPath);

            Assert.Equal(new[] { ScriptedSite.Root + UnclaimedPath }, renderer.Requested);
        }

        [Theory]
        [InlineData("<div id=\"__next\"><main></main></div>")]
        [InlineData("<div id=\"app\" data-reactroot=\"\"></div>")]
        public void PatternC_ReactContainerWithTargetIdentity_ShouldRequireRendering(string container)
        {
            var html = $"<html><head><title>IEPF - Unclaimed Shares</title></head><body>{container}</body></html>";
            var result = Assess(ScriptedSite.Root + "/investor/iepf", html);

            Assert.True(result.RequiresRendering);
            Assert.Contains("Dynamic report container detected", result.Reasons);
        }

        [Theory]
        [InlineData("<div id=\"vue-Reports\" data-reporttype=\"results\"><a :href=\"x\">PDF</a></div>")]
        [InlineData("<div id=\"__next\"></div>")]
        public async Task PatternD_TargetKeywordsOnlyInNavigation_ShouldNotLaunchPlaywright(string dynamicBody)
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor/financial-results", $"""
                    <html><head><title>Financial Results</title></head><body>
                    <nav><a href="/about">About</a><a href="/careers">Unclaimed Dividend and IEPF</a></nav>
                    {dynamicBody}
                    <a href="/files/q1-results.pdf">Q1 Results</a>
                    </body></html>
                    """);

            var (_, renderer, crawler) = await CrawlAsync(site, seeds: "/investor/financial-results");

            Assert.DoesNotContain(renderer.Requested, u => u.Contains("financial-results"));
            Assert.Empty(crawler.LastCrawlAudit!.DynamicPages);
        }

        [Theory]
        [InlineData("https://company.com/investor/unclaimed-dividend", "<title>Notices</title>", "")]
        [InlineData("https://company.com/investor/page-1", "<title>Unclaimed Dividend</title>", "")]
        [InlineData("https://company.com/investor/page-2", "<title>Notices</title>", "<h1>Transfer of shares to IEPF</h1>")]
        [InlineData("https://company.com/investor/page-3", "<title>Notices</title><meta name=\"description\" content=\"Unpaid dividend details\">", "")]
        public void PatternE_TargetInUrlTitleHeadingOrDescription_TriggersOnlyWithDynamicEvidence(string url, string head, string body)
        {
            var dynamicHtml = $"<html><head>{head}</head><body>{body}<div id=\"vue-Reports\"><a :href=\"p.url\">PDF</a></div></body></html>";
            var staticHtml = $"<html><head>{head}</head><body>{body}<a href=\"/a.pdf\">PDF</a></body></html>";

            Assert.True(Assess(url, dynamicHtml).RequiresRendering);
            Assert.False(Assess(url, staticHtml).RequiresRendering);
        }

        [Fact]
        public async Task PatternF_TwoStaticDocsPlusDynamicDocuments_ShouldStillRenderAndMerge()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed Dividend</title></head><body>
                    <ul><li><a href="/files/unclaimed-dividend-2017.pdf">Unclaimed Dividend 2017</a></li>
                    <li><a href="/files/unclaimed-dividend-2018.pdf">Unclaimed Dividend 2018</a></li></ul>
                    <div id="vue-Reports" data-reporttype="unclaimed-dividend"><a :href="p.url">PDF</a></div>
                    </body></html>
                    """);
            var renderer = new FakeRenderer(url => new RenderedPage(url, "Unclaimed Dividend",
                Enumerable.Range(1, 100)
                    .Select(i => FakeRenderer.Link($"{ScriptedSite.Root}/files/iepf/Details%20of%20Unclaimed%20Dividend%20{i}.pdf", $"Details of Unclaimed Dividend {i}"))
                    .Append(FakeRenderer.Link($"{ScriptedSite.Root}/files/unclaimed-dividend-2017.pdf", "Unclaimed Dividend 2017"))
                    .ToList()));

            var (docs, _, crawler) = await CrawlAsync(site, renderer, seeds: UnclaimedPath);

            Assert.Equal(1, renderer.Calls);
            Assert.Equal(102, docs.Count);
            Assert.Equal(1, crawler.LastCrawlAudit!.DuplicateCandidates);
        }

        [Fact]
        public async Task PatternG_FiftyStaticDocsWithoutDynamicEvidence_ShouldNotLaunchPlaywright()
        {
            var links = string.Join("", Enumerable.Range(1, 50).Select(i => $"<li><a href=\"/files/unclaimed-dividend-{i}.pdf\">Unclaimed dividend list {i}</a></li>"));
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, $"<html><head><title>Unclaimed Dividend</title></head><body><ul>{links}</ul></body></html>");

            var (docs, renderer, _) = await CrawlAsync(site, seeds: UnclaimedPath);

            Assert.Equal(50, docs.Count);
            Assert.Equal(0, renderer.Calls);
        }

        [Fact]
        public async Task PatternH_SameDocumentFromHttpPlaywrightAndOtherSources_ShouldBeOneCandidate()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed Dividend</title></head><body>
                    <ul><li><a href="/Investors/Unclaimed Dividend/IEPF-2.pdf">IEPF-2</a></li></ul>
                    <div id="vue-Reports" data-reporttype="unclaimed-dividend"><a :href="p.url">PDF</a></div>
                    </body></html>
                    """);
            var renderer = new FakeRenderer(url => new RenderedPage(url, "Unclaimed Dividend", new List<RenderedLink>
            {
                FakeRenderer.Link("https://company.com/Investors/Unclaimed%20Dividend/IEPF-2.pdf", "IEPF-2"),
                FakeRenderer.Link("http://www.company.com/Investors/Unclaimed%20Dividend/IEPF-2.pdf#page=2", "IEPF-2"),
            }));

            var (docs, _, crawler) = await CrawlAsync(site, renderer, seeds: UnclaimedPath);

            Assert.Single(docs);
            Assert.Equal(2, crawler.LastCrawlAudit!.DuplicateCandidates);
        }

        [Fact]
        public async Task PatternI_RelativeAndAbsoluteUrlsOfSameDocument_ShouldBeOneCandidate()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed</title></head><body><ul>
                    <li><a href="unclaimed-dividend-2020.pdf">Unclaimed dividend 2020</a></li>
                    <li><a href="/investor/unclaimed-dividend-2020.pdf">Unclaimed dividend 2020</a></li>
                    <li><a href="https://company.com/investor/unclaimed-dividend-2020.pdf">Unclaimed dividend 2020</a></li>
                    </ul></body></html>
                    """);

            var (docs, _, _) = await CrawlAsync(site, seeds: UnclaimedPath);

            Assert.Single(docs);
            Assert.Equal("https://company.com/investor/unclaimed-dividend-2020.pdf", docs[0].Url);
        }

        [Fact]
        public async Task PatternJ_FragmentsOfSameDocument_ShouldBeOneDownloadableDocument()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, """
                    <html><head><title>Unclaimed</title></head><body><ul>
                    <li><a href="/files/unclaimed-dividend.pdf#section1">Unclaimed dividend part 1</a></li>
                    <li><a href="/files/unclaimed-dividend.pdf#section2">Unclaimed dividend part 2</a></li>
                    </ul></body></html>
                    """);

            var (docs, _, _) = await CrawlAsync(site, seeds: UnclaimedPath);

            Assert.Single(docs);
        }

        [Fact]
        public async Task MaxDynamicPagesToRender_ShouldCapRenderedPages()
        {
            var site = new ScriptedSite().Html("/", "<html><body>home</body></html>");
            var paths = Enumerable.Range(1, 4).Select(i => $"/investor/unclaimed-dividend-{i}").ToArray();
            foreach (var path in paths)
            {
                site.Html(path, "<html><head><title>Unclaimed Dividend</title></head><body><div id=\"vue-Reports\"><a :href=\"p\">x</a></div></body></html>");
            }

            var (_, renderer, crawler) = await CrawlAsync(site, options: CrawlerFactory.Options(maxDynamic: 2), seeds: paths);

            Assert.Equal(4, crawler.LastCrawlAudit!.DynamicPages.Count);
            Assert.Equal(2, renderer.Requested.Count);
            Assert.Contains("capped by MaxDynamicPagesToRender=2", crawler.LastCrawlAudit.PlaywrightDecision);
        }

        [Fact]
        public async Task Fallback_WhenHttpFindsNothing_ShouldRenderBestValidatedInvestorPageNotHomepage()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body><a href=\"/investor\">Investors</a></body></html>")
                .Html("/investor", "<html><head><title>Investors</title></head><body>nothing static</body></html>");

            var (_, renderer, _) = await CrawlAsync(site, seeds: "/investor");

            Assert.Equal(new[] { ScriptedSite.Root + "/investor" }, renderer.Requested);
        }

        [Fact]
        public async Task PlaywrightDisabled_ShouldNeverRender()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html(UnclaimedPath, "<html><head><title>Unclaimed Dividend</title></head><body><div id=\"vue-Reports\"><a :href=\"p\">x</a></div></body></html>");

            var (_, renderer, _) = await CrawlAsync(site, options: CrawlerFactory.Options(playwright: false), seeds: UnclaimedPath);

            Assert.Equal(0, renderer.Calls);
        }
    }

    public class TargetFilterRegressionTests
    {
        private readonly TargetDocumentRelevanceFilter _filter = new(
            Options.Create(new CrawlOptions { TargetDocumentScoreThreshold = 70, TargetDocumentPossibleCandidateMinScore = 40 }),
            NullLogger<TargetDocumentRelevanceFilter>.Instance);

        private TargetDocumentFilterResult Evaluate(string title, string? pageTitle = null, string? context = null)
        {
            var file = title.Replace(' ', '_') + ".pdf";
            return _filter.Evaluate(new TargetDocumentCandidate($"https://company.com/files/{Uri.EscapeDataString(file)}", file, title, null, pageTitle, context));
        }

        [Theory]
        [InlineData("IEPF")]
        [InlineData("Unclaimed Dividend")]
        [InlineData("Unpaid Dividend")]
        [InlineData("Unclaimed Shares")]
        [InlineData("Shares liable to IEPF")]
        [InlineData("IEPF-2")]
        [InlineData("Folio-wise Unclaimed Dividend")]
        public void TargetDocuments_ShouldDownload(string title)
        {
            Assert.Equal(TargetDocumentFilterAction.Download, Evaluate(title).Action);
        }

        [Theory]
        [InlineData("Annual Report")]
        [InlineData("Investor Presentation")]
        [InlineData("Financial Results")]
        [InlineData("Corporate Governance")]
        [InlineData("Company Profile")]
        [InlineData("Dividend Policy")]
        [InlineData("Share Price")]
        [InlineData("Board Committee")]
        [InlineData("General Notices")]
        public void NonTargetDocuments_ShouldNotDownload(string title)
        {
            Assert.NotEqual(TargetDocumentFilterAction.Download, Evaluate(title).Action);
        }

        [Theory]
        [InlineData("Annual Report")]
        [InlineData("Board Committees")]
        [InlineData("Dividend Policy")]
        public void PageTitleAlone_ShouldNotQualifyUnrelatedDocument(string title)
        {
            var result = Evaluate(title, pageTitle: "Unclaimed Dividend and Shares | IEPF");
            Assert.NotEqual(TargetDocumentFilterAction.Download, result.Action);
        }

        [Theory]
        [InlineData("IEPF-2")]
        [InlineData("Details of Unclaimed Dividend FY2025")]
        [InlineData("Shares proposed to be transferred to IEPF")]
        public void TargetDocumentsOnTargetPage_ShouldDownload(string title)
        {
            Assert.Equal(TargetDocumentFilterAction.Download, Evaluate(title, pageTitle: "Unclaimed Dividend and Shares").Action);
        }

        [Fact]
        public void AccordionSectionContext_ShouldQualifyGenericallyNamedDocument()
        {
            var result = _filter.Evaluate(new TargetDocumentCandidate(
                "https://company.com/files/Notice.pdf", "Notice.pdf", "Notice", null, "Investor Information",
                "Shares proposed to be transferred to IEPF Notice"));
            Assert.Equal(TargetDocumentFilterAction.Download, result.Action);
        }

        [Fact]
        public async Task CrawledUnclaimedPage_ShouldNotLetPageHeadingQualifyNavigationPdfs()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor/unclaimed-dividend", """
                    <html><head><title>Unclaimed Dividend and Shares</title></head><body>
                    <nav><ul><li><a href="/files/Board-Committees.pdf">Board Committees</a></li>
                    <li><a href="/files/Price-List.pdf">Price List</a></li></ul></nav>
                    <div><h1>Unclaimed Dividend and Shares</h1><a href="/files/Code-of-Conduct.pdf">Code of Conduct</a></div>
                    <ul><li><a href="/files/IEPF-2.pdf">IEPF-2</a></li>
                    <li><a href="/files/Details-of-Unclaimed-Dividend-FY2025.pdf">Details of Unclaimed Dividend FY2025</a></li></ul>
                    </body></html>
                    """);

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options());
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor/unclaimed-dividend"));

            var files = docs.Select(d => Path.GetFileName(new Uri(d.Url).AbsolutePath)).OrderBy(f => f).ToList();
            Assert.Equal(new[] { "Details-of-Unclaimed-Dividend-FY2025.pdf", "IEPF-2.pdf" }, files);
            var fy = docs.Single(d => d.Url.Contains("FY2025")).FinancialYear;
            Assert.Equal("FY2025", fy);
        }
    }

    public class FinancialPeriodRegressionTests
    {
        private readonly FinancialPeriodDetector _detector = new();

        [Theory]
        [InlineData("Form IEPF-2", "107-formiepf2finalsigneddated05102016.pdf", "https://company.com/Unclaimed/Statement/2016/107-formiepf2finalsigneddated05102016.pdf", "FY2016")]
        [InlineData("Details of Unclaimed Dividend (Final FY 24-25)", "Details of Unclaimed Dividend (Final FY 24-25).pdf", "https://company.com/d.pdf", "FY2024")]
        [InlineData("Unclaimed dividend", "Unclaimed_Dividend_FY2023.pdf", "https://company.com/Unclaimed_Dividend_FY2023.pdf", "FY2023")]
        [InlineData("Unclaimed dividend 2019-20", "list.pdf", "https://company.com/list.pdf", "FY2019-20")]
        [InlineData("Notify 2023 shareholders", "notice.pdf", "https://company.com/notice.pdf", "FY2023")]
        [InlineData("Modify 25 records", "notice.pdf", "https://company.com/notice.pdf", null)]
        public void ExtractPeriod_ShouldNotMisreadFoldersFileNumbersOrWords(string text, string file, string url, string? expected)
        {
            Assert.Equal(expected, _detector.ExtractPeriod(text, file, url).FinancialYear);
        }
    }

    public class UrlNormalizationTests
    {
        [Theory]
        [InlineData("http://company.com/a/b.pdf", "https://company.com/a/b.pdf")]
        [InlineData("https://company.com/a/My Report.pdf", "https://company.com/a/My%20Report.pdf")]
        [InlineData("https://company.com/a/b.pdf#page=3", "https://company.com/a/b.pdf")]
        [InlineData("https://WWW.Company.com/a/b.pdf", "https://company.com/a/b.pdf")]
        [InlineData("https://company.com:443/a/b.pdf", "https://company.com/a/b.pdf")]
        public void DocumentKey_ShouldTreatEquivalentUrlsAsOne(string a, string b)
        {
            Assert.Equal(UrlNormalizer.DocumentKey(a), UrlNormalizer.DocumentKey(b));
        }

        [Theory]
        [InlineData("https://company.com/Files/Report.pdf", "https://company.com/files/report.pdf")]
        [InlineData("https://company.com/get.pdf?id=A1", "https://company.com/get.pdf?id=a1")]
        [InlineData("https://company.com/get.pdf?a=1&b=2", "https://company.com/get.pdf?b=2&a=1")]
        [InlineData("https://company.com/get.pdf?a=1&a=1", "https://company.com/get.pdf?a=1")]
        [InlineData("https://company.com/a.pdf", "https://company.com:8443/a.pdf")]
        public void DocumentKey_ShouldPreserveCaseQueryAndPortDifferences(string a, string b)
        {
            Assert.NotEqual(UrlNormalizer.DocumentKey(a), UrlNormalizer.DocumentKey(b));
        }

        [Theory]
        [InlineData("https://company.com/investor/", "https://company.com/investor")]
        [InlineData("https://company.com/Investor", "https://company.com/investor")]
        [InlineData("https://company.com/investor#top", "http://www.company.com/investor")]
        [InlineData("https://company.com/Investor%20Info", "https://company.com/investor info")]
        public void PageKey_ShouldMergeTrailingSlashCaseFragmentAndEncoding(string a, string b)
        {
            Assert.Equal(UrlNormalizer.PageKey(a), UrlNormalizer.PageKey(b));
        }

        [Fact]
        public void PageKey_ShouldKeepQueryCase()
        {
            Assert.NotEqual(UrlNormalizer.PageKey("https://company.com/p?id=Ab"), UrlNormalizer.PageKey("https://company.com/p?id=ab"));
        }

        [Fact]
        public void Redact_ShouldMaskCredentialQueryParameters()
        {
            var redacted = UrlNormalizer.Redact("https://company.com/graphql?apikey=SECRET123&type=reports&token=abc");
            Assert.DoesNotContain("SECRET123", redacted);
            Assert.DoesNotContain("abc", redacted);
            Assert.Contains("type=reports", redacted);
        }
    }

    public class DomainSafetyTests
    {
        private static readonly SiteScope Scope = SiteScope.FromUrl("https://company.com")!;

        [Theory]
        [InlineData("https://company.com/document.pdf", true)]
        [InlineData("https://www.company.com/document.pdf", true)]
        [InlineData("https://subdomain.company.com/document.pdf", true)]
        [InlineData("https://evil-company.com/document.pdf", false)]
        [InlineData("https://company.com.evil.com/document.pdf", false)]
        [InlineData("https://notcompany.com/document.pdf", false)]
        [InlineData("ftp://company.com/document.pdf", false)]
        [InlineData("mailto:ir@company.com", false)]
        public void SiteScope_ShouldEnforceSameSitePolicy(string url, bool expected)
        {
            Assert.Equal(expected, Scope.Contains(url));
        }

        [Fact]
        public async Task Crawler_ShouldNotCrawlOffSitePagesButMayAcceptLinkedExternalDocuments()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor", """
                    <html><body><ul>
                    <li><a href="https://evil-company.com/investor/unclaimed-dividend">Unclaimed dividend</a></li>
                    <li><a href="https://company.com.evil.com/investor/iepf">IEPF</a></li>
                    <li><a href="https://investors.company.com/shareholder-information">Shareholder information</a></li>
                    <li><a href="https://cdn.files-host.net/company/unclaimed-dividend-2021.pdf">Unclaimed dividend 2021</a></li>
                    <li><a href="mailto:ir@company.com">Investor contact</a></li>
                    <li><a href="javascript:void(0)">Shareholder menu</a></li>
                    </ul></body></html>
                    """)
                .Html("https://investors.company.com/shareholder-information", "<html><body>sub</body></html>");

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options());
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor"));

            Assert.DoesNotContain(site.Requests, r => r.Contains("evil", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, site.Count("https://investors.company.com/shareholder-information"));
            Assert.Contains(docs, d => d.Url.StartsWith("https://cdn.files-host.net/"));
        }

        [Fact]
        public async Task Crawler_ShouldNotParsePageThatRedirectsOffSite()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Route("/investor", (req, _) =>
                {
                    var redirected = new HttpRequestMessage(HttpMethod.Get, "https://evil.com/landing");
                    return Task.FromResult(ScriptedSite.Ok(redirected,
                        "<html><body><a href=\"/investor/unclaimed-dividend\">Unclaimed dividend</a><a href=\"/x/unclaimed-dividend.pdf\">Unclaimed dividend</a></body></html>", "text/html"));
                });

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options());
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor"));

            Assert.Empty(docs);
            Assert.Equal(0, site.Count("/investor/unclaimed-dividend"));
            Assert.Equal(1, crawler.LastCrawlAudit!.PagesSkippedOffSite);
        }
    }

    public class CrawlBudgetTests
    {
        [Fact]
        public async Task MaxCrawlDepth_ShouldBeRespected()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor", "<html><body><a href=\"/investor/shareholder-a\">Shareholder A</a></body></html>")
                .Html("/investor/shareholder-a", "<html><body><a href=\"/investor/shareholder-a/shareholder-b\">Shareholder B</a></body></html>")
                .Html("/investor/shareholder-a/shareholder-b", "<html><body><a href=\"/investor/shareholder-a/shareholder-b/shareholder-c\">Shareholder C</a></body></html>")
                .Html("/investor/shareholder-a/shareholder-b/shareholder-c", "<html><body>deep</body></html>");

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options(maxDepth: 2));
            await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor"));

            Assert.Equal(1, site.Count("/investor/shareholder-a"));
            Assert.Equal(0, site.Count("/investor/shareholder-a/shareholder-b"));
        }

        [Fact]
        public async Task Retries_ShouldNotBypassRequestCap()
        {
            var site = new ScriptedSite().Html("/", "<html><body>home</body></html>");
            var links = string.Join("", Enumerable.Range(1, 40).Select(i => $"<a href=\"/investor/shareholder-{i}\">Shareholder {i}</a>"));
            site.Html("/investor", $"<html><body>{links}</body></html>");
            for (var i = 1; i <= 40; i++) site.Status($"/investor/shareholder-{i}", HttpStatusCode.ServiceUnavailable);

            var options = CrawlerFactory.Options(maxPages: 5, retries: 3);
            var crawler = CrawlerFactory.Create(site, options);
            var seeds = CrawlerFactory.Seeds("/investor");
            await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), seeds);

            var cap = options.MaxPagesPerCompany * 2 + seeds.Count;
            Assert.True(site.Requests.Count <= cap + options.MaxRetryCount, $"{site.Requests.Count} requests exceeds cap {cap}");
            Assert.Equal("request cap reached", crawler.LastCrawlAudit!.StopReason);
        }

        [Fact]
        public async Task LinkHeavyPages_ShouldNotGrowQueueWithoutBound()
        {
            var site = new ScriptedSite().Html("/", "<html><body>home</body></html>");
            var links = string.Join("", Enumerable.Range(1, 3000).Select(i => $"<a href=\"/investor/shareholder-notice-{i}\">Shareholder notice {i}</a>"));
            site.Html("/investor", $"<html><body>{links}</body></html>");

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options(maxPages: 3));
            await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor"));

            var audit = crawler.LastCrawlAudit!;
            Assert.True(audit.PagesLeftInQueue <= WebsiteCrawler.MaxChildLinksPerPage, $"{audit.PagesLeftInQueue} pending");
            Assert.True(site.Requests.Count <= 3 * 2 + 2);
        }

        [Fact]
        public async Task HighValueLinkedRoute_ShouldBeProcessedBeforeGuessedRoutes()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body><a href=\"/investor/investor-information/unclaimed-dividend\">Unclaimed Dividend</a></body></html>")
                .Html("/investor/investor-information/unclaimed-dividend", "<html><body>target</body></html>")
                .Html("/financials", "<html><body>fin</body></html>")
                .Html("/annual-reports", "<html><body>ar</body></html>");

            var finder = new InvestorPageFinder(new HttpClient(site), NullLogger<InvestorPageFinder>.Instance);
            var seeds = await finder.FindInvestorPageCandidatesAsync(ScriptedSite.Root);
            var crawlRequestsStart = site.Requests.Count;

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options(maxPages: 100));
            await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), seeds);

            var order = site.Requests.Skip(crawlRequestsStart).Select(r => new Uri(r).AbsolutePath).ToList();
            var target = order.IndexOf("/investor/investor-information/unclaimed-dividend");
            Assert.Equal(1, target);
            Assert.True(order.IndexOf("/financials") > target);
            Assert.True(order.IndexOf("/annual-reports") > target);
            Assert.True(seeds.Where(s => s.IsGuessedPath).All(g => g.Priority < seeds.Single(s => s.Url.EndsWith("unclaimed-dividend")).Priority));
        }
    }

    public class CrawlErrorHandlingTests
    {
        private static async Task<(ScriptedSite Site, WebsiteCrawler Crawler)> CrawlOneAsync(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, int retries = 2, int timeoutSeconds = 5)
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Route("/investor/shareholder-information", responder)
                .Html("/investor/unclaimed-dividend", "<html><body><ul><li><a href=\"/f/unclaimed-dividend-2020.pdf\">Unclaimed dividend 2020</a></li></ul></body></html>");
            var options = CrawlerFactory.Options(retries: retries);
            options.RequestTimeoutSeconds = timeoutSeconds;
            var crawler = CrawlerFactory.Create(site, options);
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor/shareholder-information", "/investor/unclaimed-dividend"));
            Assert.Single(docs);
            return (site, crawler);
        }

        private static CrawlAuditPage Page(WebsiteCrawler crawler)
            => crawler.LastCrawlAudit!.VisitedPages.Single(p => p.Url.EndsWith("/shareholder-information"));

        [Theory]
        [InlineData(HttpStatusCode.NotFound, "not found")]
        [InlineData(HttpStatusCode.Forbidden, "access denied")]
        [InlineData(HttpStatusCode.Unauthorized, "access denied")]
        public async Task PermanentErrors_ShouldNotBeRetried(HttpStatusCode status, string outcome)
        {
            var (site, crawler) = await CrawlOneAsync((req, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = req }));

            Assert.Equal(1, site.Count("/investor/shareholder-information"));
            Assert.Equal(outcome, Page(crawler).Outcome);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task TransientErrors_ShouldBeRetriedBoundedThenContinue(HttpStatusCode status)
        {
            var (site, crawler) = await CrawlOneAsync((req, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = req }), retries: 2);

            Assert.Equal(3, site.Count("/investor/shareholder-information"));
            Assert.Equal(3, Page(crawler).Attempts);
        }

        [Fact]
        public async Task TransientErrorThenSuccess_ShouldProcessPage()
        {
            var calls = 0;
            var (_, crawler) = await CrawlOneAsync((req, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = req }
                : ScriptedSite.Ok(req, "<html><body>ok</body></html>", "text/html")));

            Assert.Equal("processed", Page(crawler).Outcome);
            Assert.Equal(2, Page(crawler).Attempts);
        }

        [Fact]
        public async Task ServerDisconnect_ShouldBeRetriedThenRecordedAsFailure()
        {
            var (site, crawler) = await CrawlOneAsync((_, _) => throw new HttpRequestException("connection reset"), retries: 1);

            Assert.Equal(2, site.Count("/investor/shareholder-information"));
            Assert.Equal("connection reset", Page(crawler).Outcome);
        }

        [Fact]
        public async Task SlowServer_ShouldTimeOutAndContinue()
        {
            var (_, crawler) = await CrawlOneAsync(async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return ScriptedSite.Ok(req, "late", "text/html");
            }, retries: 0, timeoutSeconds: 1);

            Assert.StartsWith("timed out", Page(crawler).Outcome);
        }

        [Fact]
        public async Task MalformedHtmlAndUrls_ShouldBeSkippedSafely()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor/unclaimed-dividend", """
                    <html><body><div><<<p><a href="http://[broken">Unclaimed dividend</a>
                    <a href="javascript:alert(1)">Unclaimed dividend</a><a href="">empty</a><a href="   ">blank</a>
                    <a href="mailto:x@company.com">IEPF mail</a><table><tr><td><a href="/f/unclaimed-dividend-2019.pdf">Unclaimed dividend 2019
                    </body>
                    """);

            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options());
            var docs = await crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor/unclaimed-dividend"));

            Assert.Single(docs);
        }

        [Fact]
        public async Task CancellationDuringHttpRequest_ShouldStopCrawl()
        {
            var site = new ScriptedSite()
                .Route("/", async (req, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return ScriptedSite.Ok(req, "", "text/html");
                });
            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options());
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor"), cts.Token));
        }

        [Fact]
        public async Task CancellationDuringRendering_ShouldPropagate()
        {
            var site = new ScriptedSite()
                .Html("/", "<html><body>home</body></html>")
                .Html("/investor/unclaimed-dividend", "<html><head><title>Unclaimed Dividend</title></head><body><div id=\"vue-Reports\"><a :href=\"p\">x</a></div></body></html>");
            var crawler = CrawlerFactory.Create(site, CrawlerFactory.Options(), new HangingRenderer());
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => crawler.CrawlInvestorPagesAsync(CrawlerFactory.Company(), CrawlerFactory.Seeds("/investor/unclaimed-dividend"), cts.Token));
        }

        private sealed class HangingRenderer : IDynamicPageRenderer
        {
            public async Task<RenderBatchResult> RenderAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new RenderBatchResult(Array.Empty<RenderedPage>(), 1);
            }
        }
    }

    public class DocumentDownloaderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ida-dl-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");

        private static InvestorDocument Doc(string url = "https://company.com/f/unclaimed.pdf") => new()
        {
            DocumentTitle = "Unclaimed",
            DocumentUrl = url,
            DocumentType = "Other",
            FinancialYear = "FY2024",
            FileExtension = ".pdf",
            Company = new Company { CompanyName = "Company Ltd" }
        };

        private DocumentDownloader Downloader(HttpMessageHandler handler, int retries = 2)
            => new(new HttpClient(handler), NullLogger<DocumentDownloader>.Instance,
                   Options.Create(new CrawlOptions { MaxRetryCount = retries, RetryBaseDelayMilliseconds = 0, DownloadTimeoutSeconds = 10 }));

        private static ScriptedSite Serving(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => new ScriptedSite().Route("/f/unclaimed.pdf", responder);

        private static HttpResponseMessage Bytes(HttpRequestMessage req, byte[] bytes, string mediaType = "application/pdf")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = req };
        }

        private string[] FilesOnDisk() => Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : Array.Empty<string>();

        [Fact]
        public async Task ValidPdf_ShouldBeStoredWithCorrectHashAndNoPartialFile()
        {
            var result = await Downloader(Serving((req, _) => Task.FromResult(Bytes(req, PdfBytes)))).DownloadAsync(Doc(), _dir);

            Assert.True(result.Success);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant(), result.SHA256);
            Assert.Equal(PdfBytes.Length, result.FileSize);
            Assert.Equal(new[] { result.LocalPath }, FilesOnDisk());
            Assert.Equal("Company_Ltd_Other_FY2024.pdf", result.LocalFileName);
        }

        [Fact]
        public async Task ExistingFile_ShouldNotBeOverwritten()
        {
            var downloader = Downloader(Serving((req, _) => Task.FromResult(Bytes(req, PdfBytes))));
            var first = await downloader.DownloadAsync(Doc(), _dir);
            var second = await downloader.DownloadAsync(Doc(), _dir);

            Assert.NotEqual(first.LocalPath, second.LocalPath);
            Assert.Equal("Company_Ltd_Other_FY2024_1.pdf", second.LocalFileName);
            Assert.Equal(2, FilesOnDisk().Length);
        }

        [Theory]
        [InlineData("text/html", "<html><body>Error 500</body></html>")]
        [InlineData("application/pdf", "<!DOCTYPE html><html>login required</html>")]
        [InlineData("application/octet-stream", "")]
        public async Task InvalidContent_ShouldFailAndLeaveNoFile(string mediaType, string body)
        {
            var result = await Downloader(Serving((req, _) => Task.FromResult(Bytes(req, Encoding.UTF8.GetBytes(body), mediaType)))).DownloadAsync(Doc(), _dir);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(FilesOnDisk());
        }

        [Fact]
        public async Task InterruptedStream_ShouldFailAndDeletePartialFile()
        {
            var result = await Downloader(Serving((req, _) =>
            {
                var content = new StreamContent(new FailingStream(PdfBytes));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = req });
            })).DownloadAsync(Doc(), _dir);

            Assert.False(result.Success);
            Assert.Empty(FilesOnDisk());
        }

        [Fact]
        public async Task NotFound_ShouldFailWithoutRetry()
        {
            var site = new ScriptedSite();
            var result = await Downloader(site).DownloadAsync(Doc(), _dir);

            Assert.False(result.Success);
            Assert.Single(site.Requests);
        }

        [Fact]
        public async Task ServiceUnavailable_ShouldRetryBoundedThenFail()
        {
            var site = Serving((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = req }));
            var result = await Downloader(site, retries: 2).DownloadAsync(Doc(), _dir);

            Assert.False(result.Success);
            Assert.Equal(3, site.Requests.Count);
        }

        [Fact]
        public async Task CancellationDuringDownload_ShouldPropagateAndLeaveNoFile()
        {
            using var cts = new CancellationTokenSource();
            var site = Serving((req, _) =>
            {
                var content = new StreamContent(new BlockingStream(PdfBytes, cts));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = req });
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(site).DownloadAsync(Doc(), _dir, cts.Token));
            Assert.Empty(FilesOnDisk());
        }

        [Theory]
        [InlineData("..", "Document")]
        [InlineData("../../etc/passwd", "etc_passwd")]
        [InlineData("Annual Report: 2024?", "Annual_Report__2024")]
        public void SanitizePath_ShouldNeverEscapeDirectory(string input, string expected)
        {
            Assert.Equal(expected, DocumentDownloader.SanitizePath(input));
        }

        [Theory]
        [InlineData("..", "Company")]
        [InlineData("Dabur India Limited", "Dabur India Limited")]
        [InlineData("A/B Ltd.", "A_B Ltd")]
        public void CompanyFolderName_ShouldKeepLayoutAndBlockTraversal(string input, string expected)
        {
            Assert.Equal(expected, CompanyProcessor.SanitizeFolderName(input));
        }

        private sealed class FailingStream : MemoryStream
        {
            public FailingStream(byte[] head) : base(head) { }
            public override int Read(byte[] buffer, int offset, int count) => Position < Length ? base.Read(buffer, offset, count) : throw new IOException("connection reset");
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("connection reset");
        }

        private sealed class BlockingStream : MemoryStream
        {
            private readonly CancellationTokenSource _cancelOnDrain;
            public BlockingStream(byte[] head, CancellationTokenSource cancelOnDrain) : base(head) => _cancelOnDrain = cancelOnDrain;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
                _cancelOnDrain.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }

    public class CompanyProcessingHardeningTests
    {
        private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"hardening-{Guid.NewGuid():N}").Options);

        private static CompanyProcessor Processor(ApplicationDbContext db, IWebsiteCrawler crawler, IDocumentDownloader downloader)
        {
            var finder = new Mock<IInvestorPageFinder>();
            finder.Setup(f => f.FindInvestorPageCandidatesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<InvestorPageCandidate> { CrawlPriority.ForSeed("https://company.com/", InvestorPageSource.Homepage) });
            return new CompanyProcessor(db, Mock.Of<ICompanyWebsiteFinder>(), finder.Object, crawler, downloader,
                new DuplicateDetector(db), Mock.Of<ICrawlLogger>(),
                Options.Create(new CrawlOptions { StorageDirectory = Path.GetTempPath() }), NullLogger<CompanyProcessor>.Instance);
        }

        private static Mock<IWebsiteCrawler> CrawlerReturning(params string[] urls)
        {
            var crawler = new Mock<IWebsiteCrawler>();
            crawler.Setup(c => c.CrawlInvestorPagesAsync(It.IsAny<Company>(), It.IsAny<IReadOnlyList<InvestorPageCandidate>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(urls.Select(u => new DiscoveredDocument("Unclaimed", u, "https://company.com/", ".pdf", "Other", null, null, null)).ToList());
            return crawler;
        }

        [Theory]
        [InlineData(0, 0, 0, 0, CompanyStatus.NeedsReview)]
        [InlineData(5, 5, 0, 0, CompanyStatus.Completed)]
        [InlineData(5, 3, 0, 2, CompanyStatus.Partial)]
        [InlineData(5, 0, 5, 0, CompanyStatus.Completed)]
        [InlineData(5, 0, 2, 3, CompanyStatus.Failed)]
        public void DetermineStatus_ShouldReflectOutcome(int found, int downloaded, int duplicates, int failed, CompanyStatus expected)
        {
            Assert.Equal(expected, CompanyProcessor.DetermineStatus(found, downloaded, duplicates, failed).Status);
        }

        [Fact]
        public async Task AlreadyDownloadedUrlVariant_ShouldNotBeDownloadedAgain()
        {
            using var db = Db();
            db.Companies.Add(new Company { Id = 1, CompanyName = "Company Ltd", Website = "https://company.com" });
            db.InvestorDocuments.Add(new InvestorDocument
            {
                CompanyId = 1, DocumentTitle = "Unclaimed", DocumentUrl = "https://company.com/Unclaimed Dividend/list.pdf",
                Downloaded = true, Status = "Downloaded", SHA256 = "abc"
            });
            await db.SaveChangesAsync();

            var downloader = new Mock<IDocumentDownloader>();
            await Processor(db, CrawlerReturning("https://www.company.com/Unclaimed%20Dividend/list.pdf#p1").Object, downloader.Object).ProcessCompanyAsync(1);

            downloader.Verify(d => d.DownloadAsync(It.IsAny<InvestorDocument>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Equal(CompanyStatus.Completed, (await db.Companies.SingleAsync()).Status);
        }

        [Fact]
        public async Task CancellationDuringDownload_ShouldReturnCompanyToPendingAndMarkDocumentFailed()
        {
            using var db = Db();
            db.Companies.Add(new Company { Id = 1, CompanyName = "Company Ltd", Website = "https://company.com" });
            await db.SaveChangesAsync();

            using var cts = new CancellationTokenSource();
            var downloader = new Mock<IDocumentDownloader>();
            downloader.Setup(d => d.DownloadAsync(It.IsAny<InvestorDocument>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .Returns<InvestorDocument, string, CancellationToken>((_, _, ct) =>
                      {
                          cts.Cancel();
                          ct.ThrowIfCancellationRequested();
                          return Task.FromResult(new DownloadResult(true, null, null, 0, null, null));
                      });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Processor(db, CrawlerReturning("https://company.com/f/unclaimed.pdf").Object, downloader.Object).ProcessCompanyAsync(1, cts.Token));

            var company = await db.Companies.Include(c => c.Documents).SingleAsync();
            Assert.Equal(CompanyStatus.Pending, company.Status);
            Assert.All(company.Documents, d => Assert.Equal("Failed", d.Status));
            Assert.All(company.Documents, d => Assert.False(d.Downloaded));
        }

        [Fact]
        public async Task Worker_ShouldIsolateCompanyFailuresAndNotLoopForever()
        {
            var dbName = $"worker-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
            var processed = new ConcurrentBag<int>();
            var processor = new Mock<ICompanyProcessor>();
            processor.Setup(p => p.ProcessCompanyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                     .Returns<int, CancellationToken>((id, _) =>
                     {
                         processed.Add(id);
                         if (id == 1) throw new InvalidOperationException("boom");
                         return Task.CompletedTask;
                     });
            services.AddScoped(_ => processor.Object);
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Companies.AddRange(new Company { Id = 1, CompanyName = "A" }, new Company { Id = 2, CompanyName = "B" });
                await db.SaveChangesAsync();
            }

            await using var queue = new JobQueueService(provider, Options.Create(new CrawlOptions { MaxConcurrentCompanies = 1 }), NullLogger<JobQueueService>.Instance);
            await queue.StartProcessingAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (queue.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.False(queue.IsRunning);
            Assert.Equal(new[] { 1, 2 }, processed.OrderBy(i => i));
        }
    }
}
