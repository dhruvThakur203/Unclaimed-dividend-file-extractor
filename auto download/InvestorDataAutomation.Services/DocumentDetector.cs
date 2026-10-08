using System;
using System.IO;
using System.Linq;
using InvestorDataAutomation.Core.Interfaces;

namespace InvestorDataAutomation.Services
{
    public class DocumentDetector : IDocumentDetector
    {
        private static readonly string[] SupportedExtensions = { ".pdf", ".xls", ".xlsx", ".csv", ".doc", ".docx", ".ppt", ".pptx" };

        public bool IsDocumentUrl(string url, string text)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Check URL path extension
            var cleanUrl = url.Split('?')[0].Split('#')[0];
            var ext = Path.GetExtension(cleanUrl)?.ToLowerInvariant();

            if (!string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext))
            {
                return true;
            }

            // Check query string parameters for download terms (e.g., download.ashx?file=report.pdf)
            var lowerUrl = url.ToLowerInvariant();
            if (SupportedExtensions.Any(e => lowerUrl.Contains(e)))
            {
                return true;
            }

            // Check anchor text / title clues
            var lowerText = text.ToLowerInvariant();
            if ((lowerText.Contains("download") || lowerText.Contains("pdf") || lowerText.Contains("annual report") || lowerText.Contains("results")) 
                && (lowerUrl.Contains("download") || lowerUrl.Contains("pdf") || lowerUrl.Contains("file") || lowerUrl.Contains("doc") || lowerUrl.Contains("attachment")))
            {
                return true;
            }

            return false;
        }

        public string ExtrapolateExtension(string url, string contentType)
        {
            var cleanUrl = url.Split('?')[0].Split('#')[0];
            var ext = Path.GetExtension(cleanUrl)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext))
            {
                return ext;
            }

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var lowerType = contentType.ToLowerInvariant();
                if (lowerType.Contains("pdf")) return ".pdf";
                if (lowerType.Contains("spreadsheetml") || lowerType.Contains("xlsx")) return ".xlsx";
                if (lowerType.Contains("ms-excel") || lowerType.Contains("xls")) return ".xls";
                if (lowerType.Contains("csv")) return ".csv";
            }

            return ".pdf"; // default fallback
        }
    }
}
