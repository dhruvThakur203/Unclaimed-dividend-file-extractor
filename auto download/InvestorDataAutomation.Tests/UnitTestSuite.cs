using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using InvestorDataAutomation.Core.DTOs;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Data;
using InvestorDataAutomation.Services;
using Microsoft.EntityFrameworkCore;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InvestorDataAutomation.Tests
{
    public class TargetDocumentRelevanceFilterTests
    {
        private readonly TargetDocumentRelevanceFilter _filter = new(
            Options.Create(new CrawlOptions
            {
                TargetDocumentScoreThreshold = 70,
                TargetDocumentPossibleCandidateMinScore = 40
            }),
            NullLogger<TargetDocumentRelevanceFilter>.Instance);

        private TargetDocumentFilterResult Evaluate(string fileName, string anchor = "", string url = "")
        {
            var docUrl = string.IsNullOrWhiteSpace(url) ? $"https://example.com/files/{fileName}" : url;
            return _filter.Evaluate(new TargetDocumentCandidate(docUrl, fileName, anchor, null, null, null));
        }

        [Theory]
        [InlineData("Unclaimed_Dividend_2024_25.pdf")]
        [InlineData("Unpaid_Dividend_Statement.pdf")]
        [InlineData("IEPF_Transfer_List_2025.xlsx")]
        [InlineData("Shares_Liable_to_IEPF.pdf")]
        [InlineData("Shares-Transferred-to-IEPF.pdf")]
        [InlineData("Unclaimed_Shares.pdf")]
        [InlineData("Folio-wise-Unclaimed-Dividend.xlsx")]
        [InlineData("Shareholder-wise-Unclaimed-Amount.pdf")]
        [InlineData("Details-of-Unclaimed-Dividend.pdf")]
        [InlineData("IEPF-5.pdf")]
        public void ShouldDownload_TargetIepfAndUnclaimedFiles(string fileName)
        {
            var result = Evaluate(fileName);
            Assert.Equal(TargetDocumentFilterAction.Download, result.Action);
            Assert.True(result.Score >= 70);
        }

        [Theory]
        [InlineData("Annual_Report_2024_25.pdf")]
        [InlineData("Investor_Presentation.pdf")]
        [InlineData("Financial_Results_Q4.pdf")]
        [InlineData("Dividend_Policy.pdf")]
        [InlineData("Corporate_Governance.pdf")]
        [InlineData("Company_Profile.pdf")]
        public void ShouldSkip_UnrelatedInvestorDocuments(string fileName)
        {
            var result = Evaluate(fileName);
            Assert.NotEqual(TargetDocumentFilterAction.Download, result.Action);
        }

        [Fact]
        public void ShouldDownload_WhenAnchorProvidesContextDespiteOpaqueFilename()
        {
            var result = _filter.Evaluate(new TargetDocumentCandidate(
                "https://example.com/download.aspx?id=1234",
                "download.aspx",
                "List of Unclaimed Dividend for FY 2024-25",
                null,
                "Investor Downloads",
                null));

            Assert.Equal(TargetDocumentFilterAction.Download, result.Action);
            Assert.True(result.Score >= 70);
        }

        [Theory]
        [InlineData("unclaimed-dividend")]
        [InlineData("unclaimed_dividend")]
        [InlineData("unclaimed dividend")]
        [InlineData("UnclaimedDividend")]
        public void SeparatorVariations_ShouldMatchUnclaimedDividend(string phrase)
        {
            var fileName = phrase.Replace(" ", "_").Replace("-", "_") + "_2024_25.pdf";
            var result = Evaluate(fileName, phrase);
            Assert.Equal(TargetDocumentFilterAction.Download, result.Action);
        }

        [Fact]
        public void SevenYearsAlone_ShouldNotDownload()
        {
            var result = Evaluate("Notice_7_years_transfer.pdf", "7 years compliance notice");
            Assert.NotEqual(TargetDocumentFilterAction.Download, result.Action);
        }

        [Fact]
        public void DividendStatementWithoutUnclaimedSignal_ShouldNotDownload()
        {
            var result = Evaluate("Dividend_Statement_2024.pdf", "Dividend Statement 2024");
            Assert.NotEqual(TargetDocumentFilterAction.Download, result.Action);
        }
    }

    public class HierarchicalInvestorRouteTests
    {
        private const string DaburUnclaimed =
            "https://www.dabur.com/investor/investor-information/unclaimed-dividend";

        [Theory]
        [InlineData("https://www.example.com/investor/investor-information/unclaimed-dividend")]
        [InlineData("https://www.example.com/investor/investor-information")]
        [InlineData("https://www.example.com/investor/shareholder-services")]
        [InlineData("https://www.example.com/investors/shareholder-information")]
        [InlineData("https://www.example.com/shareholders/unclaimed-shares")]
        public void HierarchicalRoutes_ShouldBeFollowedFromInvestorHub(string targetUrl)
        {
            var parent = "https://www.example.com/investor/investor-information";
            Assert.True(InvestorRouteRelevance.ShouldFollowCrawlLink(parent, targetUrl, string.Empty, string.Empty));
        }

        [Theory]
        [InlineData("https://www.example.com/careers")]
        [InlineData("https://www.example.com/products")]
        [InlineData("https://www.example.com/about-us")]
        public void UnrelatedRoutes_ShouldNotInheritInvestorContext(string targetUrl)
        {
            var parent = "https://www.example.com/investor/investor-information";
            Assert.False(InvestorRouteRelevance.ShouldFollowCrawlLink(parent, targetUrl, string.Empty, string.Empty));
        }

        [Fact]
        public void DeepInvestorPath_ShouldScoreHigherThanSingleSegmentUnclaimedRoute()
        {
            var deepScore = InvestorRouteRelevance.ScoreCrawlRoute(DaburUnclaimed, string.Empty, string.Empty, null);
            var shallowScore = InvestorRouteRelevance.ScoreCrawlRoute(
                "https://www.dabur.com/unclaimed-dividend", string.Empty, string.Empty, null);

            Assert.True(deepScore > shallowScore);
            Assert.True(deepScore >= InvestorRouteRelevance.CrawlFollowMinScore);
        }

        [Fact]
        public void InvestorInformationHub_ShouldBeRecognizedWithoutUnclaimedKeyword()
        {
            var hubUrl = "https://www.example.com/investor/investor-information";
            Assert.True(InvestorRouteRelevance.IsInvestorHub(hubUrl));
            Assert.True(InvestorRouteRelevance.ShouldFollowCrawlLink(
                "https://www.example.com/investor",
                hubUrl,
                "Investor Information",
                string.Empty));
        }

        [Fact]
        public void DaburRoute_ReportsExpectedHierarchyScore()
        {
            var score = InvestorRouteRelevance.ScoreCrawlRoute(DaburUnclaimed, string.Empty, string.Empty, null);
            Assert.True(score >= 120, $"Expected Dabur hierarchical score >= 120, actual {score}");
        }
    }

    public class InvestorDiscoveryExpansionTests
    {
        private static bool ShouldFollowInvestorLink(string url, string text, string parentPage = "https://example.com/investor")
        {
            return InvestorRouteRelevance.ShouldFollowCrawlLink(parentPage, url, text.ToLowerInvariant(), string.Empty);
        }

        private static async Task<List<string>> FindPagesWithNoHomepageLinksAsync(string baseUrl)
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body></body></html>")
            });
            var finder = new InvestorPageFinder(new HttpClient(handler), NullLogger<InvestorPageFinder>.Instance);
            return await finder.FindInvestorPagesAsync(baseUrl);
        }

        [Theory]
        [InlineData("/investor-relations")]
        [InlineData("/investorrelations")]
        [InlineData("/ir")]
        [InlineData("/shareholder-information")]
        [InlineData("/shareholder-info")]
        [InlineData("/stock-information")]
        [InlineData("/stock-price")]
        [InlineData("/share-price")]
        [InlineData("/shareholding-pattern")]
        [InlineData("/dividend-history")]
        [InlineData("/unclaimed-dividends")]
        [InlineData("/registrars")]
        [InlineData("/rtas")]
        [InlineData("/annual-general-meeting")]
        [InlineData("/investor-faqs")]
        [InlineData("/contact-ir")]
        [InlineData("/investor-contacts")]
        public async Task StandardPaths_ShouldIncludeExpandedInvestorRoutes(string pathSegment)
        {
            var pages = await FindPagesWithNoHomepageLinksAsync("https://example.com");
            Assert.Contains(pages, u => u.EndsWith(pathSegment, StringComparison.OrdinalIgnoreCase)
                || u.Contains(pathSegment, StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("https://example.com/investor-relations", "Investor Relations", true)]
        [InlineData("https://example.com/shareholder-information", "Shareholder Information", true)]
        [InlineData("https://example.com/dividend-history", "Dividend History", true)]
        [InlineData("https://example.com/investor-contacts", "Investor Contacts", true)]
        public void ShouldFollowCrawlLink_ShouldRecognizeExpandedRoutes(string url, string text, bool expected)
        {
            Assert.Equal(expected, ShouldFollowInvestorLink(url, text));
        }

        [Theory]
        [InlineData("https://example.com/careers", "Careers")]
        [InlineData("https://example.com/products", "Products")]
        [InlineData("https://example.com/contact", "Contact Us")]
        [InlineData("https://example.com/about-us", "About Us")]
        [InlineData("https://example.com/news", "News")]
        public void ShouldFollowCrawlLink_ShouldRejectUnrelatedRoutes(string url, string text)
        {
            Assert.False(ShouldFollowInvestorLink(url, text));
        }

        [Fact]
        public async Task HomepageScoring_ShouldNotPromoteGenericUnrelatedLinks()
        {
            var html = """
                <html><body>
                <a href="/careers">Careers</a>
                <a href="/products">Products</a>
                <a href="/contact">Contact</a>
                <a href="/about-us">About Us</a>
                <a href="/news">Company News</a>
                </body></html>
                """;
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            });
            var finder = new InvestorPageFinder(new HttpClient(handler), NullLogger<InvestorPageFinder>.Instance);
            var pages = await finder.FindInvestorPagesAsync("https://example.com");

            Assert.DoesNotContain(pages, u => u.Contains("/careers", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(pages, u => u.Contains("/products", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(pages, u => u.Contains("/about-us", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(pages, u => u.Contains("/news", StringComparison.OrdinalIgnoreCase));
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }
    }

    public class DynamicPageAssessorTests
    {
        private static DynamicPageAssessment Assess(string url, string html)
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            return DynamicPageAssessor.Assess(url, html, doc);
        }

        [Fact]
        public void VueReportPageWithUnclaimedIdentity_ShouldRequireRendering()
        {
            var html = """
                <html><head><title>Unclaimed Dividend and Shares Information | Dabur</title></head>
                <body><div id="vue-Reports" data-reporttype="unclaimed-dividend">
                <li v-for="(pdfres, ip) in event.details.pdf"><a :href="pdfres.element.fullpath">PDF</a></li>
                </div></body></html>
                """;

            var result = Assess("https://www.dabur.com/investor/investor-information/unclaimed-dividend", html);

            Assert.True(result.RequiresRendering);
            Assert.Contains("Vue binding detected", result.Reasons);
            Assert.Contains("Dynamic report container detected", result.Reasons);
            Assert.Contains("IEPF/unclaimed signals detected", result.Reasons);
        }

        [Fact]
        public void VueReportPageWithOnlyNavUnclaimedLink_ShouldNotRequireRendering()
        {
            var html = """
                <html><head><title>Financial Results | Dabur</title></head>
                <body><nav><a href="/investor/investor-information/unclaimed-dividend">Unclaimed Dividend and Shares</a></nav>
                <div id="vue-Reports" data-reporttype="results"><a :href="pdfres.element.fullpath">PDF</a></div>
                </body></html>
                """;

            var result = Assess("https://www.dabur.com/investor/financial-information/results", html);

            Assert.False(result.RequiresRendering);
        }

        [Fact]
        public void StaticUnclaimedPage_ShouldNotRequireRendering()
        {
            var html = """
                <html><head><title>Unclaimed Dividend</title></head>
                <body><a href="/files/unclaimed-2024.pdf">Unclaimed 2024</a></body></html>
                """;

            var result = Assess("https://example.com/unclaimed-dividend", html);

            Assert.False(result.RequiresRendering);
        }

        [Fact]
        public void HttpAndBrowserHrefs_ShouldMergeToSameDocumentKey()
        {
            var httpHref = "https://www.dabur.com/Investors/Investor Information/Unclaimed Dividend/Notice.pdf";
            var browserHref = "https://www.dabur.com/Investors/Investor%20Information/Unclaimed%20Dividend/Notice.pdf";

            Assert.Equal(UrlNormalizer.DocumentKey(httpHref), UrlNormalizer.DocumentKey(browserHref));
        }
    }

    public class DocumentDetectorTests
    {
        private readonly DocumentDetector _detector = new();

        [Theory]
        [InlineData("https://example.com/reports/Annual_Report_2025.pdf", "Annual Report", true)]
        [InlineData("https://example.com/financials/Q2_Results.xlsx", "Q2 Results", true)]
        [InlineData("https://example.com/data.csv", "Data", true)]
        [InlineData("https://example.com/download.ashx?file=report.pdf", "Download PDF", true)]
        [InlineData("https://example.com/about-us", "About Us", false)]
        public void IsDocumentUrl_ShouldDetectCorrectly(string url, string text, bool expected)
        {
            var result = _detector.IsDocumentUrl(url, text);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ExtrapolateExtension_ShouldReturnCorrectExtension()
        {
            Assert.Equal(".pdf", _detector.ExtrapolateExtension("https://example.com/file.pdf", "application/pdf"));
            Assert.Equal(".xlsx", _detector.ExtrapolateExtension("https://example.com/file", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            Assert.Equal(".csv", _detector.ExtrapolateExtension("https://example.com/data.csv", "text/csv"));
        }
    }

    public class DocumentClassifierTests
    {
        private readonly DocumentClassifier _classifier = new();

        [Theory]
        [InlineData("Reliance_Annual_Report_2025.pdf", "Annual Report 2025", DocumentType.AnnualReport)]
        [InlineData("TCS_Q2_FY2026_Results.pdf", "Quarterly Results Q2", DocumentType.QuarterlyResults)]
        [InlineData("Infosys_Investor_Presentation_Q1.pdf", "Investor Presentation", DocumentType.InvestorPresentation)]
        [InlineData("Concall_Transcript_Q3_FY25.pdf", "Earnings Call Transcript", DocumentType.ConcallTranscript)]
        [InlineData("Shareholding_Pattern_Mar2025.pdf", "Shareholding Pattern", DocumentType.ShareholdingPattern)]
        [InlineData("Corporate_Governance_Q4.pdf", "Governance Report", DocumentType.CorporateGovernance)]
        [InlineData("CRISIL_Rating_Report.pdf", "Credit Rating", DocumentType.CreditRating)]
        public void Classify_ShouldReturnExpectedDocumentType(string filename, string text, DocumentType expected)
        {
            var docType = _classifier.Classify(filename, "https://example.com/" + filename, text, "Investor Relations");
            Assert.Equal(expected, docType);
        }
    }

    public class FinancialPeriodDetectorTests
    {
        private readonly FinancialPeriodDetector _detector = new();

        [Fact]
        public void ExtractPeriod_ShouldDetectFYAndQuarter()
        {
            var (fy, q, date) = _detector.ExtractPeriod("Q2 FY2026 Financial Results", "Q2_FY2026_Results.pdf", "https://example.com/Q2_FY2026.pdf");
            Assert.Equal("FY2026", fy);
            Assert.Equal("Q2", q);
        }

        [Fact]
        public void ExtractPeriod_ShouldDetectYearRangeAndMonth()
        {
            var (fy, q, date) = _detector.ExtractPeriod("Annual Report for March 2025", "Annual_Report_2024-25.pdf", "https://example.com/ar.pdf");
            Assert.Equal("FY2024-25", fy);
            Assert.Equal("March 2025", date);
        }
    }

    public class DuplicateDetectorTests
    {
        [Fact]
        public async Task IsUrlDuplicateAsync_ShouldDetectDuplicateUrl()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "DupTestDb1")
                .Options;

            using var db = new ApplicationDbContext(options);
            db.Companies.Add(new Company { Id = 1, CompanyName = "Test Co" });
            db.InvestorDocuments.Add(new InvestorDocument
            {
                Id = 1,
                CompanyId = 1,
                DocumentTitle = "Report",
                DocumentUrl = "https://example.com/doc.pdf",
                Downloaded = true
            });
            await db.SaveChangesAsync();

            var dupDetector = new DuplicateDetector(db);
            bool isDup = await dupDetector.IsUrlDuplicateAsync("https://example.com/doc.pdf", 1);
            bool notDup = await dupDetector.IsUrlDuplicateAsync("https://example.com/other.pdf", 1);

            Assert.True(isDup);
            Assert.False(notDup);
        }

        [Fact]
        public async Task IsHashDuplicateAsync_ShouldDetectDuplicateHash()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "DupTestDb2")
                .Options;

            using var db = new ApplicationDbContext(options);
            db.Companies.Add(new Company { Id = 2, CompanyName = "Test Co 2" });
            db.InvestorDocuments.Add(new InvestorDocument
            {
                Id = 2,
                CompanyId = 2,
                DocumentTitle = "Report 2",
                DocumentUrl = "https://example.com/doc2.pdf",
                SHA256 = "abc123hash",
                Downloaded = true
            });
            await db.SaveChangesAsync();

            var dupDetector = new DuplicateDetector(db);
            bool isHashDup = await dupDetector.IsHashDuplicateAsync("abc123hash", 2);
            bool notHashDup = await dupDetector.IsHashDuplicateAsync("differenthash", 2);

            Assert.True(isHashDup);
            Assert.False(notHashDup);
        }
    }

    public class ExcelImporterExporterTests
    {
        [Fact]
        public async Task ImportAndExportExcel_ShouldWorkSeamlessly()
        {
            // Create a test Excel workbook in memory
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Companies");
            ws.Cell(1, 1).Value = "Company Name";
            ws.Cell(1, 2).Value = "NSE Symbol";
            ws.Cell(1, 3).Value = "BSE Code";
            ws.Cell(1, 4).Value = "Website";

            ws.Cell(2, 1).Value = "Reliance Industries";
            ws.Cell(2, 2).Value = "RELIANCE";
            ws.Cell(2, 3).Value = "500325";

            ws.Cell(3, 1).Value = "Tata Consultancy Services";
            ws.Cell(3, 2).Value = "TCS";
            ws.Cell(3, 3).Value = "532540";

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;

            var importer = new ExcelImporter();
            var imported = await importer.ImportCompaniesAsync(ms);

            Assert.Equal(2, imported.Count);
            Assert.Equal("Reliance Industries", imported[0].CompanyName);
            Assert.Equal("RELIANCE", imported[0].NSESymbol);
            Assert.Equal("Tata Consultancy Services", imported[1].CompanyName);

            // Test Exporting 3-Sheet Report
            var exporter = new ExcelExporter();
            var companies = new List<Company>
            {
                new Company
                {
                    Id = 1,
                    CompanyName = "Reliance Industries",
                    NSESymbol = "RELIANCE",
                    Website = "https://www.ril.com",
                    Status = CompanyStatus.Completed,
                    Documents = new List<InvestorDocument>
                    {
                        new InvestorDocument
                        {
                            DocumentTitle = "Annual Report 2025",
                            DocumentUrl = "https://www.ril.com/ar2025.pdf",
                            DocumentType = "AnnualReport",
                            FinancialYear = "FY2025",
                            Downloaded = true,
                            Status = "Downloaded"
                        }
                    }
                }
            };

            var reportBytes = await exporter.ExportReportAsync(companies);
            Assert.NotNull(reportBytes);
            Assert.True(reportBytes.Length > 0);

            // Verify 3 sheets in exported Excel
            using var reportMs = new MemoryStream(reportBytes);
            using var reportWb = new XLWorkbook(reportMs);
            Assert.Equal(3, reportWb.Worksheets.Count);
            Assert.True(reportWb.Worksheets.Contains("Summary"));
            Assert.True(reportWb.Worksheets.Contains("Documents"));
            Assert.True(reportWb.Worksheets.Contains("Failed"));
        }

        [Fact]
        public void GenerateSampleCompaniesFile()
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Companies");

            ws.Cell(1, 1).Value = "Company Name";
            ws.Cell(1, 2).Value = "NSE Symbol";
            ws.Cell(1, 3).Value = "BSE Code";
            ws.Cell(1, 4).Value = "Website";

            ws.Cell(2, 1).Value = "Reliance Industries";
            ws.Cell(2, 2).Value = "RELIANCE";
            ws.Cell(2, 3).Value = "500325";
            ws.Cell(2, 4).Value = "https://www.ril.com";

            ws.Cell(3, 1).Value = "Tata Consultancy Services";
            ws.Cell(3, 2).Value = "TCS";
            ws.Cell(3, 3).Value = "532540";
            ws.Cell(3, 4).Value = "https://www.tcs.com";

            ws.Cell(4, 1).Value = "Infosys";
            ws.Cell(4, 2).Value = "INFY";
            ws.Cell(4, 3).Value = "500209";
            ws.Cell(4, 4).Value = "https://www.infosys.com";

            ws.Cell(5, 1).Value = "HDFC Bank";
            ws.Cell(5, 2).Value = "HDFCBANK";
            ws.Cell(5, 3).Value = "500180";
            ws.Cell(5, 4).Value = "https://www.hdfcbank.com";

            ws.Cell(6, 1).Value = "ITC";
            ws.Cell(6, 2).Value = "ITC";
            ws.Cell(6, 3).Value = "500875";
            ws.Cell(6, 4).Value = "https://www.itcportal.com";

            var outputDir = Path.Combine(Path.GetTempPath(), $"ida-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            try
            {
                var outputPath = Path.Combine(outputDir, "companies.xlsx");
                workbook.SaveAs(outputPath);

                Assert.True(File.Exists(outputPath));
                using var saved = new XLWorkbook(outputPath);
                Assert.Equal("ITC", saved.Worksheet("Companies").Cell(6, 1).GetString());
            }
            finally
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
