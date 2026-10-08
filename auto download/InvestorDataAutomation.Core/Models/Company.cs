using System;
using System.Collections.Generic;

namespace InvestorDataAutomation.Core.Models
{
    public class Company
    {
        public int Id { get; set; }
        public required string CompanyName { get; set; }
        public string? NSESymbol { get; set; }
        public string? BSECode { get; set; }
        public string? Website { get; set; }
        public string? InvestorPage { get; set; }
        public double WebsiteConfidence { get; set; } = 0.0;
        public CompanyStatus Status { get; set; } = CompanyStatus.Pending;
        public string? FailureReason { get; set; }
        public int RetryCount { get; set; } = 0;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

        public ICollection<InvestorDocument> Documents { get; set; } = new List<InvestorDocument>();
        public ICollection<CrawlLog> CrawlLogs { get; set; } = new List<CrawlLog>();
    }
}
