using System;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Data;

namespace InvestorDataAutomation.Services
{
    public class CrawlLogger : ICrawlLogger
    {
        private readonly ApplicationDbContext _dbContext;

        public CrawlLogger(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task LogAsync(int companyId, string url, string stage, string status, int pagesScanned, int docsFound, string? error = null)
        {
            var log = new CrawlLog
            {
                CompanyId = companyId,
                Url = url,
                Stage = stage,
                Status = status,
                PagesScanned = pagesScanned,
                DocumentsFound = docsFound,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                ErrorMessage = error
            };

            _dbContext.CrawlLogs.Add(log);
            await _dbContext.SaveChangesAsync();
        }
    }
}
