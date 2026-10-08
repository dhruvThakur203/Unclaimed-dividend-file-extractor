namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// Shared investor-route keyword lists (homepage discovery vs crawl follow).
    /// </summary>
    internal static class InvestorRouteKeywordSets
    {
        public static readonly string[] HomepageLinkKeywords =
        {
            "investor", "investors", "investor relations", "investor-relations", "investorrelations",
            "financial results", "financial-results", "annual report", "annual-report", "quarterly results",
            "financials", "earnings", "shareholders", "shareholder", "corporate governance",
            "investor presentation", "reports", "disclosures", "financial-information",
            "stock-exchange-disclosures",
            "shareholder information", "shareholder-information", "shareholder info", "shareholder-info",
            "shareholder services", "shareholder-services", "shareholder corner", "shareholder-structure",
            "shareholder structure",
            "stock information", "stock-information", "stock info", "stock-info", "stock price", "stock-price",
            "stock quote", "stock-quote", "stock performance", "stock-performance", "stock charts", "stock-charts",
            "historical stock price", "historical-stock-price",
            "share price", "share-price", "share data", "share-data", "shareholding", "shareholding pattern",
            "shareholding-pattern",
            "dividend", "dividends", "dividend history", "dividend-history", "dividend payout", "dividend-payout",
            "unclaimed dividend", "unclaimed-dividends",
            "transfer agent", "transfer-agent", "registrar", "registrars", "rtas",
            "agm", "annual general meeting", "annual-general-meeting", "egm",
            "extraordinary general meeting", "extraordinary-general-meeting", "shareholder meetings",
            "shareholder-meetings", "postal ballot", "postal-ballot",
            "investor faq", "investor-faq", "investor faqs", "investor-faqs", "contact ir", "contact-ir",
            "investor contacts", "investor-contacts",
            "corporate access", "corporate-access", "investor hub", "investor-hub", "investor portal",
            "investor-portal"
        };

        public static readonly string[] CrawlFollowKeywords =
        {
            "investor", "investor-relations", "investorrelations", "investor-hub", "investor-portal",
            "investor-faqs", "investor-contacts", "contact-ir", "corporate-access",
            "investor-information", "financial-information", "share-information",
            "shareholder", "shareholders", "shareholder-information", "shareholder-info",
            "shareholder-services", "shareholder-structure", "shareholder-meetings",
            "shareholding", "shareholding-pattern",
            "stock-information", "stock-info", "stock-price", "stock-quote", "stock-performance",
            "stock-charts", "historical-stock-price",
            "share-price", "share-data",
            "dividend", "dividends", "dividend-history", "dividend-payout", "unclaimed-dividend", "unclaimed-dividends",
            "unclaimed-shares", "iepf",
            "transfer-agent", "registrars", "rtas",
            "annual-general-meeting", "extraordinary-general-meeting", "postal-ballot", "agm", "egm",
            "report", "financial", "result", "quarter", "annual", "presentation", "governance", "statement"
        };

        public const int StrongRouteWeight = 40;
        public const int ModerateRouteWeight = 15;
        public const int WeakRouteWeight = 3;

        /// <summary>
        /// Tiered route signals used to order the crawl. Phrases are space-separated and matched
        /// on word boundaries against a normalized URL path / anchor (hyphens, underscores,
        /// slashes and URL encoding collapse to spaces), so overlapping phrases accumulate.
        /// </summary>
        public static readonly (string Phrase, int Weight)[] RouteSignalWeights =
        {
            ("investor", StrongRouteWeight), ("investors", StrongRouteWeight),
            ("investor relations", StrongRouteWeight), ("investorrelations", StrongRouteWeight),
            ("investor information", StrongRouteWeight), ("investor reports", StrongRouteWeight),
            ("shareholder", StrongRouteWeight), ("shareholders", StrongRouteWeight),
            ("shareholder information", StrongRouteWeight), ("shareholder services", StrongRouteWeight),
            ("unclaimed", StrongRouteWeight), ("unpaid", StrongRouteWeight), ("iepf", StrongRouteWeight),
            ("unclaimed dividend", StrongRouteWeight), ("unclaimed dividends", StrongRouteWeight),
            ("unpaid dividend", StrongRouteWeight), ("unclaimed shares", StrongRouteWeight),
            ("shareholding", StrongRouteWeight), ("statutory filings", StrongRouteWeight),

            ("dividend", ModerateRouteWeight), ("dividends", ModerateRouteWeight),
            ("share", ModerateRouteWeight), ("shares", ModerateRouteWeight), ("stock", ModerateRouteWeight),
            ("agm", ModerateRouteWeight), ("egm", ModerateRouteWeight), ("notices", ModerateRouteWeight),
            ("filings", ModerateRouteWeight), ("financial information", ModerateRouteWeight),
            ("financial results", ModerateRouteWeight), ("annual report", ModerateRouteWeight),
            ("annual reports", ModerateRouteWeight), ("disclosures", ModerateRouteWeight),
            ("corporate governance", ModerateRouteWeight), ("postal ballot", ModerateRouteWeight),
            ("registrar", ModerateRouteWeight), ("transfer agent", ModerateRouteWeight),

            ("report", WeakRouteWeight), ("reports", WeakRouteWeight), ("statement", WeakRouteWeight),
            ("statements", WeakRouteWeight), ("information", WeakRouteWeight), ("downloads", WeakRouteWeight),
            ("financial", WeakRouteWeight), ("results", WeakRouteWeight)
        };

        /// <summary>Routes that usually host IEPF/unclaimed documents, which are often rendered dynamically.</summary>
        public static readonly string[] TargetDocumentRouteSignals = { "iepf", "unclaimed", "unpaid" };

        /// <summary>Final path segments that identify an investor landing page.</summary>
        public static readonly string[] InvestorHubSegments =
        {
            "investor", "investors", "investor relations", "investorrelations", "ir",
            "shareholder", "shareholders", "shareholder information", "investor information", "investor centre",
            "investor center", "investor hub", "investor portal"
        };
    }
}
