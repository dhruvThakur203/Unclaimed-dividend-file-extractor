using System;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Data;
using Microsoft.EntityFrameworkCore;

namespace InvestorDataAutomation.Services
{
    public class DuplicateDetector : IDuplicateDetector
    {
        private readonly ApplicationDbContext _dbContext;

        public DuplicateDetector(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<bool> IsUrlDuplicateAsync(string documentUrl, int companyId)
        {
            if (string.IsNullOrWhiteSpace(documentUrl)) return false;
            return await _dbContext.InvestorDocuments
                .AnyAsync(d => d.CompanyId == companyId && d.DocumentUrl == documentUrl && d.Downloaded);
        }

        public async Task<bool> IsHashDuplicateAsync(string sha256Hash, int companyId)
        {
            if (string.IsNullOrWhiteSpace(sha256Hash)) return false;
            return await _dbContext.InvestorDocuments
                .AnyAsync(d => d.CompanyId == companyId && d.SHA256 == sha256Hash && d.Downloaded);
        }
    }
}
