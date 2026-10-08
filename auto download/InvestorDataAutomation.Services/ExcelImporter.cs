using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using InvestorDataAutomation.Core.DTOs;
using InvestorDataAutomation.Core.Interfaces;

namespace InvestorDataAutomation.Services
{
    public class ExcelImporter : IExcelImporter
    {
        public Task<List<CompanyImportDto>> ImportCompaniesAsync(Stream stream)
        {
            var list = new List<CompanyImportDto>();
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheets.FirstOrDefault();

            if (worksheet == null) return Task.FromResult(list);

            var firstRow = worksheet.FirstRowUsed();
            if (firstRow == null) return Task.FromResult(list);

            // Map Header Column Names to Indexes
            var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in firstRow.CellsUsed())
            {
                var val = cell.Value.ToString().Trim().Replace(" ", "").Replace("_", "");
                colMap[val] = cell.Address.ColumnNumber;
            }

            int companyNameCol = GetColumnIndex(colMap, "CompanyName", "Company");
            int nseCol = GetColumnIndex(colMap, "NSESymbol", "NSE", "Symbol");
            int bseCol = GetColumnIndex(colMap, "BSECode", "BSE", "Code");
            int websiteCol = GetColumnIndex(colMap, "Website", "OfficialWebsite", "Url");

            if (companyNameCol == 0) companyNameCol = 1; // Default first column

            var rows = worksheet.RowsUsed().Skip(1); // Skip header
            foreach (var row in rows)
            {
                var name = row.Cell(companyNameCol).Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var nse = nseCol > 0 ? row.Cell(nseCol).Value.ToString().Trim() : null;
                var bse = bseCol > 0 ? row.Cell(bseCol).Value.ToString().Trim() : null;
                var web = websiteCol > 0 ? row.Cell(websiteCol).Value.ToString().Trim() : null;

                list.Add(new CompanyImportDto
                {
                    CompanyName = name,
                    NSESymbol = string.IsNullOrWhiteSpace(nse) ? null : nse,
                    BSECode = string.IsNullOrWhiteSpace(bse) ? null : bse,
                    Website = string.IsNullOrWhiteSpace(web) ? null : web
                });
            }

            return Task.FromResult(list);
        }

        private static int GetColumnIndex(Dictionary<string, int> map, params string[] candidates)
        {
            foreach (var cand in candidates)
            {
                var clean = cand.Replace(" ", "").Replace("_", "");
                if (map.TryGetValue(clean, out var idx)) return idx;
            }
            return 0;
        }
    }
}
