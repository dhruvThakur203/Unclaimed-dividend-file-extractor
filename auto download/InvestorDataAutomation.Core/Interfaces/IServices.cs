using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.DTOs;
using InvestorDataAutomation.Core.Models;

namespace InvestorDataAutomation.Core.Interfaces
{
    public record WebsiteSearchResult(string Url, double Confidence, string TargetDomain);

    public interface ICompanyWebsiteFinder
    {
        Task<WebsiteSearchResult> FindOfficialWebsiteAsync(string companyName, string? nseSymbol = null, string? bseCode = null, CancellationToken cancellationToken = default);
    }

    public enum InvestorPageSource
    {
        /// <summary>The website root itself.</summary>
        Homepage,
        /// <summary>A link actually present on the homepage.</summary>
        HomepageLink,
        /// <summary>An unvalidated guess such as /investors.</summary>
        StandardPath,
        /// <summary>A link discovered on a crawled (non-homepage) page.</summary>
        CrawledLink,
        /// <summary>A URL supplied without discovery metadata.</summary>
        Provided
    }

    /// <summary>A page the crawler may visit, with the metadata used to prioritize it.</summary>
    public record InvestorPageCandidate(
        string Url,
        InvestorPageSource Source,
        int Priority,
        string Reason,
        string? ParentUrl = null,
        string? AnchorText = null,
        int Depth = 1)
    {
        public bool IsGuessedPath => Source == InvestorPageSource.StandardPath;
    }

    public interface IInvestorPageFinder
    {
        Task<List<string>> FindInvestorPagesAsync(string websiteUrl, CancellationToken cancellationToken = default);
        Task<List<InvestorPageCandidate>> FindInvestorPageCandidatesAsync(string websiteUrl, CancellationToken cancellationToken = default);
    }

    public interface IWebsiteCrawler
    {
        Task<List<DiscoveredDocument>> CrawlInvestorPagesAsync(Company company, List<string> investorPages, CancellationToken cancellationToken = default);
        Task<List<DiscoveredDocument>> CrawlInvestorPagesAsync(Company company, IReadOnlyList<InvestorPageCandidate> candidates, CancellationToken cancellationToken = default);

        /// <summary>Audit of the most recent crawl performed by this instance, or null before the first crawl.</summary>
        CrawlAuditReport? LastCrawlAudit { get; }
    }

    public record DiscoveredDocument(
        string Title,
        string Url,
        string SourcePageUrl,
        string? FileExtension,
        string DocumentType,
        string? FinancialYear,
        string? Quarter,
        string? DocumentDate
    );

    public record TargetDocumentCandidate(
        string DocumentUrl,
        string FileName,
        string AnchorText,
        string? LinkTitle,
        string? PageTitle,
        string? SurroundingContext);

    public enum TargetDocumentFilterAction
    {
        Ignore,
        PossibleCandidate,
        Download
    }

    public record TargetDocumentFilterResult(
        int Score,
        string Reason,
        TargetDocumentFilterAction Action);

    public interface ITargetDocumentRelevanceFilter
    {
        TargetDocumentFilterResult Evaluate(TargetDocumentCandidate candidate);
    }

    public interface IDocumentDetector
    {
        bool IsDocumentUrl(string url, string text);
        string ExtrapolateExtension(string url, string contentType);
    }

    public interface IDocumentClassifier
    {
        DocumentType Classify(string filename, string url, string anchorText, string pageTitle);
    }

    public interface IFinancialPeriodDetector
    {
        (string? FinancialYear, string? Quarter, string? DocumentDate) ExtractPeriod(string text, string filename, string url);
    }

    public interface IDocumentDownloader
    {
        Task<DownloadResult> DownloadAsync(InvestorDocument document, string companyStoragePath, CancellationToken cancellationToken = default);
    }

    public record DownloadResult(bool Success, string? LocalPath, string? LocalFileName, long FileSize, string? SHA256, string? ErrorMessage);

    public interface IDuplicateDetector
    {
        Task<bool> IsUrlDuplicateAsync(string documentUrl, int companyId);
        Task<bool> IsHashDuplicateAsync(string sha256Hash, int companyId);
    }

    public interface ICompanyProcessor
    {
        Task ProcessCompanyAsync(int companyId, CancellationToken cancellationToken = default);
    }

    public interface IExcelImporter
    {
        Task<List<CompanyImportDto>> ImportCompaniesAsync(Stream stream);
    }

    public interface IExcelExporter
    {
        Task<byte[]> ExportReportAsync(List<Company> companies);
    }

    public interface ICrawlLogger
    {
        Task LogAsync(int companyId, string url, string stage, string status, int pagesScanned, int docsFound, string? error = null);
    }

    public interface IJobQueueService
    {
        Task QueueAllPendingAsync();
        Task StartProcessingAsync();
        Task PauseProcessingAsync();
        Task ResumeProcessingAsync();
        Task RetryFailedAsync();
        Task ClearAllAsync();
        ProcessingSummaryDto GetSummary();
        bool IsRunning { get; }
        bool IsPaused { get; }
    }
}
