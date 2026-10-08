using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace InvestorDataAutomation.Services
{
    internal sealed record DynamicPageAssessment(bool RequiresRendering, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Decides whether an HTTP-fetched page builds its document links client-side
    /// and is relevant enough to IEPF/unclaimed content to justify a browser render.
    /// </summary>
    internal static class DynamicPageAssessor
    {
        private static readonly Regex FrameworkBindingRegex = new(
            @"\s(?::href|v-bind:href|v-for|ng-repeat|ng-href|\[href\])\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DynamicContainerRegex = new(
            @"\sid\s*=\s*[""']vue-[^""']*[""']|\sdata-reporttype\s*=|\sng-app\b|\sdata-reactroot\b|\sid\s*=\s*[""']__next[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] TargetSignals = { "iepf", "unclaimed", "unpaid" };

        public static DynamicPageAssessment Assess(string pageUrl, string html, HtmlDocument doc)
        {
            var reasons = new List<string>();

            var hasBinding = FrameworkBindingRegex.IsMatch(html);
            if (hasBinding) reasons.Add("Vue binding detected");

            var hasContainer = DynamicContainerRegex.IsMatch(html);
            if (hasContainer) reasons.Add("Dynamic report container detected");

            // Navigation menus on every page link to "Unclaimed Dividend", so only the
            // page's own identity (URL, title, headings, report type) counts as a signal.
            var hasTargetSignal = HasTargetSignal(BuildPageIdentity(pageUrl, doc));
            if (hasTargetSignal) reasons.Add("IEPF/unclaimed signals detected");

            var requiresRendering = (hasBinding || hasContainer) && hasTargetSignal;
            return new DynamicPageAssessment(requiresRendering, reasons);
        }

        private static string BuildPageIdentity(string pageUrl, HtmlDocument doc)
        {
            var parts = new List<string> { Uri.UnescapeDataString(pageUrl) };

            var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
            if (!string.IsNullOrWhiteSpace(title)) parts.Add(title);

            var description = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(description)) parts.Add(description);

            var headings = doc.DocumentNode.SelectNodes("//h1");
            if (headings != null) parts.AddRange(headings.Select(h => h.InnerText));

            var reportTypes = doc.DocumentNode.SelectNodes("//*[@data-reporttype]");
            if (reportTypes != null) parts.AddRange(reportTypes.Select(n => n.GetAttributeValue("data-reporttype", "")));

            return string.Join(" ", parts).ToLowerInvariant();
        }

        private static bool HasTargetSignal(string identity)
        {
            return TargetSignals.Any(s => identity.Contains(s, StringComparison.Ordinal));
        }
    }
}
