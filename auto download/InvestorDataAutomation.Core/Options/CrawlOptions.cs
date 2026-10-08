namespace InvestorDataAutomation.Core.Options
{
    public class CrawlOptions
    {
        public const string Position = "CrawlOptions";

        public int MaxPagesPerCompany { get; set; } = 100;
        public int MaxCrawlDepth { get; set; } = 5;

        /// <summary>Timeout for a single page request (headers and body).</summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>Retries for transient HTTP failures (408/429/5xx, timeouts, disconnects). Clamped to 0–5. 404/403 are never retried.</summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>Base delay between retries; attempt N waits N × this value (or the server's Retry-After, capped at 10 s).</summary>
        public int RetryBaseDelayMilliseconds { get; set; } = 1000;

        public int MaxConcurrentCompanies { get; set; } = 5;
        public double WebsiteConfidenceThreshold { get; set; } = 0.70;
        public bool EnablePlaywrightFallback { get; set; } = true;

        /// <summary>Maximum number of dynamically rendered IEPF/unclaimed pages to open in Playwright per company.</summary>
        public int MaxDynamicPagesToRender { get; set; } = 5;

        /// <summary>Maximum number of headless browsers running at once across all companies.</summary>
        public int MaxConcurrentBrowsers { get; set; } = 2;

        /// <summary>Timeout for downloading a single document (headers and body).</summary>
        public int DownloadTimeoutSeconds { get; set; } = 300;

        /// <summary>Emit a structured [CRAWL AUDIT] report per company (diagnostics only).</summary>
        public bool EnableCrawlAudit { get; set; } = false;

        public string StorageDirectory { get; set; } = "InvestorData";
        public string DatabaseProvider { get; set; } = "Sqlite"; // Sqlite or SqlServer
        public string ConnectionString { get; set; } = "Data Source=investor_data.db";

        /// <summary>Minimum relevance score (0–∞) required to download a discovered document.</summary>
        public int TargetDocumentScoreThreshold { get; set; } = 70;

        /// <summary>Minimum score to log as a possible target document candidate (not downloaded).</summary>
        public int TargetDocumentPossibleCandidateMinScore { get; set; } = 40;
    }
}
