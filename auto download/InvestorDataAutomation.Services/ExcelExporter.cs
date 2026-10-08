using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Models;

namespace InvestorDataAutomation.Services
{
    public class ExcelExporter : IExcelExporter
    {
        public Task<byte[]> ExportReportAsync(List<Company> companies)
        {
            using var workbook = new XLWorkbook();

            // Sheet 1 – Summary
            var summarySheet = workbook.Worksheets.Add("Summary");
            summarySheet.Cell(1, 1).Value = "Company";
            summarySheet.Cell(1, 2).Value = "Website";
            summarySheet.Cell(1, 3).Value = "Investor Page";
            summarySheet.Cell(1, 4).Value = "Status";
            summarySheet.Cell(1, 5).Value = "Documents Found";
            summarySheet.Cell(1, 6).Value = "Documents Downloaded";
            summarySheet.Cell(1, 7).Value = "Failed Documents";
            summarySheet.Cell(1, 8).Value = "Last Checked";

            var headerRange1 = summarySheet.Range(1, 1, 1, 8);
            headerRange1.Style.Font.Bold = true;
            headerRange1.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
            headerRange1.Style.Font.FontColor = XLColor.White;

            int row1 = 2;
            foreach (var company in companies)
            {
                var docsFound = company.Documents.Count;
                var docsDownloaded = company.Documents.Count(d => d.Downloaded);
                var docsFailed = company.Documents.Count(d => d.Status == "Failed");

                summarySheet.Cell(row1, 1).Value = company.CompanyName;
                summarySheet.Cell(row1, 2).Value = company.Website ?? "";
                summarySheet.Cell(row1, 3).Value = company.InvestorPage ?? "";
                summarySheet.Cell(row1, 4).Value = company.Status.ToString();
                summarySheet.Cell(row1, 5).Value = docsFound;
                summarySheet.Cell(row1, 6).Value = docsDownloaded;
                summarySheet.Cell(row1, 7).Value = docsFailed;
                summarySheet.Cell(row1, 8).Value = company.UpdatedDate.ToString("yyyy-MM-dd HH:mm");
                row1++;
            }
            summarySheet.Columns().AdjustToContents();

            // Sheet 2 – Documents
            var docSheet = workbook.Worksheets.Add("Documents");
            docSheet.Cell(1, 1).Value = "Company";
            docSheet.Cell(1, 2).Value = "Document Title";
            docSheet.Cell(1, 3).Value = "Document Type";
            docSheet.Cell(1, 4).Value = "Financial Year";
            docSheet.Cell(1, 5).Value = "Quarter";
            docSheet.Cell(1, 6).Value = "Document Date";
            docSheet.Cell(1, 7).Value = "Original URL";
            docSheet.Cell(1, 8).Value = "Local File";
            docSheet.Cell(1, 9).Value = "File Type";
            docSheet.Cell(1, 10).Value = "File Size (KB)";
            docSheet.Cell(1, 11).Value = "Download Status";

            var headerRange2 = docSheet.Range(1, 1, 1, 11);
            headerRange2.Style.Font.Bold = true;
            headerRange2.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");
            headerRange2.Style.Font.FontColor = XLColor.White;

            int row2 = 2;
            foreach (var company in companies)
            {
                foreach (var doc in company.Documents)
                {
                    docSheet.Cell(row2, 1).Value = company.CompanyName;
                    docSheet.Cell(row2, 2).Value = doc.DocumentTitle;
                    docSheet.Cell(row2, 3).Value = doc.DocumentType;
                    docSheet.Cell(row2, 4).Value = doc.FinancialYear ?? "";
                    docSheet.Cell(row2, 5).Value = doc.Quarter ?? "";
                    docSheet.Cell(row2, 6).Value = doc.DocumentDate ?? "";
                    docSheet.Cell(row2, 7).Value = doc.DocumentUrl;
                    docSheet.Cell(row2, 8).Value = doc.LocalFileName ?? "";
                    docSheet.Cell(row2, 9).Value = doc.FileExtension ?? "";
                    docSheet.Cell(row2, 10).Value = doc.FileSize > 0 ? doc.FileSize / 1024 : 0;
                    docSheet.Cell(row2, 11).Value = doc.Status;
                    row2++;
                }
            }
            docSheet.Columns().AdjustToContents();

            // Sheet 3 – Failed
            var failedSheet = workbook.Worksheets.Add("Failed");
            failedSheet.Cell(1, 1).Value = "Company";
            failedSheet.Cell(1, 2).Value = "Stage";
            failedSheet.Cell(1, 3).Value = "URL";
            failedSheet.Cell(1, 4).Value = "Error";
            failedSheet.Cell(1, 5).Value = "Retry Count";
            failedSheet.Cell(1, 6).Value = "Last Attempt";

            var headerRange3 = failedSheet.Range(1, 1, 1, 6);
            headerRange3.Style.Font.Bold = true;
            headerRange3.Style.Fill.BackgroundColor = XLColor.FromHtml("#991B1B");
            headerRange3.Style.Font.FontColor = XLColor.White;

            int row3 = 2;
            foreach (var company in companies.Where(c => c.Status == CompanyStatus.Failed || c.Status == CompanyStatus.NeedsReview || c.FailureReason != null))
            {
                failedSheet.Cell(row3, 1).Value = company.CompanyName;
                failedSheet.Cell(row3, 2).Value = company.Status.ToString();
                failedSheet.Cell(row3, 3).Value = company.Website ?? company.InvestorPage ?? "";
                failedSheet.Cell(row3, 4).Value = company.FailureReason ?? "Failed processing";
                failedSheet.Cell(row3, 5).Value = company.RetryCount;
                failedSheet.Cell(row3, 6).Value = company.UpdatedDate.ToString("yyyy-MM-dd HH:mm");
                row3++;
            }
            failedSheet.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return Task.FromResult(ms.ToArray());
        }
    }
}
