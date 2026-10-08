using System;
using System.Linq;
using System.Text.RegularExpressions;
using InvestorDataAutomation.Core.Interfaces;

namespace InvestorDataAutomation.Services
{
    public class FinancialPeriodDetector : IFinancialPeriodDetector
    {
        // No letter before "FY", so "notify 2023" or "modify 25" are not read as financial years ("Q2_FY2026" still is).
        private static readonly Regex FyFullRegex = new(@"(?<![a-z])FY\s*(20\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FyShortRegex = new(@"(?<![a-z])FY\s*(\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A range is two consecutive years ("2024-25", "2024/2025"); "2016/107-form" is a folder and a file number.
        private static readonly Regex YearRangeRegex = new(@"(?<!\d)(20\d{2})[\s\-_/\\]+(20)?(\d{2})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StandaloneYearRegex = new(@"\b(20[1-3]\d)\b", RegexOptions.Compiled);

        private static readonly Regex QuarterRegex = new(@"\b(Q[1-4])\b|quarter\s*([1-4])|([1-4])(?:st|nd|rd|th)?\s*quarter", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MonthYearRegex = new(@"\b(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s*[\-_,\s]* (20\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public (string? FinancialYear, string? Quarter, string? DocumentDate) ExtractPeriod(string text, string filename, string url)
        {
            var combined = $"{filename} {url} {text}";

            string? fy = null;
            string? quarter = null;
            string? docDate = null;

            // 1. Extract Quarter
            var qMatch = QuarterRegex.Match(combined);
            if (qMatch.Success)
            {
                if (!string.IsNullOrEmpty(qMatch.Groups[1].Value))
                    quarter = qMatch.Groups[1].Value.ToUpperInvariant();
                else if (!string.IsNullOrEmpty(qMatch.Groups[2].Value))
                    quarter = $"Q{qMatch.Groups[2].Value}";
                else if (!string.IsNullOrEmpty(qMatch.Groups[3].Value))
                    quarter = $"Q{qMatch.Groups[3].Value}";
            }

            // 2. Extract Financial Year
            var fyFullMatch = FyFullRegex.Match(combined);
            if (fyFullMatch.Success)
            {
                fy = $"FY{fyFullMatch.Groups[1].Value}";
            }
            else
            {
                var yearRangeMatch = YearRangeRegex.Matches(combined)
                    .FirstOrDefault(m => (int.Parse(m.Groups[1].Value) + 1) % 100 == int.Parse(m.Groups[3].Value));
                if (yearRangeMatch != null)
                {
                    var y1 = yearRangeMatch.Groups[1].Value;
                    var y2 = yearRangeMatch.Groups[3].Value;
                    fy = $"FY{y1}-{y2}";
                }
                else
                {
                    var fyShortMatch = FyShortRegex.Match(combined);
                    if (fyShortMatch.Success)
                    {
                        fy = $"FY20{fyShortMatch.Groups[1].Value}";
                    }
                    else
                    {
                        var standaloneMatch = StandaloneYearRegex.Match(combined);
                        if (standaloneMatch.Success)
                        {
                            fy = $"FY{standaloneMatch.Groups[1].Value}";
                        }
                    }
                }
            }

            // 3. Extract Month/Year Date
            var monthMatch = MonthYearRegex.Match(combined);
            if (monthMatch.Success)
            {
                docDate = $"{monthMatch.Groups[1].Value} {monthMatch.Groups[2].Value}";
            }

            return (fy, quarter, docDate);
        }
    }
}
