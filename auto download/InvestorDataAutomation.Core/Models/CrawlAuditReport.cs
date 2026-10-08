using System;
using System.Collections.Generic;

namespace InvestorDataAutomation.Core.Models
{
    /// <summary>
    /// Per-company record of what the crawler did and why. Always collected (cheap);
    /// serialized to the log only when <c>CrawlOptions.EnableCrawlAudit</c> is on.
    /// </summary>
    public sealed class CrawlAuditReport
    {
        public string CompanyName { get; init; } = "";
        public List<CrawlAuditCandidate> CandidatePages { get; } = new();
        public List<CrawlAuditPage> VisitedPages { get; } = new();
        public int PagesProcessed { get; set; }
        public int PagesNotFound { get; set; }
        public int PagesFailed { get; set; }
        public int PagesSkippedOffSite { get; set; }
        public int PagesLeftInQueue { get; set; }
        public int HttpRequests { get; set; }
        public string StopReason { get; set; } = "";

        public List<string> DynamicPages { get; } = new();
        public List<string> RenderedPages { get; } = new();
        public string PlaywrightDecision { get; set; } = "not evaluated";
        public int BrowserLaunches { get; set; }
        public TimeSpan DynamicDiscoveryTime { get; set; }

        public int HttpDocumentCandidates { get; set; }
        public int RenderedDocumentCandidates { get; set; }
        public int DuplicateCandidates { get; set; }
        public int FilterDownload { get; set; }
        public int FilterPossible { get; set; }
        public int FilterIgnored { get; set; }
        public List<CrawlAuditDocument> Documents { get; } = new();
        public List<CrawlAuditDownload> Downloads { get; } = new();
    }

    public sealed record CrawlAuditCandidate(string Url, int Priority, string Source, string Reason);

    public sealed record CrawlAuditPage(string Url, int Priority, string Source, int Depth, int? HttpStatus, int Attempts, string Outcome, bool? Dynamic);

    public sealed record CrawlAuditDocument(string Url, string SourcePage, string Origin, int Score, string Action, string Reason);

    public sealed record CrawlAuditDownload(string Url, string Status, long Bytes, string? Error);
}
