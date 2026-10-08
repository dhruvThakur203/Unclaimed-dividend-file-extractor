using System;

namespace InvestorDataAutomation.Core.Models
{
    public class CrawlLog
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public Company? Company { get; set; }
        
        public required string Url { get; set; }
        public string Stage { get; set; } = "Crawl";
        public string Status { get; set; } = "Success";
        public int PagesScanned { get; set; } = 0;
        public int DocumentsFound { get; set; } = 0;
        
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
