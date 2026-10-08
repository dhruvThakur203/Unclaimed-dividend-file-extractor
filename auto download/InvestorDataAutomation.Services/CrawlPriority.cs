using System;
using System.Collections.Generic;
using InvestorDataAutomation.Core.Interfaces;

namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// Transparent crawl-ordering score: route signals + source adjustment + target-route bonus
    /// + parent context - depth. Higher is crawled first. Only ordering is affected; whether a
    /// link is followed at all is still decided by <see cref="InvestorRouteRelevance.ShouldFollowCrawlLink"/>.
    /// </summary>
    internal static class CrawlPriority
    {
        public const int HomepageRootPriority = 10_000;
        public const int HomepageLinkBonus = 40;
        public const int CrawledLinkBonus = 20;
        public const int StandardPathPenalty = -60;
        public const int TargetRouteBonus = 30;
        public const int DepthPenalty = 10;
        public const int MaxChildContextBonus = 40;
        public const int MaxLinkedFromInvestorPageBonus = 20;

        public static InvestorPageCandidate ForSeed(string url, InvestorPageSource source, string? anchorText = null, string? linkTitle = null)
        {
            if (source == InvestorPageSource.Homepage)
            {
                return new InvestorPageCandidate(url, source, HomepageRootPriority, "website root (crawled first to discover linked routes)");
            }

            var reasons = new List<string>();
            var priority = ScoreRoute(url, anchorText, linkTitle, reasons);
            priority += SourceAdjustment(source, reasons);
            return new InvestorPageCandidate(url, source, priority, string.Join("; ", reasons), AnchorText: anchorText);
        }

        public static InvestorPageCandidate ForChild(InvestorPageCandidate parent, string url, string? anchorText, string? linkTitle, int depth)
        {
            var source = parent.Source == InvestorPageSource.Homepage ? InvestorPageSource.HomepageLink : InvestorPageSource.CrawledLink;

            var reasons = new List<string>();
            var priority = ScoreRoute(url, anchorText, linkTitle, reasons);
            priority += SourceAdjustment(source, reasons);
            priority += ParentContext(parent, url, reasons);

            if (depth > 1)
            {
                var penalty = (depth - 1) * DepthPenalty;
                priority -= penalty;
                reasons.Add($"depth {depth} -{penalty}");
            }

            return new InvestorPageCandidate(url, source, priority, string.Join("; ", reasons), parent.Url, anchorText, depth);
        }

        private static int ScoreRoute(string url, string? anchorText, string? linkTitle, List<string> reasons)
        {
            var route = InvestorRouteRelevance.ScoreRouteSignals(url, anchorText, linkTitle);
            reasons.Add($"route {route.Score} [{string.Join(", ", route.Matches)}]");
            var score = route.Score;

            if (InvestorRouteRelevance.HasTargetDocumentRouteSignal(url, anchorText))
            {
                score += TargetRouteBonus;
                reasons.Add($"IEPF/unclaimed route +{TargetRouteBonus}");
            }
            return score;
        }

        private static int SourceAdjustment(InvestorPageSource source, List<string> reasons)
        {
            var adjustment = source switch
            {
                InvestorPageSource.HomepageLink => HomepageLinkBonus,
                InvestorPageSource.CrawledLink => CrawledLinkBonus,
                InvestorPageSource.StandardPath => StandardPathPenalty,
                _ => 0
            };
            if (adjustment != 0) reasons.Add($"{source} {(adjustment > 0 ? "+" : "")}{adjustment}");
            return adjustment;
        }

        private static int ParentContext(InvestorPageCandidate parent, string childUrl, List<string> reasons)
        {
            if (parent.Source == InvestorPageSource.Homepage) return 0;

            var parentRoute = InvestorRouteRelevance.ScoreRouteSignals(parent.Url, parent.AnchorText);
            if (!parentRoute.IsStrong) return 0;

            if (InvestorRouteRelevance.IsStrictChildPath(parent.Url, childUrl))
            {
                var bonus = Math.Min(parentRoute.Score / 2, MaxChildContextBonus);
                reasons.Add($"child of investor page +{bonus}");
                return bonus;
            }

            var linkedBonus = Math.Min(parentRoute.Score / 4, MaxLinkedFromInvestorPageBonus);
            reasons.Add($"linked from investor page +{linkedBonus}");
            return linkedBonus;
        }
    }
}
