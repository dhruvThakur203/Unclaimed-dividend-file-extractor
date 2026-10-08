using System;

namespace InvestorDataAutomation.Core.Models
{
    public class InvestorDocument
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public Company? Company { get; set; }
        
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
        public long FileSize { get; set; } = 0;
        public string? SHA256 { get; set; }
        
        public bool Downloaded { get; set; } = false;
        public DateTime? DownloadDate { get; set; }
        public string Status { get; set; } = "Discovered"; // Discovered, Downloaded, SkippedDuplicate, Failed
        public string? ErrorMessage { get; set; }
        
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;
    }
}
