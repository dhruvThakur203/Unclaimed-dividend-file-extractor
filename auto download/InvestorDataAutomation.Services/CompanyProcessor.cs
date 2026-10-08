using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    public class CompanyProcessor : ICompanyProcessor
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ICompanyWebsiteFinder _websiteFinder;
        private readonly IInvestorPageFinder _investorPageFinder;
        private readonly IWebsiteCrawler _crawler;
        private readonly IDocumentDownloader _downloader;
        private readonly IDuplicateDetector _duplicateDetector;
        private readonly ICrawlLogger _crawlLogger;
        private readonly IOptions<CrawlOptions> _options;
        private readonly ILogger<CompanyProcessor> _logger;

        public CompanyProcessor(
            ApplicationDbContext dbContext,
            ICompanyWebsiteFinder websiteFinder,
            IInvestorPageFinder investorPageFinder,
            IWebsiteCrawler crawler,
            IDocumentDownloader downloader,
            IDuplicateDetector duplicateDetector,
            ICrawlLogger crawlLogger,
            IOptions<CrawlOptions> options,
            ILogger<CompanyProcessor> logger)
        {
            _dbContext = dbContext;
            _websiteFinder = websiteFinder;
            _investorPageFinder = investorPageFinder;
            _crawler = crawler;
            _downloader = downloader;
            _duplicateDetector = duplicateDetector;
            _crawlLogger = crawlLogger;
            _options = options;
            _logger = logger;
        }

        public async Task ProcessCompanyAsync(int companyId, CancellationToken cancellationToken = default)
        {
            var company = await _dbContext.Companies
                .Include(c => c.Documents)
                .FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken);

            if (company == null) return;

            _logger.LogInformation("Starting automated processing for company: {CompanyName} (ID: {Id})", company.CompanyName, company.Id);

            company.Status = CompanyStatus.Processing;
            company.UpdatedDate = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                // Step 1: Website Discovery
                if (string.IsNullOrWhiteSpace(company.Website))
                {
                    var searchRes = await _websiteFinder.FindOfficialWebsiteAsync(
                        company.CompanyName, company.NSESymbol, company.BSECode, cancellationToken);

                    company.Website = searchRes.Url;
                    company.WebsiteConfidence = searchRes.Confidence;

                    if (searchRes.Confidence < _options.Value.WebsiteConfidenceThreshold || string.IsNullOrWhiteSpace(searchRes.Url))
                    {
                        company.Status = CompanyStatus.NeedsReview;
                        company.FailureReason = $"Official website confidence ({searchRes.Confidence:P0}) below threshold";
                        company.UpdatedDate = DateTime.UtcNow;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await _crawlLogger.LogAsync(companyId, searchRes.Url, "WebsiteDiscovery", "NeedsReview", 0, 0, company.FailureReason);
                        return;
                    }
                }

                // Step 2: Investor Page Discovery
                var investorPages = await _investorPageFinder.FindInvestorPageCandidatesAsync(company.Website, cancellationToken);
                var primaryInvestorPage = InvestorPageFinder.SelectPrimaryInvestorPage(investorPages);
                if (primaryInvestorPage != null)
                {
                    company.InvestorPage = primaryInvestorPage;
                    _logger.LogInformation("Primary investor page for {Company}: {Url} ({Count} candidate pages)",
                        company.CompanyName, primaryInvestorPage, investorPages.Count);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                await _crawlLogger.LogAsync(companyId, company.Website, "InvestorPageDiscovery", "Success", investorPages.Count, 0);

                // Step 3: Crawl Investor Pages
                var discoveredDocs = await _crawler.CrawlInvestorPagesAsync(company, investorPages, cancellationToken);
                var audit = _crawler.LastCrawlAudit;

                // Storage root: InvestorData/{CompanyName}
                var baseStorageDir = Path.GetFullPath(_options.Value.StorageDirectory);
                var companyStorageDir = Path.Combine(baseStorageDir, SanitizeFolderName(company.CompanyName));

                int downloadedCount = 0;
                int skippedDuplicates = 0;
                int failedDownloads = 0;

                // Documents already stored for this company, keyed the same way the crawler de-duplicates them.
                var downloadedKeys = new HashSet<string>(
                    company.Documents.Where(d => d.Downloaded).Select(d => UrlNormalizer.DocumentKey(d.DocumentUrl)),
                    StringComparer.Ordinal);

                // Step 4: Download & Deduplicate Documents
                foreach (var docDto in discoveredDocs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (downloadedKeys.Contains(UrlNormalizer.DocumentKey(docDto.Url))
                        || await _duplicateDetector.IsUrlDuplicateAsync(docDto.Url, company.Id))
                    {
                        skippedDuplicates++;
                        audit?.Downloads.Add(new CrawlAuditDownload(docDto.Url, "SkippedDuplicateUrl", 0, null));
                        continue;
                    }

                    var docEntity = new InvestorDocument
                    {
                        CompanyId = company.Id,
                        DocumentTitle = docDto.Title,
                        DocumentUrl = docDto.Url,
                        DocumentType = docDto.DocumentType,
                        FinancialYear = docDto.FinancialYear,
                        Quarter = docDto.Quarter,
                        DocumentDate = docDto.DocumentDate,
                        FileExtension = docDto.FileExtension,
                        Status = "Downloading",
                        CreatedDate = DateTime.UtcNow,
                        UpdatedDate = DateTime.UtcNow
                    };
                    company.Documents.Add(docEntity);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var downloadResult = await _downloader.DownloadAsync(docEntity, companyStorageDir, cancellationToken);

                    if (downloadResult.Success)
                    {
                        if (!string.IsNullOrEmpty(downloadResult.SHA256) && await _duplicateDetector.IsHashDuplicateAsync(downloadResult.SHA256, company.Id))
                        {
                            docEntity.Status = "SkippedDuplicate";
                            docEntity.Downloaded = false;
                            if (File.Exists(downloadResult.LocalPath)) File.Delete(downloadResult.LocalPath);
                            skippedDuplicates++;
                        }
                        else
                        {
                            docEntity.Status = "Downloaded";
                            docEntity.Downloaded = true;
                            docEntity.DownloadDate = DateTime.UtcNow;
                            docEntity.LocalFileName = downloadResult.LocalFileName;
                            docEntity.LocalFilePath = downloadResult.LocalPath;
                            docEntity.FileSize = downloadResult.FileSize;
                            docEntity.SHA256 = downloadResult.SHA256;
                            downloadedKeys.Add(UrlNormalizer.DocumentKey(docDto.Url));
                            downloadedCount++;
                        }
                    }
                    else
                    {
                        docEntity.Status = "Failed";
                        docEntity.ErrorMessage = downloadResult.ErrorMessage;
                        failedDownloads++;
                    }

                    audit?.Downloads.Add(new CrawlAuditDownload(docDto.Url, docEntity.Status, downloadResult.FileSize, downloadResult.ErrorMessage));
                    docEntity.UpdatedDate = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                (company.Status, company.FailureReason) = DetermineStatus(discoveredDocs.Count, downloadedCount, skippedDuplicates, failedDownloads);
                company.UpdatedDate = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                await _crawlLogger.LogAsync(companyId, company.Website, "CrawlAndDownload", company.Status.ToString(),
                    audit?.PagesProcessed ?? investorPages.Count, discoveredDocs.Count);

                _logger.LogInformation("Company {Company} processing finished: Status={Status}, Found={Found}, Downloaded={DL}, Duplicates={Dup}, Failed={Fail}",
                    company.CompanyName, company.Status, discoveredDocs.Count, downloadedCount, skippedDuplicates, failedDownloads);

                if (_options.Value.EnableCrawlAudit && audit != null)
                {
                    _logger.LogInformation("[CRAWL AUDIT] {Report}", JsonSerializer.Serialize(audit));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Processing cancelled for {Company}; returning it to the pending queue", company.CompanyName);
                await SaveTerminalStateAsync(company, CompanyStatus.Pending, "Processing was cancelled before completion", incrementRetry: false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing company {Company}", company.CompanyName);
                await SaveTerminalStateAsync(company, CompanyStatus.Failed, ex.Message, incrementRetry: true);
            }
        }

        /// <summary>Keeps the existing "{Company Name}" folder layout; dots/spaces are trimmed so "." or ".." cannot escape the storage root.</summary>
        internal static string SanitizeFolderName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()))
            {
                name = name.Replace(c, '_');
            }
            name = name.Trim().Trim('.', ' ');
            return string.IsNullOrEmpty(name) ? "Company" : name;
        }

        internal static (CompanyStatus Status, string? FailureReason) DetermineStatus(int discovered, int downloaded, int duplicates, int failed)
        {
            if (downloaded > 0)
                return failed > 0 ? (CompanyStatus.Partial, $"{failed} document download(s) failed") : (CompanyStatus.Completed, null);
            if (discovered == 0)
                return (CompanyStatus.NeedsReview, "No investor documents found during crawl");
            if (failed == 0 && duplicates > 0)
                return (CompanyStatus.Completed, null);
            return (CompanyStatus.Failed, "All document downloads failed");
        }

        /// <summary>
        /// Persists the outcome after an interruption without using the (possibly cancelled) job token.
        /// Documents left mid-download are marked failed so they are never mistaken for completed files.
        /// </summary>
        private async Task SaveTerminalStateAsync(Company company, CompanyStatus status, string reason, bool incrementRetry)
        {
            try
            {
                foreach (var doc in company.Documents.Where(d => d.Status == "Downloading"))
                {
                    doc.Status = "Failed";
                    doc.Downloaded = false;
                    doc.ErrorMessage = reason;
                    doc.UpdatedDate = DateTime.UtcNow;
                }

                company.Status = status;
                company.FailureReason = reason;
                if (incrementRetry) company.RetryCount++;
                company.UpdatedDate = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(CancellationToken.None);

                await _crawlLogger.LogAsync(company.Id, company.Website ?? "", "ProcessCompany", status.ToString(), 0, 0, reason);
            }
            catch (Exception saveError)
            {
                _logger.LogError(saveError, "Could not persist {Status} state for company {Company}", status, company.CompanyName);
            }
        }
    }
}
