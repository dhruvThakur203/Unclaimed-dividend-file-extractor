using System;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;

namespace InvestorDataAutomation.Services
{
    public class DocumentClassifier : IDocumentClassifier
    {
        public DocumentType Classify(string filename, string url, string anchorText, string pageTitle)
        {
            var combined = $"{filename} {url} {anchorText} {pageTitle}".ToLowerInvariant();

            // 1. Annual Report
            if (combined.Contains("annual report") || combined.Contains("annual_report") || combined.Contains("annual-report") ||
                combined.Contains("annualreport") || combined.Contains("integrated report") ||
                System.Text.RegularExpressions.Regex.IsMatch(combined, @"\bar_?20\b|\bar-20\b"))
            {
                return DocumentType.AnnualReport;
            }

            // 2. Investor Presentation / Earnings Presentation
            if (combined.Contains("investor presentation") || combined.Contains("earnings presentation") || combined.Contains("investor_presentation") ||
                combined.Contains("results presentation") || combined.Contains("investor deck") || combined.Contains("analyst presentation") || combined.Contains("investor-presentation"))
            {
                return DocumentType.InvestorPresentation;
            }

            // 3. Earnings Call / Concall Transcript
            if (combined.Contains("transcript") || combined.Contains("call transcript") || combined.Contains("concall") ||
                combined.Contains("audio transcript") || combined.Contains("earnings call"))
            {
                return DocumentType.ConcallTranscript;
            }

            // 4. Shareholding Pattern
            if (combined.Contains("shareholding") || combined.Contains("shareholding pattern") || combined.Contains("shp") ||
                combined.Contains("shareholding_pattern"))
            {
                return DocumentType.ShareholdingPattern;
            }

            // 5. Corporate Governance
            if (combined.Contains("corporate governance") || combined.Contains("governance report") || combined.Contains("cg report"))
            {
                return DocumentType.CorporateGovernance;
            }

            // 6. Credit Rating
            if (combined.Contains("credit rating") || combined.Contains("rating report") || combined.Contains("crisil") || combined.Contains("icra") || combined.Contains("care rating"))
            {
                return DocumentType.CreditRating;
            }

            // 7. Quarterly / Financial Results
            if (combined.Contains("quarterly") || combined.Contains("q1") || combined.Contains("q2") || combined.Contains("q3") || combined.Contains("q4") ||
                combined.Contains("financial results") || combined.Contains("financial_results") || combined.Contains("quarterly results") ||
                combined.Contains("un-audited results") || combined.Contains("unaudited results") || combined.Contains("audited results") ||
                combined.Contains("financial performance"))
            {
                return DocumentType.QuarterlyResults;
            }

            // 8. Financial Statements
            if (combined.Contains("financial statement") || combined.Contains("balance sheet") || combined.Contains("profit and loss") ||
                combined.Contains("standalone") || combined.Contains("consolidated"))
            {
                return DocumentType.FinancialStatements;
            }

            // 9. Press Release / Investor Update
            if (combined.Contains("press release") || combined.Contains("media release") || combined.Contains("press_release"))
            {
                return DocumentType.PressRelease;
            }

            if (combined.Contains("investor update") || combined.Contains("business update") || combined.Contains("factsheet"))
            {
                return DocumentType.InvestorUpdate;
            }

            return DocumentType.Other;
        }
    }
}
