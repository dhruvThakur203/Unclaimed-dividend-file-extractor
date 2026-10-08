using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvestorDataAutomation.Services
{
    public class TargetDocumentRelevanceFilter : ITargetDocumentRelevanceFilter
    {
        private readonly IOptions<CrawlOptions> _options;
        private readonly ILogger<TargetDocumentRelevanceFilter> _logger;

        private static readonly (string Phrase, int Score, string? Category)[] ScoringRules =
        {
            ("unclaimed dividend", 100, "unclaimed_dividend"),
            ("unpaid dividend", 100, "unpaid_dividend"),
            ("shares liable to iepf", 100, "iepf_shares"),
            ("shares transferred to iepf", 100, "iepf_shares"),
            ("shares transferred to the iepf", 100, "iepf_shares"),
            ("dividend liable to iepf", 100, "iepf_dividend"),
            ("amount liable to iepf", 100, "iepf_amount"),
            ("due for transfer to iepf", 95, "iepf_transfer"),
            ("proposed transfer to iepf", 95, "iepf_transfer"),
            ("transfer of shares to iepf", 95, "iepf_transfer"),
            ("transfer of shares to the iepf", 95, "iepf_transfer"),
            ("details of unclaimed dividend", 100, "unclaimed_dividend"),
            ("details of unpaid dividend", 100, "unpaid_dividend"),
            ("statement of unclaimed dividend", 100, "unclaimed_dividend"),
            ("statement of unpaid dividend", 100, "unpaid_dividend"),
            ("unclaimed dividend statement", 100, "unclaimed_dividend"),
            ("unpaid dividend statement", 100, "unpaid_dividend"),
            ("unclaimed dividend amount", 95, "unclaimed_dividend"),
            ("unpaid dividend amount", 95, "unpaid_dividend"),
            ("year wise unclaimed dividend", 95, "unclaimed_dividend"),
            ("year wise unpaid dividend", 95, "unpaid_dividend"),
            ("dividend remaining unpaid", 90, "unpaid_dividend"),
            ("dividend remaining unclaimed", 90, "unclaimed_dividend"),
            ("unpaid dividend as on", 90, "unpaid_dividend"),
            ("unclaimed dividend as on", 90, "unclaimed_dividend"),
            ("folio wise unclaimed dividend", 95, "folio_unclaimed"),
            ("shareholder wise unclaimed", 90, "shareholder_unclaimed"),
            ("shareholder wise unclaimed dividend", 95, "shareholder_unclaimed"),
            ("folio wise unclaimed", 85, "folio_unclaimed"),
            ("investor wise unclaimed", 85, "shareholder_unclaimed"),
            ("investor wise dividend", 75, "shareholder_unclaimed"),
            ("shareholder wise dividend", 80, "shareholder_unclaimed"),
            ("folio wise dividend", 75, "folio_unclaimed"),
            ("unclaimed shares", 90, "unclaimed_shares"),
            ("unclaimed equity shares", 90, "unclaimed_shares"),
            ("unclaimed physical shares", 90, "unclaimed_shares"),
            ("unclaimed share suspense", 85, "unclaimed_shares"),
            ("shares due for transfer", 85, "iepf_shares"),
            ("shares liable for transfer", 85, "iepf_shares"),
            ("shares liable to be transferred", 85, "iepf_shares"),
            ("shares proposed to be transferred", 85, "iepf_shares"),
            ("iepf transfer", 90, "iepf_transfer"),
            ("iepf authority transfer", 95, "iepf_transfer"),
            ("claim from iepf", 90, "iepf_claim"),
            ("claim of shares from iepf", 95, "iepf_claim"),
            ("refund from iepf", 90, "iepf_claim"),
            ("iepf claim form", 90, "iepf_claim"),
            ("iepf claim", 85, "iepf_claim"),
            ("iepf statement", 90, "iepf_doc"),
            ("iepf details", 85, "iepf_doc"),
            ("iepf list", 85, "iepf_doc"),
            ("iepf shares", 90, "iepf_shares"),
            ("iepf form", 80, "iepf_doc"),
            ("iepf authority", 85, "iepf_doc"),
            ("unclaimed amount", 80, "unclaimed_amount"),
            ("unpaid amount", 80, "unpaid_amount"),
            ("dividend outstanding", 60, "dividend_status"),
            ("dividend pending", 50, "dividend_status"),
            ("demat suspense account", 80, "suspense"),
            ("demat suspense", 75, "suspense"),
            ("suspense account", 70, "suspense"),
            ("returned dividend", 75, "returned_dividend"),
            ("dividend returned", 75, "returned_dividend"),
            ("undelivered share certificates", 80, "undelivered"),
            ("undelivered shares", 75, "undelivered"),
            ("iepf 5", 85, "iepf_form"),
            ("iepf 1", 75, "iepf_form"),
            ("iepf 2", 75, "iepf_form"),
            ("iepf 4", 75, "iepf_form"),
        };

        private static readonly (string Phrase, int Score, string Category)[] ModerateRules =
        {
            ("unclaimed", 60, "token_unclaimed"),
            ("unpaid", 60, "token_unpaid"),
            ("iepf", 100, "token_iepf"),
            ("shareholder wise", 80, "shareholder_wise"),
            ("folio wise", 75, "folio_wise"),
            ("shareholder", 20, "token_shareholder"),
            ("shareholders", 20, "token_shareholder"),
            ("folio", 20, "token_folio"),
            ("dividend", 10, "token_dividend"),
        };

        private static readonly string[] ExclusionPhrases =
        {
            "annual report", "investor presentation", "financial results", "earnings presentation",
            "corporate governance", "dividend policy", "dividend declaration", "dividend history",
            "balance sheet", "company profile", "board meeting", "quarterly results"
        };

        public TargetDocumentRelevanceFilter(IOptions<CrawlOptions> options, ILogger<TargetDocumentRelevanceFilter> logger)
        {
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Scores the document's own evidence (URL, file name, anchor, link title, local link context).
        /// The page title is page-level: it can raise a document that already has partial evidence
        /// (≥ the possible-candidate minimum) but can never qualify a document on its own, so
        /// unrelated PDFs on an "Unclaimed Dividend" page are not downloaded.
        /// </summary>
        public TargetDocumentFilterResult Evaluate(TargetDocumentCandidate candidate)
        {
            var threshold = _options.Value.TargetDocumentScoreThreshold;
            var possibleMin = _options.Value.TargetDocumentPossibleCandidateMinScore;

            var (score, reasons) = ScoreCorpus(Normalize(BuildDocumentCorpus(candidate)));

            if (!string.IsNullOrWhiteSpace(candidate.PageTitle))
            {
                var (withPageScore, withPageReasons) = ScoreCorpus(Normalize($"{BuildDocumentCorpus(candidate)} {candidate.PageTitle}"));
                if (score >= possibleMin && withPageScore > score)
                {
                    score = withPageScore;
                    reasons = withPageReasons;
                    reasons.Add("page title support");
                }
                else if (score < possibleMin && withPageScore >= threshold)
                {
                    reasons.Add("page-level signal only (ignored)");
                }
            }

            TargetDocumentFilterAction action;
            string actionLabel;
            if (score >= threshold)
            {
                action = TargetDocumentFilterAction.Download;
                actionLabel = "DOWNLOAD";
            }
            else if (score >= possibleMin)
            {
                action = TargetDocumentFilterAction.PossibleCandidate;
                actionLabel = "SKIP / POSSIBLE";
            }
            else
            {
                action = TargetDocumentFilterAction.Ignore;
                actionLabel = "SKIP";
            }

            var reasonText = reasons.Count > 0 ? string.Join(" + ", reasons) : "no targeted IEPF/unclaimed signal";
            var displayFile = string.IsNullOrWhiteSpace(candidate.FileName) ? UrlNormalizer.Redact(candidate.DocumentUrl) : candidate.FileName;

            _logger.Log(
                action == TargetDocumentFilterAction.Ignore ? LogLevel.Debug : LogLevel.Information,
                "[TARGET FILTER] File: {File} Score: {Score} Reason: {Reason} Action: {Action}",
                displayFile, score, reasonText, actionLabel);

            return new TargetDocumentFilterResult(score, reasonText, action);
        }

        private static (int Score, List<string> Reasons) ScoreCorpus(string normalized)
        {
            var score = 0;
            var reasons = new List<string>();
            var matchedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in ScoringRules.OrderByDescending(r => r.Phrase.Length))
            {
                if (!ContainsPhrase(normalized, rule.Phrase)) continue;
                if (rule.Category != null && matchedCategories.Contains(rule.Category)) continue;

                score += rule.Score;
                reasons.Add(rule.Phrase);
                if (rule.Category != null) matchedCategories.Add(rule.Category);
            }

            if (ContainsBoth(normalized, "unclaimed", "dividend") && !matchedCategories.Contains("unclaimed_dividend"))
            {
                score += 100;
                reasons.Add("unclaimed+dividend");
                matchedCategories.Add("unclaimed_dividend");
            }

            if (ContainsBoth(normalized, "unpaid", "dividend") && !matchedCategories.Contains("unpaid_dividend"))
            {
                score += 100;
                reasons.Add("unpaid+dividend");
                matchedCategories.Add("unpaid_dividend");
            }

            if (ContainsBoth(normalized, "unclaimed", "shares") && !matchedCategories.Contains("unclaimed_shares"))
            {
                score += 90;
                reasons.Add("unclaimed+shares");
                matchedCategories.Add("unclaimed_shares");
            }

            foreach (var rule in ModerateRules.OrderByDescending(r => r.Phrase.Length))
            {
                if (!ContainsPhrase(normalized, rule.Phrase)) continue;
                if (matchedCategories.Contains(rule.Category)) continue;

                score += rule.Score;
                reasons.Add(rule.Phrase);
                matchedCategories.Add(rule.Category);
            }

            if (HasTargetSignal(score, matchedCategories) && HasYearEvidence(normalized))
            {
                score += 5;
                reasons.Add("financial year");
            }

            if (HasTargetSignal(score, matchedCategories) && HasSevenYearEvidence(normalized))
            {
                score += 10;
                reasons.Add("seven year transfer context");
            }

            foreach (var exclusion in ExclusionPhrases)
            {
                if (!ContainsPhrase(normalized, exclusion)) continue;
                if (score < 80)
                {
                    score = 0;
                    reasons.Clear();
                    reasons.Add($"excluded: {exclusion}");
                    break;
                }
            }

            if (ContainsPhrase(normalized, "physical shares") && !matchedCategories.Contains("unclaimed_shares")
                && !ContainsPhrase(normalized, "unclaimed physical shares"))
            {
                score = Math.Max(0, score - 30);
                reasons.Add("generic physical shares (penalty)");
            }

            if (ContainsPhrase(normalized, "dividend warrants") && !matchedCategories.Contains("returned_dividend"))
            {
                score = Math.Max(0, score - 20);
                reasons.Add("dividend warrants without returned context (penalty)");
            }

            return (score, reasons);
        }

        private static bool HasTargetSignal(int score, HashSet<string> categories)
        {
            if (score >= 50) return true;
            return categories.Any(c => c.StartsWith("unclaimed", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith("unpaid", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith("iepf", StringComparison.OrdinalIgnoreCase)
                || c.Contains("folio", StringComparison.OrdinalIgnoreCase)
                || c.Contains("shareholder", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildDocumentCorpus(TargetDocumentCandidate candidate)
        {
            return string.Join(" ",
                DecodeUrl(candidate.DocumentUrl),
                candidate.FileName,
                candidate.AnchorText,
                candidate.LinkTitle ?? "",
                candidate.SurroundingContext ?? "");
        }

        private static string DecodeUrl(string url)
        {
            try { return Uri.UnescapeDataString(url ?? ""); }
            catch (UriFormatException) { return url ?? ""; }
        }

        internal static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var s = Regex.Replace(input, @"([a-z])([A-Z])", "$1 $2");
            s = s.ToLowerInvariant();
            s = s.Replace('_', ' ').Replace('-', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        internal static bool ContainsPhrase(string normalizedCorpus, string phrase)
        {
            if (string.IsNullOrEmpty(normalizedCorpus) || string.IsNullOrEmpty(phrase)) return false;
            return normalizedCorpus.Contains(Normalize(phrase), StringComparison.Ordinal);
        }

        private static bool ContainsBoth(string normalizedCorpus, string a, string b)
        {
            return ContainsPhrase(normalizedCorpus, a) && ContainsPhrase(normalizedCorpus, b);
        }

        private static bool HasYearEvidence(string normalized)
        {
            if (Regex.IsMatch(normalized, @"\bfy\s*20[12]\d\b")) return true;
            if (Regex.IsMatch(normalized, @"\bfy\s*[12]\d\b")) return true;
            if (Regex.IsMatch(normalized, @"\b20[12]\d\s*[\-/]\s*(20)?\d{2}\b")) return true;
            if (Regex.IsMatch(normalized, @"\b20[12]\d\b")) return true;
            return false;
        }

        private static bool HasSevenYearEvidence(string normalized)
        {
            return Regex.IsMatch(normalized, @"\bseven\s+years?\b")
                || Regex.IsMatch(normalized, @"\b7\s*[\-]?\s*years?\b")
                || Regex.IsMatch(normalized, @"\bseven\s*[\-]?\s*year\b");
        }
    }
}
