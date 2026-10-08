using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.DTOs;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly IJobQueueService _jobQueueService;
        private readonly IExcelImporter _excelImporter;
        private readonly IExcelExporter _excelExporter;
        private readonly IOptions<CrawlOptions> _options;

        public HomeController(
            ApplicationDbContext dbContext,
            IJobQueueService jobQueueService,
            IExcelImporter excelImporter,
            IExcelExporter excelExporter,
            IOptions<CrawlOptions> options)
        {
            _dbContext = dbContext;
            _jobQueueService = jobQueueService;
            _excelImporter = excelImporter;
            _excelExporter = excelExporter;
            _options = options;
        }

        public async Task<IActionResult> Index(string? statusFilter = null, string? search = null)
        {
            var summary = _jobQueueService.GetSummary();
            ViewBag.Summary = summary;

            IQueryable<Company> query = _dbContext.Companies
                .Include(c => c.Documents)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<CompanyStatus>(statusFilter, true, out var status))
            {
                query = query.Where(c => c.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(c => c.CompanyName.Contains(search) || (c.NSESymbol != null && c.NSESymbol.Contains(search)));
            }

            var companies = await query.OrderBy(c => c.Id).ToListAsync();
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Search = search;

            return View(companies);
        }

        public async Task<IActionResult> Details(int id)
        {
            var company = await _dbContext.Companies
                .Include(c => c.Documents)
                .Include(c => c.CrawlLogs)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (company == null) return NotFound();

            var detail = new CompanyDetailDto
            {
                Id = company.Id,
                CompanyName = company.CompanyName,
                NSESymbol = company.NSESymbol,
                BSECode = company.BSECode,
                Website = company.Website,
                InvestorPage = company.InvestorPage,
                WebsiteConfidence = company.WebsiteConfidence,
                Status = company.Status.ToString(),
                FailureReason = company.FailureReason,
                RetryCount = company.RetryCount,
                PagesScanned = company.CrawlLogs.Sum(l => l.PagesScanned),
                DocumentsFound = company.Documents.Count,
                DocumentsDownloaded = company.Documents.Count(d => d.Downloaded),
                FailedDownloads = company.Documents.Count(d => d.Status == "Failed"),
                Documents = company.Documents.Select(d => new DocumentDto
                {
                    Id = d.Id,
                    CompanyId = d.CompanyId,
                    CompanyName = company.CompanyName,
                    DocumentTitle = d.DocumentTitle,
                    DocumentUrl = d.DocumentUrl,
                    OriginalFileName = d.OriginalFileName,
                    LocalFileName = d.LocalFileName,
                    LocalFilePath = d.LocalFilePath,
                    DocumentType = d.DocumentType,
                    FinancialYear = d.FinancialYear,
                    Quarter = d.Quarter,
                    DocumentDate = d.DocumentDate,
                    FileExtension = d.FileExtension,
                    FileSize = d.FileSize,
                    Status = d.Status,
                    ErrorMessage = d.ErrorMessage,
                    DownloadUrl = d.Downloaded ? Url.Action("DownloadDocument", new { id = d.Id }) : null
                }).ToList(),
                Logs = company.CrawlLogs.Select(l => new CrawlLogDto
                {
                    Id = l.Id,
                    Url = l.Url,
                    Stage = l.Stage,
                    Status = l.Status,
                    PagesScanned = l.PagesScanned,
                    DocumentsFound = l.DocumentsFound,
                    StartedAt = l.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    CompletedAt = l.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    ErrorMessage = l.ErrorMessage
                }).ToList()
            };

            return View(detail);
        }

        [HttpPost]
        public async Task<IActionResult> UploadExcel(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select a valid Excel (.xlsx) file.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var stream = file.OpenReadStream();
                var imported = await _excelImporter.ImportCompaniesAsync(stream);

                if (imported.Count == 0)
                {
                    TempData["Error"] = "No company records found in the uploaded file.";
                    return RedirectToAction(nameof(Index));
                }

                foreach (var item in imported)
                {
                    var exists = await _dbContext.Companies.AnyAsync(c => c.CompanyName.ToLower() == item.CompanyName.ToLower());
                    if (!exists)
                    {
                        _dbContext.Companies.Add(new Company
                        {
                            CompanyName = item.CompanyName,
                            NSESymbol = item.NSESymbol,
                            BSECode = item.BSECode,
                            Website = item.Website,
                            WebsiteConfidence = !string.IsNullOrWhiteSpace(item.Website) ? 1.0 : 0.0,
                            Status = CompanyStatus.Pending,
                            CreatedDate = DateTime.UtcNow,
                            UpdatedDate = DateTime.UtcNow
                        });
                    }
                }

                await _dbContext.SaveChangesAsync();
                TempData["Success"] = $"Successfully imported {imported.Count} companies.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error reading Excel file: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Start()
        {
            await _jobQueueService.StartProcessingAsync();
            TempData["Success"] = "Automated processing started.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Pause()
        {
            await _jobQueueService.PauseProcessingAsync();
            TempData["Info"] = "Processing paused.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Resume()
        {
            await _jobQueueService.ResumeProcessingAsync();
            TempData["Success"] = "Processing resumed.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> RetryFailed()
        {
            await _jobQueueService.RetryFailedAsync();
            TempData["Success"] = "Queued failed/needs-review companies for retry.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> ClearAll()
        {
            await _jobQueueService.ClearAllAsync();
            TempData["Info"] = "All company records, crawl logs, and documents cleared.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> ExportReport()
        {
            var companies = await _dbContext.Companies
                .Include(c => c.Documents)
                .AsNoTracking()
                .ToListAsync();

            var bytes = await _excelExporter.ExportReportAsync(companies);
            var filename = $"InvestorDataReport_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        public async Task<IActionResult> DownloadDocument(int id)
        {
            var doc = await _dbContext.InvestorDocuments
                .Include(d => d.Company)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (doc == null || !doc.Downloaded || string.IsNullOrEmpty(doc.LocalFilePath) || !System.IO.File.Exists(doc.LocalFilePath))
            {
                return NotFound("Document file not found on disk.");
            }

            var bytes = await System.IO.File.ReadAllBytesAsync(doc.LocalFilePath);
            var fileName = doc.LocalFileName ?? Path.GetFileName(doc.LocalFilePath);
            return File(bytes, "application/octet-stream", fileName);
        }

        [HttpGet]
        public IActionResult GetSummaryApi()
        {
            var summary = _jobQueueService.GetSummary();
            return Json(summary);
        }
    }
}
