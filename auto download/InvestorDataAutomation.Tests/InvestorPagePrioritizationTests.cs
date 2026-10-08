using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using InvestorDataAutomation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace InvestorDataAutomation.Tests
{
    public class CrawlPriorityTests
    {
        private const string Site = "https://example.com";

        private static InvestorPageCandidate Linked(string path, string anchor)
            => CrawlPriority.ForSeed(Site + path, InvestorPageSource.HomepageLink, anchor);

        private static InvestorPageCandidate Guessed(string path)
            => CrawlPriority.ForSeed(Site + path, InvestorPageSource.StandardPath);

        [Fact]
        public void LinkedInvestorPage_ShouldOutrankGuessedInvestorsPath()
        {
            var linked = Linked("/investor", "Investors");
            var guessed = Guessed("/investors");

            Assert.True(linked.Priority > guessed.Priority, $"{linked.Priority} ({linked.Reason}) vs {guessed.Priority} ({guessed.Reason})");
        }

        [Fact]
        public void ValidNestedInvestorPage_ShouldOutrankGenericGuesses()
        {
            var investor = Linked("/investor", "Investors");
            var nested = CrawlPriority.ForChild(investor, Site + "/investor/shareholder-information", "Shareholder Information", "", 2);

            foreach (var guess in new[] { "/financial-results", "/annual-reports", "/shareholder-information", "/investors", "/stock-price" })
            {
                var guessed = Guessed(guess);
                Assert.True(nested.Priority > guessed.Priority, $"{nested.Url} {nested.Priority} should outrank guessed {guess} {guessed.Priority}");
            }
        }

        [Fact]
        public void ParentInvestorContext_ShouldKeepNestedShareholderPageEligibleAndBoosted()
        {
            var investor = Linked("/investor", "Investors");
            var unrelatedParent = Linked("/about-us", "About Us");
            var url = Site + "/investor/shareholder-information/unclaimed-shares";

            var underInvestor = CrawlPriority.ForChild(investor, url, "Unclaimed Shares", "", 2);
            var underUnrelated = CrawlPriority.ForChild(unrelatedParent, url, "Unclaimed Shares", "", 2);

            Assert.True(InvestorRouteRelevance.ShouldFollowCrawlLink(investor.Url, url, "unclaimed shares", ""));
            Assert.Contains("child of investor page", underInvestor.Reason);
            Assert.True(underInvestor.Priority > underUnrelated.Priority);
        }

        [Fact]
        public void HighValueChild_ShouldOutrankUnrelatedChildOfSameParent()
        {
            var investor = Linked("/investor", "Investors");
            var unclaimed = CrawlPriority.ForChild(investor, Site + "/investor/unclaimed-dividend", "Unclaimed Dividend", "", 2);
            var aboutUs = CrawlPriority.ForChild(investor, Site + "/investor/about-us", "About Us", "", 2);

            Assert.True(unclaimed.Priority > aboutUs.Priority);
        }

        [Fact]
        public void DaburMultiSegmentUnclaimedUrl_ShouldScoreHigh()
        {
            const string dabur = "https://www.dabur.com/investor/investor-information/unclaimed-dividend";
            var route = InvestorRouteRelevance.ScoreRouteSignals(dabur);
            Assert.True(route.Score >= 150, $"route score {route.Score}: {string.Join(", ", route.Matches)}");

            var parent = CrawlPriority.ForSeed("https://www.dabur.com/investor", InvestorPageSource.HomepageLink, "Investors");
            var info = CrawlPriority.ForChild(parent, "https://www.dabur.com/investor/investor-information", "Investor Information", "", 2);
            var target = CrawlPriority.ForChild(info, dabur, "Unclaimed Dividend and Shares", "", 3);

            Assert.True(target.Priority > info.Priority);
            Assert.True(target.Priority > parent.Priority);
            Assert.Contains("IEPF/unclaimed route", target.Reason);
        }

        [Theory]
        [InlineData("https://example.com/shareholder-services/unclaimed-shares")]
        [InlineData("https://example.com/Shareholder_Services/Unclaimed%20Shares")]
        [InlineData("https://example.com/shareholder services/unclaimed shares")]
        public void ShareholderServicesUnclaimedShares_ShouldScoreHighAcrossNormalizations(string url)
        {
            var route = InvestorRouteRelevance.ScoreRouteSignals(url);
            Assert.True(route.Score >= 150, $"route score {route.Score}: {string.Join(", ", route.Matches)}");
            Assert.True(InvestorRouteRelevance.HasTargetDocumentRouteSignal(url));
        }

        [Fact]
        public void GenericReportsPage_ShouldNotDominateStrongInvestorRoutes()
        {
            var reports = Linked("/reports", "Reports");
            var investor = Linked("/investor", "Investors");
            var shareholder = Linked("/shareholder-information", "Shareholder Information");

            Assert.True(investor.Priority > reports.Priority);
            Assert.True(shareholder.Priority > reports.Priority);
        }

        [Fact]
        public void HomepageRoot_ShouldBeCrawledFirst()
        {
            var root = CrawlPriority.ForSeed(Site + "/", InvestorPageSource.Homepage);
            var strongest = CrawlPriority.ForSeed(Site + "/investor/unclaimed-dividend-iepf", InvestorPageSource.HomepageLink, "Unclaimed Dividend IEPF");

            Assert.True(root.Priority > strongest.Priority);
        }
    }

    public class InvestorPageSelectionTests
    {
        [Fact]
        public void SelectPrimaryInvestorPage_ShouldPreferLinkedInvestorLandingPage()
        {
            var candidates = new List<InvestorPageCandidate>
            {
                CrawlPriority.ForSeed("https://example.com/", InvestorPageSource.Homepage),
                CrawlPriority.ForSeed("https://example.com/investors", InvestorPageSource.StandardPath),
                CrawlPriority.ForSeed("https://example.com/investor/investor-information/unclaimed-dividend", InvestorPageSource.HomepageLink, "Unclaimed Dividend"),
                CrawlPriority.ForSeed("https://example.com/investor", InvestorPageSource.HomepageLink, "Investors"),
                CrawlPriority.ForSeed("https://example.com/careers", InvestorPageSource.HomepageLink, "Careers"),
            };

            Assert.Equal("https://example.com/investor", InvestorPageFinder.SelectPrimaryInvestorPage(candidates));
        }

        [Fact]
        public void SelectPrimaryInvestorPage_ShouldNeverChooseUnvalidatedGuess()
        {
            var candidates = new List<InvestorPageCandidate>
            {
                CrawlPriority.ForSeed("https://example.com/investors", InvestorPageSource.StandardPath),
                CrawlPriority.ForSeed("https://example.com/", InvestorPageSource.Homepage),
            };

            Assert.Equal("https://example.com/", InvestorPageFinder.SelectPrimaryInvestorPage(candidates));
        }

        [Fact]
        public async Task CompanyProcessor_ShouldStoreBestInvestorPageAndPassFullCandidateListToCrawler()
        {
            var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"InvestorPageSelection-{Guid.NewGuid():N}")
                .Options;
            using var db = new ApplicationDbContext(dbOptions);
            db.Companies.Add(new Company { Id = 1, CompanyName = "Example Ltd", Website = "https://example.com" });
            await db.SaveChangesAsync();

            var candidates = new List<InvestorPageCandidate>
            {
                CrawlPriority.ForSeed("https://example.com/", InvestorPageSource.Homepage),
                CrawlPriority.ForSeed("https://example.com/investor", InvestorPageSource.HomepageLink, "Investors"),
                CrawlPriority.ForSeed("https://example.com/investors", InvestorPageSource.StandardPath),
                CrawlPriority.ForSeed("https://example.com/shareholder-services", InvestorPageSource.StandardPath),
            };

            var finder = new Mock<IInvestorPageFinder>();
            finder.Setup(f => f.FindInvestorPageCandidatesAsync("https://example.com", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(candidates);

            IReadOnlyList<InvestorPageCandidate>? crawled = null;
            var crawler = new Mock<IWebsiteCrawler>();
            crawler.Setup(c => c.CrawlInvestorPagesAsync(It.IsAny<Company>(), It.IsAny<IReadOnlyList<InvestorPageCandidate>>(), It.IsAny<CancellationToken>()))
                   .Callback<Company, IReadOnlyList<InvestorPageCandidate>, CancellationToken>((_, list, _) => crawled = list)
                   .ReturnsAsync(new List<DiscoveredDocument>());

            var processor = new CompanyProcessor(
                db,
                Mock.Of<ICompanyWebsiteFinder>(),
                finder.Object,
                crawler.Object,
                Mock.Of<IDocumentDownloader>(),
                Mock.Of<IDuplicateDetector>(),
                Mock.Of<ICrawlLogger>(),
                Options.Create(new CrawlOptions { StorageDirectory = System.IO.Path.GetTempPath() }),
                NullLogger<CompanyProcessor>.Instance);

            await processor.ProcessCompanyAsync(1);

            var company = await db.Companies.SingleAsync(c => c.Id == 1);
            Assert.Equal("https://example.com/investor", company.InvestorPage);
            Assert.NotNull(crawled);
            Assert.Equal(candidates.Select(c => c.Url), crawled!.Select(c => c.Url));
        }
    }

    public class PriorityCrawlerTests
    {
        private const string Site = "https://example.com";

        private sealed class FakeSite : HttpMessageHandler
        {
            private readonly Dictionary<string, string> _pages = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentQueue<string> Requests { get; } = new();
            public ConcurrentQueue<string> SuccessfulRequests { get; } = new();

            public FakeSite Page(string path, string html)
            {
                _pages[UrlNormalizer.PageKey(Site + path)] = html;
                return this;
            }

            public int RequestCount(string path)
                => Requests.Count(r => string.Equals(UrlNormalizer.PageKey(r), UrlNormalizer.PageKey(Site + path), StringComparison.OrdinalIgnoreCase));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.ToString();
                Requests.Enqueue(url);
                if (_pages.TryGetValue(UrlNormalizer.PageKey(url), out var html))
                {
                    SuccessfulRequests.Enqueue(url);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(html, Encoding.UTF8, "text/html"),
                        RequestMessage = request
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }
        }

        private sealed class ListLogger<T> : ILogger<T>
        {
            public ConcurrentQueue<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Enqueue(formatter(state, exception));
        }

        private static CrawlOptions Options(int maxPages, int maxDepth = 4) => new()
        {
            MaxPagesPerCompany = maxPages,
            MaxCrawlDepth = maxDepth,
            EnablePlaywrightFallback = false
        };

        private static WebsiteCrawler CreateCrawler(FakeSite site, CrawlOptions options, ILogger<WebsiteCrawler>? logger = null)
        {
            var opts = Microsoft.Extensions.Options.Options.Create(options);
            return new WebsiteCrawler(
                new HttpClient(site),
                new DocumentDetector(),
                new DocumentClassifier(),
                new FinancialPeriodDetector(),
                new TargetDocumentRelevanceFilter(opts, NullLogger<TargetDocumentRelevanceFilter>.Instance),
                opts,
                logger ?? NullLogger<WebsiteCrawler>.Instance);
        }

        private static async Task<List<InvestorPageCandidate>> FindSeedsAsync(FakeSite site)
        {
            var finder = new InvestorPageFinder(new HttpClient(site), NullLogger<InvestorPageFinder>.Instance);
            return await finder.FindInvestorPageCandidatesAsync(Site);
        }

        private const string DaburStyleUnclaimedPage = """
            <html><head><title>Unclaimed Dividend and Shares Information</title></head>
            <body><h1>Unclaimed Dividend</h1>
            <div id="vue-Reports" data-reporttype="unclaimed-dividend">
              <li v-for="(pdfres, ip) in event.details.pdf"><a :href="pdfres.element.fullpath">PDF</a></li>
            </div>
            <a href="/files/IEPF-unclaimed-dividend-2018-19.pdf">Unclaimed Dividend FY 2018-19 transferred to IEPF</a>
            </body></html>
            """;

        private static FakeSite HierarchicalSite() => new FakeSite()
            .Page("/", """<html><body><a href="/investor">Investors</a><a href="/careers">Careers</a></body></html>""")
            .Page("/investor", """
                <html><head><title>Investor</title></head><body>
                <a href="/investor/about-us">About Us</a>
                <a href="/investor/investor-information">Investor Information</a>
                </body></html>
                """)
            .Page("/investor/about-us", "<html><body>about</body></html>")
            .Page("/investor/investor-information", """
                <html><head><title>Investor Information</title></head><body>
                <a href="/investor/investor-information/unclaimed-dividend">Unclaimed Dividend and Shares</a>
                </body></html>
                """)
            .Page("/investor/investor-information/unclaimed-dividend", DaburStyleUnclaimedPage);

        [Fact]
        public async Task GuessedPaths_ThatReturn404_ShouldNotCrowdOutDeepUnclaimedPage()
        {
            var site = HierarchicalSite();
            var seeds = await FindSeedsAsync(site);
            Assert.True(seeds.Count(s => s.IsGuessedPath) > 30);

            var logger = new ListLogger<WebsiteCrawler>();
            var crawler = CreateCrawler(site, Options(maxPages: 4), logger);
            var docs = await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" }, seeds);

            Assert.Equal(1, site.RequestCount("/investor/investor-information/unclaimed-dividend"));
            Assert.Equal(0, site.RequestCount("/investor/about-us"));
            Assert.Contains(docs, d => d.Url.Contains("IEPF-unclaimed-dividend-2018-19.pdf"));
            Assert.Contains(logger.Messages, m => m.Contains("[DYNAMIC ASSESSMENT]") && m.Contains("unclaimed-dividend") && m.Contains("Dynamic: TRUE"));
        }

        [Fact]
        public async Task NotFoundPages_ShouldNotConsumePageBudgetOrBeExpanded()
        {
            var site = HierarchicalSite();
            var seeds = await FindSeedsAsync(site);

            var crawler = CreateCrawler(site, Options(maxPages: 100));
            await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" }, seeds);

            foreach (var guess in seeds.Where(s => s.IsGuessedPath))
            {
                Assert.True(site.RequestCount(new Uri(guess.Url).AbsolutePath) <= 1);
            }
            Assert.Equal(1, site.RequestCount("/investor/investor-information/unclaimed-dividend"));
        }

        [Fact]
        public async Task MaxPagesPerCompany_ShouldStillBoundTheCrawl()
        {
            var site = new FakeSite();
            var hubLinks = string.Join("", Enumerable.Range(1, 30).Select(i => $"<a href=\"/investor/shareholder-notice-{i}\">Shareholder Notice {i}</a>"));
            site.Page("/", "<html><body><a href=\"/investor\">Investors</a></body></html>");
            site.Page("/investor", $"<html><body>{hubLinks}</body></html>");
            for (var i = 1; i <= 30; i++)
            {
                var children = string.Join("", Enumerable.Range(1, 5).Select(j => $"<a href=\"/investor/shareholder-notice-{i}/dividend-{j}\">Dividend {j}</a>"));
                site.Page($"/investor/shareholder-notice-{i}", $"<html><body>{children}</body></html>");
                for (var j = 1; j <= 5; j++)
                    site.Page($"/investor/shareholder-notice-{i}/dividend-{j}", "<html><body>dividend</body></html>");
            }

            var crawler = CreateCrawler(site, Options(maxPages: 5));
            var seeds = new List<InvestorPageCandidate>
            {
                CrawlPriority.ForSeed(Site + "/", InvestorPageSource.Homepage),
                CrawlPriority.ForSeed(Site + "/investor", InvestorPageSource.HomepageLink, "Investors")
            };
            await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" }, seeds);

            Assert.Equal(5, site.SuccessfulRequests.Count);
            Assert.Equal(5, site.Requests.Count);
        }

        [Fact]
        public async Task RequestCap_ShouldBoundCrawlWhenMostLinksAreBroken()
        {
            var site = new FakeSite();
            var brokenLinks = string.Join("", Enumerable.Range(1, 200).Select(i => $"<a href=\"/investor/shareholder-notice-{i}\">Shareholder Notice {i}</a>"));
            site.Page("/", "<html><body><a href=\"/investor\">Investors</a></body></html>");
            site.Page("/investor", $"<html><body>{brokenLinks}</body></html>");

            var crawler = CreateCrawler(site, Options(maxPages: 5));
            var seeds = new List<InvestorPageCandidate>
            {
                CrawlPriority.ForSeed(Site + "/", InvestorPageSource.Homepage),
                CrawlPriority.ForSeed(Site + "/investor", InvestorPageSource.HomepageLink, "Investors")
            };
            await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" }, seeds);

            Assert.Equal(5 * 2 + seeds.Count, site.Requests.Count);
        }

        [Fact]
        public async Task DuplicateUrlVariants_ShouldBeRequestedOnce()
        {
            var site = new FakeSite()
                .Page("/", """
                    <html><body>
                    <a href="/investor">Investors</a><a href="/investor/">Investors</a>
                    <a href="/INVESTOR">Investors</a><a href="/investor#top">Investors</a>
                    <a href="https://example.com/investor">Investors</a>
                    </body></html>
                    """)
                .Page("/investor", """<html><body><a href="/">Home</a><a href="/investor/">Self</a><a href="/investor/shareholder-information">Shareholder Information</a></body></html>""")
                .Page("/investor/shareholder-information", """<html><body><a href="/investor">Investors</a></body></html>""");

            var seeds = await FindSeedsAsync(site);
            var crawler = CreateCrawler(site, Options(maxPages: 50));
            await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" }, seeds);

            Assert.Equal(1, site.RequestCount("/investor"));
            Assert.Equal(1, site.RequestCount("/investor/shareholder-information"));
            var duplicatedRequests = site.Requests
                .GroupBy(UrlNormalizer.PageKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key != UrlNormalizer.PageKey(Site + "/"))
                .Where(g => g.Count() > 1);
            Assert.Empty(duplicatedRequests);
        }

        [Fact]
        public async Task LegacyStringOverload_ShouldStillCrawlProvidedPagesInOrder()
        {
            var site = HierarchicalSite();
            var crawler = CreateCrawler(site, Options(maxPages: 10));

            var docs = await crawler.CrawlInvestorPagesAsync(new Company { CompanyName = "Example" },
                new List<string> { Site + "/investor/investor-information/unclaimed-dividend" });

            Assert.Equal(Site + "/investor/investor-information/unclaimed-dividend", site.Requests.First());
            Assert.Contains(docs, d => d.Url.Contains("IEPF-unclaimed-dividend-2018-19.pdf"));
        }
    }
}
