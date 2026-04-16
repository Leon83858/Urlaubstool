using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NPOI.HSSF.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.SS.UserModel;

namespace Urlaubstool.TemplateAnalyzer;

/// <summary>
/// Analyzes the Excel template to discover all placeholder keys.
/// This utility is used to identify all "Hier ..." placeholders in the template.
/// </summary>
public static class TemplateAnalyzer
{
    public static void Main(string[] args)
    {
        try
        {
            string templatePath = args.Length > 0 ? args[0] : "basis/Urlaubsschein.xls";
            
            if (!File.Exists(templatePath))
            {
                Console.WriteLine($"Template file not found: {templatePath}");
                Console.WriteLine("Usage: dotnet run <templatePath>");
                return;
            }

            Console.WriteLine($"Analyzing template: {templatePath}");
            Console.WriteLine("=".PadRight(60, '='));

            var placeholders = AnalyzeTemplate(templatePath);

            Console.WriteLine($"\nFound {placeholders.Count} placeholders:");
            Console.WriteLine("-".PadRight(60, '-'));
            
            foreach (var key in placeholders.OrderBy(k => k))
            {
                Console.WriteLine($"  - {key}");
            }

            Console.WriteLine("\n" + "=".PadRight(60, '='));
            Console.WriteLine($"Documentation for README:\n");
            Console.WriteLine("## Excel Template Placeholders (basis/Urlaubsschein.xls)\n");
            Console.WriteLine("The following placeholder keys are required in the template:");
            foreach (var key in placeholders.OrderBy(k => k))
            {
                Console.WriteLine($"- `{key}`");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>
    /// Scans all worksheets in the Excel file and collects all placeholder keys.
    /// Handles both .xls (HSSF) and .xlsx (XSSF) formats.
    /// </summary>
    public static HashSet<string> AnalyzeTemplate(string templatePath)
    {
        var placeholders = new HashSet<string>();

        using (var fileStream = new FileStream(templatePath, FileMode.Open, FileAccess.Read))
        {
            IWorkbook workbook;
            
            // Try XSSF first (modern Excel format), then fall back to HSSF (legacy)
            try
            {
                fileStream.Seek(0, SeekOrigin.Begin);
                workbook = new XSSFWorkbook(fileStream);
            }
            catch
            {
                fileStream.Seek(0, SeekOrigin.Begin);
                workbook = new HSSFWorkbook(fileStream);
            }

            for (int sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
            {
                var sheet = workbook.GetSheetAt(sheetIndex);
                Console.WriteLine($"\nSheet: {sheet.SheetName}");
                Console.WriteLine("-".PadRight(40, '-'));

                for (int rowIndex = 0; rowIndex <= sheet.LastRowNum; rowIndex++)
                {
                    var row = sheet.GetRow(rowIndex);
                    if (row == null) continue;

                    for (int cellIndex = 0; cellIndex < row.LastCellNum; cellIndex++)
                    {
                        var cell = row.GetCell(cellIndex);
                        if (cell == null) continue;

                        string cellValue = GetCellValue(cell);
                        if (string.IsNullOrWhiteSpace(cellValue)) continue;

                        // Check if cell contains a placeholder starting with "Hier"
                        if (cellValue.StartsWith("Hier", StringComparison.OrdinalIgnoreCase))
                        {
                            // Extract key: "Hier Name" -> "Name"
                            string key = cellValue.Substring(4).Trim().TrimEnd(':').Trim();
                            
                            if (!string.IsNullOrEmpty(key))
                            {
                                placeholders.Add(key);
                                Console.WriteLine($"  [R{rowIndex + 1}:C{cellIndex + 1}] {cellValue} => KEY: '{key}'");
                            }
                        }
                    }
                }
            }

            workbook.Close();
        }

        return placeholders;
    }

    /// <summary>
    /// Extracts the cell value as a string, handling different cell types.
    /// </summary>
    private static string GetCellValue(ICell cell)
    {
        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => cell.NumericCellValue.ToString(),
            CellType.Boolean => cell.BooleanCellValue.ToString(),
            CellType.Formula => cell.CellFormula,
            _ => ""
        };
    }
}
