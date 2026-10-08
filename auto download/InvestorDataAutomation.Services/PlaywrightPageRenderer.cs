using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace InvestorDataAutomation.Services
{
    internal sealed class RenderedLink
    {
        [JsonPropertyName("href")] public string? Href { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("context")] public string? Context { get; set; }
    }

    internal sealed record RenderedPage(string Url, string Title, IReadOnlyList<RenderedLink> Links);

    internal sealed record RenderBatchResult(IReadOnlyList<RenderedPage> Pages, int BrowserLaunches);

    /// <summary>Renders pages whose document links are built client-side and returns their links.</summary>
    internal interface IDynamicPageRenderer
    {
        Task<RenderBatchResult> RenderAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One headless browser per batch; each page gets its own context which is always disposed.
    /// A process-wide semaphore caps concurrent browsers across companies, and cancellation closes
    /// the browser immediately so no navigation or wait outlives the job.
    /// </summary>
    internal sealed class PlaywrightPageRenderer : IDynamicPageRenderer
    {
        private const float NavigationTimeoutMs = 30_000;
        private const float NetworkIdleTimeoutMs = 15_000;
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
        private static readonly string[] FallbackChannels = { "msedge", "chrome" };

        private static readonly object SlotsLock = new();
        private static SemaphoreSlim? _browserSlots;

        // Container text is only used when it is short and holds no page heading; long containers
        // (whole sections, navigation menus) would attach page-level keywords to unrelated documents.
        // The accordion header is kept: it labels exactly the documents inside that section.
        private const string ExtractLinksScript = @"els => els.map(a => {
            const container = a.closest('li, tr, .views-row, .pdf-list, .accordion-section-content, section, div');
            const section = a.closest('.accordion-section-content, [id^=""accordion""], details, .tab-pane');
            const header = section && section.previousElementSibling ? section.previousElementSibling.textContent : '';
            const containerText = (container && !container.querySelector('h1, h2') ? container.textContent || '' : '').replace(/\s+/g, ' ').trim();
            const context = ((header || '') + ' ' + (containerText.length <= 300 ? containerText : '')).replace(/\s+/g, ' ').trim();
            const heading = a.querySelector('h1, h2, h3, h4, h5, h6, .title');
            return {
                href: a.href || a.getAttribute('href') || '',
                text: ((heading ? heading.textContent : a.textContent) || '').replace(/\s+/g, ' ').trim(),
                title: a.getAttribute('title') || '',
                context: context.slice(0, 500)
            };
        })";

        private readonly CrawlOptions _options;
        private readonly ILogger _logger;

        public PlaywrightPageRenderer(CrawlOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        public async Task<RenderBatchResult> RenderAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
        {
            var pages = new List<RenderedPage>();
            if (urls.Count == 0) return new RenderBatchResult(pages, 0);

            var slots = GetBrowserSlots(_options.MaxConcurrentBrowsers);
            await slots.WaitAsync(cancellationToken);
            var launches = 0;
            try
            {
                using var playwright = await Playwright.CreateAsync();
                var browser = await LaunchBrowserAsync(playwright);
                launches++;
                await using (browser)
                {
                    using var closeOnCancel = cancellationToken.Register(() => _ = CloseQuietlyAsync(browser));
                    foreach (var url in urls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!browser.IsConnected)
                        {
                            _logger.LogWarning("Playwright browser disconnected; skipping remaining {Count} page(s)", urls.Count - pages.Count);
                            break;
                        }

                        var rendered = await RenderPageAsync(browser, url, cancellationToken);
                        if (rendered != null) pages.Add(rendered);
                    }
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Playwright browser automation unavailable or failed: {Error}", ex.Message);
            }
            finally
            {
                slots.Release();
            }

            return new RenderBatchResult(pages, launches);
        }

        private async Task<RenderedPage?> RenderPageAsync(IBrowser browser, string url, CancellationToken cancellationToken)
        {
            try
            {
                await using var context = await browser.NewContextAsync();
                var page = await context.NewPageAsync();

                _logger.LogInformation("Playwright opening {Url}", UrlNormalizer.Redact(url));
                await page.GotoAsync(url, new PageGotoOptions { Timeout = NavigationTimeoutMs, WaitUntil = WaitUntilState.Load });

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = NetworkIdleTimeoutMs });
                }
                catch (TimeoutException)
                {
                    // Analytics beacons can keep the network busy; rendered content is usually ready by now.
                }
                await Task.Delay(SettleDelay, cancellationToken);

                var title = await page.TitleAsync() ?? "";
                var links = await page.EvalOnSelectorAllAsync<RenderedLink[]>("a[href]", ExtractLinksScript);
                return new RenderedPage(url, title, links ?? Array.Empty<RenderedLink>());
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogWarning("Playwright failed to render {Url}: {Error}", UrlNormalizer.Redact(url), ex.Message);
                return null;
            }
        }

        /// <summary>Bundled Chromium first, then installed Edge, then Chrome — three attempts at most, no path probing.</summary>
        private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            }
            catch (PlaywrightException bundledError)
            {
                _logger.LogWarning("Bundled Chromium unavailable ({Error}); trying installed browser channels", FirstLine(bundledError.Message));
                foreach (var channel in FallbackChannels)
                {
                    try
                    {
                        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = channel });
                        _logger.LogInformation("Playwright using installed {Channel} browser", channel);
                        return browser;
                    }
                    catch (PlaywrightException channelError)
                    {
                        _logger.LogWarning("Browser channel {Channel} unavailable: {Error}", channel, FirstLine(channelError.Message));
                    }
                }
                throw new InvalidOperationException(
                    "No browser available for Playwright (bundled Chromium, msedge and chrome all failed). Run 'playwright install chromium'.",
                    bundledError);
            }
        }

        private static async Task CloseQuietlyAsync(IBrowser browser)
        {
            try { await browser.CloseAsync(); }
            catch (PlaywrightException) { }
        }

        private static SemaphoreSlim GetBrowserSlots(int configured)
        {
            lock (SlotsLock)
            {
                return _browserSlots ??= new SemaphoreSlim(Math.Max(1, configured));
            }
        }

        private static string FirstLine(string message)
        {
            var newline = message.IndexOf('\n');
            return newline < 0 ? message : message[..newline].TrimEnd();
        }
    }
}
