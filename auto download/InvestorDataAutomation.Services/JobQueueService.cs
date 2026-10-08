using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.DTOs;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// Background worker for bulk processing. Disposed by the host on shutdown, which cancels
    /// in-flight companies (closing any Playwright browser) and waits briefly for them to stop.
    /// </summary>
    public class JobQueueService : IJobQueueService, IAsyncDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<CrawlOptions> _options;
        private readonly ILogger<JobQueueService> _logger;

        private CancellationTokenSource? _cts;
        private Task? _processingTask;
        private volatile bool _isPaused = false;
        private readonly object _lock = new();

        public bool IsRunning => _processingTask != null && !_processingTask.IsCompleted;
        public bool IsPaused => _isPaused;

        public JobQueueService(IServiceProvider serviceProvider, IOptions<CrawlOptions> options, ILogger<JobQueueService> logger)
        {
            _serviceProvider = serviceProvider;
            _options = options;
            _logger = logger;
        }

        public async Task QueueAllPendingAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var pending = await db.Companies.Where(c => c.Status == CompanyStatus.Pending).ToListAsync();
            _logger.LogInformation("Queued {Count} pending companies for bulk processing", pending.Count);
        }

        public Task StartProcessingAsync()
        {
            lock (_lock)
            {
                if (IsRunning && !_isPaused) return Task.CompletedTask;

                if (_isPaused)
                {
                    _isPaused = false;
                    _logger.LogInformation("Resumed processing worker.");
                    return Task.CompletedTask;
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _isPaused = false;
                _processingTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
                _logger.LogInformation("Bulk processing worker started.");
            }
            return Task.CompletedTask;
        }

        public Task PauseProcessingAsync()
        {
            lock (_lock)
            {
                if (IsRunning)
                {
                    _isPaused = true;
                    _logger.LogInformation("Processing paused by user request.");
                }
            }
            return Task.CompletedTask;
        }

        public Task ResumeProcessingAsync()
        {
            return StartProcessingAsync();
        }

        public async Task RetryFailedAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var failed = await db.Companies
                .Where(c => c.Status == CompanyStatus.Failed || c.Status == CompanyStatus.NeedsReview)
                .ToListAsync();

            foreach (var c in failed)
            {
                c.Status = CompanyStatus.Pending;
                c.FailureReason = null;
            }
            await db.SaveChangesAsync();

            await StartProcessingAsync();
        }

        public async Task ClearAllAsync()
        {
            if (IsRunning)
            {
                await StopWorkerAsync(TimeSpan.FromSeconds(30));
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.InvestorDocuments.RemoveRange(db.InvestorDocuments);
            db.CrawlLogs.RemoveRange(db.CrawlLogs);
            db.Companies.RemoveRange(db.Companies);
            await db.SaveChangesAsync();
        }

        public ProcessingSummaryDto GetSummary()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var companies = db.Companies.AsNoTracking().ToList();
            var docs = db.InvestorDocuments.AsNoTracking().ToList();

            return new ProcessingSummaryDto
            {
                TotalCompanies = companies.Count,
                Completed = companies.Count(c => c.Status == CompanyStatus.Completed),
                Processing = companies.Count(c => c.Status == CompanyStatus.Processing),
                Pending = companies.Count(c => c.Status == CompanyStatus.Pending),
                Partial = companies.Count(c => c.Status == CompanyStatus.Partial),
                Failed = companies.Count(c => c.Status == CompanyStatus.Failed),
                NeedsReview = companies.Count(c => c.Status == CompanyStatus.NeedsReview),

                DocumentsFound = docs.Count,
                DocumentsDownloaded = docs.Count(d => d.Downloaded),
                DuplicatesSkipped = docs.Count(d => d.Status == "SkippedDuplicate"),
                FailedDownloads = docs.Count(d => d.Status == "Failed"),

                IsRunning = IsRunning,
                IsPaused = IsPaused
            };
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            var maxConcurrent = Math.Max(1, _options.Value.MaxConcurrentCompanies);
            // Each company is attempted at most once per run, so one that stays Pending
            // (e.g. its status could not be saved) cannot be picked up in an endless loop.
            var attempted = new HashSet<int>();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_isPaused)
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    List<int> companyIdsToProcess;
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var pendingIds = await db.Companies
                            .Where(c => c.Status == CompanyStatus.Pending)
                            .OrderBy(c => c.Id)
                            .Select(c => c.Id)
                            .ToListAsync(cancellationToken);
                        companyIdsToProcess = pendingIds.Where(id => !attempted.Contains(id)).Take(maxConcurrent).ToList();
                    }

                    if (companyIdsToProcess.Count == 0)
                    {
                        break;
                    }

                    attempted.UnionWith(companyIdsToProcess);
                    await Task.WhenAll(companyIdsToProcess.Select(id => ProcessIsolatedAsync(id, cancellationToken)));
                }

                _logger.LogInformation("Worker loop exited cleanly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker loop cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop stopped unexpectedly.");
            }
        }

        /// <summary>Runs one company in its own scope; its failure is logged and never stops the other companies.</summary>
        private async Task ProcessIsolatedAsync(int companyId, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ICompanyProcessor>();
                await processor.ProcessCompanyAsync(companyId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Processor already returned the company to Pending.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing company {CompanyId}; continuing with remaining companies", companyId);
            }
        }

        private async Task StopWorkerAsync(TimeSpan timeout)
        {
            Task? worker;
            lock (_lock)
            {
                _cts?.Cancel();
                worker = _processingTask;
            }
            if (worker == null) return;

            var finished = await Task.WhenAny(worker, Task.Delay(timeout));
            if (finished != worker)
            {
                _logger.LogWarning("Worker did not stop within {Seconds}s", timeout.TotalSeconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopWorkerAsync(TimeSpan.FromSeconds(30));
            _cts?.Dispose();
        }
    }
}
