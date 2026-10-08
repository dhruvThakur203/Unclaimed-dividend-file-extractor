using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InvestorDataAutomation.Services
{
    internal static class InvestorRouteRelevance
    {
        public const int CrawlFollowMinScore = 35;
        public const int InvestorHubMinScore = 40;

        public static int ScoreHomepageLink(string url, string anchorText, string linkTitle)
        {
            return ScoreRoute(url, anchorText, linkTitle, parentPageUrl: null, InvestorRouteKeywordSets.HomepageLinkKeywords,
                anchorWeight: 10, titleWeight: 5, urlWeight: 8);
        }

        public static int ScoreCrawlRoute(string url, string anchorText, string linkTitle, string? parentPageUrl)
        {
            return ScoreRoute(url, anchorText, linkTitle, parentPageUrl, InvestorRouteKeywordSets.CrawlFollowKeywords,
                anchorWeight: 10, titleWeight: 5, urlWeight: 8);
        }

        public static bool ShouldFollowCrawlLink(string parentPageUrl, string targetUrl, string anchorText, string linkTitle)
        {
            var score = ScoreCrawlRoute(targetUrl, anchorText, linkTitle, parentPageUrl);
            if (score >= CrawlFollowMinScore)
            {
                return true;
            }

            if (IsInvestorHub(parentPageUrl) && IsStrictChildPath(parentPageUrl, targetUrl))
            {
                var childPathScore = ScorePathHierarchy(targetUrl, InvestorRouteKeywordSets.CrawlFollowKeywords);
                return childPathScore >= 15 || score >= 20;
            }

            return false;
        }

        public static bool IsInvestorHub(string pageUrl)
        {
            return ScorePathHierarchy(pageUrl, InvestorRouteKeywordSets.CrawlFollowKeywords) >= InvestorHubMinScore;
        }

        internal static int ScoreRoute(
            string url,
            string anchorText,
            string linkTitle,
            string? parentPageUrl,
            string[] keywords,
            int anchorWeight,
            int titleWeight,
            int urlWeight)
        {
            var score = 0;
            var lowerUrl = url.ToLowerInvariant();
            var lowerAnchor = anchorText.ToLowerInvariant();
            var lowerTitle = linkTitle.ToLowerInvariant();

            foreach (var kw in keywords)
            {
                if (lowerAnchor.Contains(kw)) score += anchorWeight;
                if (lowerTitle.Contains(kw)) score += titleWeight;
                if (lowerUrl.Contains(kw)) score += urlWeight;
            }

            score += ScorePathHierarchy(url, keywords);

            if (!string.IsNullOrWhiteSpace(parentPageUrl)
                && IsStrictChildPath(parentPageUrl, url))
            {
                score += ScorePathHierarchy(parentPageUrl, keywords) / 2;
                score += CountMatchingSegments(parentPageUrl, keywords) * 5;
            }

            return score;
        }

        internal static int ScorePathHierarchy(string url, string[] keywords)
        {
            var segments = GetPathSegments(url);
            if (segments.Count == 0) return 0;

            var score = 0;
            var matchingSegments = 0;

            foreach (var segment in segments)
            {
                var segmentScore = ScoreSegment(segment, keywords);
                if (segmentScore > 0)
                {
                    matchingSegments++;
                    score += segmentScore;
                }
            }

            if (matchingSegments >= 2)
            {
                score += (matchingSegments - 1) * 12;
            }

            if (matchingSegments >= 3)
            {
                score += 20;
            }

            var combinedPath = string.Join(" ", segments);
            foreach (var kw in keywords.OrderByDescending(k => k.Length))
            {
                var normalizedKeyword = NormalizeToken(kw);
                if (combinedPath.Contains(normalizedKeyword, StringComparison.Ordinal))
                {
                    score += 10;
                }
            }

            return score;
        }

        internal static int ScoreSegment(string segment, string[] keywords)
        {
            var normalizedSegment = NormalizeToken(segment);
            if (string.IsNullOrWhiteSpace(normalizedSegment)) return 0;

            var score = 0;
            foreach (var kw in keywords.OrderByDescending(k => k.Length))
            {
                var normalizedKeyword = NormalizeToken(kw);
                if (normalizedSegment.Equals(normalizedKeyword, StringComparison.Ordinal)
                    || normalizedSegment.Contains(normalizedKeyword, StringComparison.Ordinal)
                    || (normalizedKeyword.Contains(normalizedSegment, StringComparison.Ordinal)
                        && normalizedSegment.Length >= 8))
                {
                    score += 18;
                }
            }

            return score;
        }

        internal static List<string> GetPathSegments(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        internal static int CountMatchingSegments(string url, string[] keywords)
        {
            return GetPathSegments(url).Count(seg => ScoreSegment(seg, keywords) > 0);
        }

        internal static bool IsStrictChildPath(string parentPageUrl, string childUrl)
        {
            try
            {
                var parentPath = new Uri(parentPageUrl).AbsolutePath.TrimEnd('/');
                var childPath = new Uri(childUrl).AbsolutePath.TrimEnd('/');
                if (childPath.Length <= parentPath.Length) return false;
                if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)) return false;
                return childPath[parentPath.Length] == '/';
            }
            catch
            {
                return false;
            }
        }

        internal static string NormalizeToken(string value)
        {
            return value.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Trim();
        }

        private static readonly (Regex Pattern, string Phrase, int Weight)[] RouteSignalPatterns =
            InvestorRouteKeywordSets.RouteSignalWeights
                .Select(s => (new Regex($@"\b{Regex.Escape(s.Phrase)}\b", RegexOptions.Compiled), s.Phrase, s.Weight))
                .ToArray();

        /// <summary>
        /// Tiered route score over the whole normalized path plus link text. Signals accumulate
        /// across segments; anchor/title signals count at half weight so URLs dominate.
        /// </summary>
        public static RouteSignalScore ScoreRouteSignals(string url, string? anchorText = null, string? linkTitle = null)
        {
            var matches = new List<string>();
            var pathText = NormalizeRouteText(GetPathText(url));
            var score = ScoreSignalText(pathText, matches, "path");

            var linkText = NormalizeRouteText($"{anchorText} {linkTitle}");
            score += ScoreSignalText(linkText, matches, "link") / 2;

            var strongSegments = GetPathSegments(url)
                .Count(seg => ScoreSignalText(NormalizeRouteText(seg), null, null) >= InvestorRouteKeywordSets.ModerateRouteWeight);
            if (strongSegments >= 2)
            {
                var bonus = (strongSegments - 1) * 10;
                score += bonus;
                matches.Add($"multi-segment +{bonus}");
            }

            return new RouteSignalScore(score, matches);
        }

        public static bool HasTargetDocumentRouteSignal(string url, string? anchorText = null)
        {
            var text = $" {NormalizeRouteText(GetPathText(url))} {NormalizeRouteText(anchorText ?? "")} ";
            return InvestorRouteKeywordSets.TargetDocumentRouteSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
        }

        public static bool IsInvestorHubPath(string url)
        {
            var segments = GetPathSegments(url);
            if (segments.Count == 0) return false;
            var last = NormalizeRouteText(segments[^1]);
            return InvestorRouteKeywordSets.InvestorHubSegments.Contains(last, StringComparer.Ordinal);
        }

        internal static string NormalizeRouteText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string decoded;
            try { decoded = Uri.UnescapeDataString(value); }
            catch { decoded = value; }
            decoded = Regex.Replace(decoded.ToLowerInvariant(), @"[\-_/\\.+%&,:;()]+", " ");
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static string GetPathText(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        }

        private static int ScoreSignalText(string normalizedText, List<string>? matches, string? label)
        {
            if (string.IsNullOrEmpty(normalizedText)) return 0;
            var score = 0;
            foreach (var (pattern, phrase, weight) in RouteSignalPatterns)
            {
                if (!pattern.IsMatch(normalizedText)) continue;
                score += weight;
                matches?.Add($"{label}:{phrase}");
            }
            return score;
        }
    }

    internal sealed record RouteSignalScore(int Score, IReadOnlyList<string> Matches)
    {
        public bool IsStrong => Score >= InvestorRouteKeywordSets.StrongRouteWeight;
    }
}
