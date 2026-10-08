using System.Collections.Generic;
using InvestorDataAutomation.Core.Models;

namespace InvestorDataAutomation.Core.DTOs
{
    public class CompanyImportDto
    {
        public required string CompanyName { get; set; }
        public string? NSESymbol { get; set; }
        public string? BSECode { get; set; }
        public string? Website { get; set; }
    }

    public class ProcessingSummaryDto
    {
        public int TotalCompanies { get; set; }
        public int Completed { get; set; }
        public int Processing { get; set; }
        public int Pending { get; set; }
        public int Partial { get; set; }
        public int Failed { get; set; }
        public int NeedsReview { get; set; }

        public int DocumentsFound { get; set; }
        public int DocumentsDownloaded { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int FailedDownloads { get; set; }

        public bool IsRunning { get; set; }
        public bool IsPaused { get; set; }
    }

    public class CompanyDetailDto
    {
        public int Id { get; set; }
        public required string CompanyName { get; set; }
        public string? NSESymbol { get; set; }
        public string? BSECode { get; set; }
        public string? Website { get; set; }
        public string? InvestorPage { get; set; }
        public double WebsiteConfidence { get; set; }
        public string Status { get; set; } = "Pending";
        public string? FailureReason { get; set; }
        public int RetryCount { get; set; }

        public int PagesScanned { get; set; }
        public int DocumentsFound { get; set; }
        public int DocumentsDownloaded { get; set; }
        public int FailedDownloads { get; set; }

        public List<DocumentDto> Documents { get; set; } = new();
        public List<CrawlLogDto> Logs { get; set; } = new();
    }

    public class DocumentDto
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public required string DocumentTitle { get; set; }
        public required string DocumentUrl { get; set; }
        public string? OriginalFileName { get; set; }
        public string? LocalFileName { get; set; }
        public string? LocalFilePath { get; set; }
        public string DocumentType { get; set; } = "Other";
        public string? FinancialYear { get; set; }
        public string? Quarter { get; set; }
        public string? DocumentDate { get; set; }
        public string? FileExtension { get; set; }
        public long FileSize { get; set; }
        public string Status { get; set; } = "Discovered";
        public string? ErrorMessage { get; set; }
        public string? DownloadUrl { get; set; }
    }

    public class CrawlLogDto
    {
        public int Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int PagesScanned { get; set; }
        public int DocumentsFound { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
