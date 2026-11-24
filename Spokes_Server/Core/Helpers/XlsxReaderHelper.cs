using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Lightweight, zero-dependency OpenXML (.xlsx) worksheet reader built on <see cref="ZipArchive"/> and <see cref="XDocument"/>.
/// Resolves shared strings (including rich text runs), inline strings, sparse column references (e.g. A1, D1, AA1),
/// and Excel serial date numbers from styles.xml so spreadsheets can be consumed identically to <see cref="CsvReaderHelper"/>.
/// </summary>
public static class XlsxReaderHelper
{
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    // Standard ECMA-376 built-in date / datetime number format IDs
    private static readonly HashSet<int> BuiltInDateNumFmtIds =
    [
        14, 15, 16, 17, 18, 19, 20, 21, 22,
        27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58
    ];

    /// <summary>
    /// Returns true if the payload looks like a modern OpenXML (.xlsx) ZIP archive containing workbook XML.
    /// </summary>
    public static bool IsXlsx(byte[]? bytes, string? fileName = null)
    {
        if (bytes == null || bytes.Length < 4) return false;
        // ZIP local file header signature: PK\x03\x04
        if (bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
            return false;

        if (!string.IsNullOrWhiteSpace(fileName) &&
            fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            return archive.GetEntry("xl/workbook.xml") != null ||
                   archive.Entries.Any(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the payload is a legacy binary Excel 97-2003 (.xls) OLE2 Compound File.
    /// </summary>
    public static bool IsLegacyBinaryXls(byte[]? bytes)
    {
        return bytes is { Length: >= 8 } &&
               bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0 &&
               bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1;
    }

    /// <summary>
    /// Parses the first non-empty worksheet in an .xlsx workbook into tabular string rows.
    /// </summary>
    public static List<List<string>> ParseFirstSheet(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return [];

        using var ms = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        var sharedStrings = ReadSharedStrings(archive);
        var (dateStyleIndices, useDate1904) = ReadDateStyleInfo(archive);
        var sheetPaths = ResolveOrderedWorksheetPaths(archive);

        foreach (var path in sheetPaths)
        {
            var entry = FindEntryCaseInsensitive(archive, path);
            if (entry == null) continue;

            var rows = ParseWorksheet(entry, sharedStrings, dateStyleIndices, useDate1904);
            if (rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c))))
                return rows;
        }

        return [];
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = FindEntryCaseInsensitive(archive, "xl/sharedStrings.xml");
        if (entry == null) return [];

        var doc = LoadXmlSafe(entry);
        var result = new List<string>();
        foreach (var si in doc.Descendants(MainNs + "si"))
        {
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(MainNs + "t"))
            {
                sb.Append(t.Value);
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    private static (HashSet<int> DateStyleIndices, bool UseDate1904) ReadDateStyleInfo(ZipArchive archive)
    {
        bool useDate1904 = false;
        var wbEntry = FindEntryCaseInsensitive(archive, "xl/workbook.xml");
        if (wbEntry != null)
        {
            var wbDoc = LoadXmlSafe(wbEntry);
            var props = wbDoc.Descendants(MainNs + "workbookPr").FirstOrDefault();
            var d1904 = props?.Attribute("date1904")?.Value;
            useDate1904 = d1904 == "1" || string.Equals(d1904, "true", StringComparison.OrdinalIgnoreCase);
        }

        var dateStyles = new HashSet<int>();
        var stylesEntry = FindEntryCaseInsensitive(archive, "xl/styles.xml");
        if (stylesEntry == null) return (dateStyles, useDate1904);

        var stylesDoc = LoadXmlSafe(stylesEntry);
        var customDateFmtIds = new HashSet<int>();
        foreach (var numFmt in stylesDoc.Descendants(MainNs + "numFmt"))
        {
            if (!int.TryParse(numFmt.Attribute("numFmtId")?.Value, out var fmtId)) continue;
            var formatCode = numFmt.Attribute("formatCode")?.Value ?? string.Empty;
            if (LooksLikeDateFormatCode(formatCode))
                customDateFmtIds.Add(fmtId);
        }

        var cellXfs = stylesDoc.Descendants(MainNs + "cellXfs").FirstOrDefault();
        if (cellXfs != null)
        {
            int idx = 0;
            foreach (var xf in cellXfs.Elements(MainNs + "xf"))
            {
                if (int.TryParse(xf.Attribute("numFmtId")?.Value, out var fmtId) &&
                    (BuiltInDateNumFmtIds.Contains(fmtId) || customDateFmtIds.Contains(fmtId)))
                {
                    dateStyles.Add(idx);
                }
                idx++;
            }
        }

        return (dateStyles, useDate1904);
    }

    private static bool LooksLikeDateFormatCode(string formatCode)
    {
        if (string.IsNullOrWhiteSpace(formatCode)) return false;

        // Strip quoted literal text and bracketed locale/color tokens like [$-40C] or [Red]
        var sb = new StringBuilder(formatCode.Length);
        bool inQuotes = false;
        bool inBrackets = false;
        for (int i = 0; i < formatCode.Length; i++)
        {
            char c = formatCode[i];
            if (c == '\\' && i + 1 < formatCode.Length) { i++; continue; }
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (c == '[') { inBrackets = true; continue; }
            if (c == ']') { inBrackets = false; continue; }
            if (inBrackets) continue;
            sb.Append(char.ToLowerInvariant(c));
        }

        var clean = sb.ToString();
        // Require date markers (y/d/m) and exclude pure numeric/currency formats
        bool hasYearOrDay = clean.Contains("yy") || clean.Contains('d') || clean.Contains("aaaa") || clean.Contains('j');
        bool hasMonth = clean.Contains('m');
        bool looksNumeric = clean.Contains('0') || clean.Contains('#');
        return hasYearOrDay && hasMonth && !looksNumeric;
    }

    private static List<string> ResolveOrderedWorksheetPaths(ZipArchive archive)
    {
        var paths = new List<string>();
        var wbEntry = FindEntryCaseInsensitive(archive, "xl/workbook.xml");
        var relsEntry = FindEntryCaseInsensitive(archive, "xl/_rels/workbook.xml.rels");

        if (wbEntry != null && relsEntry != null)
        {
            var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var relsDoc = LoadXmlSafe(relsEntry);
            foreach (var rel in relsDoc.Descendants(PkgRelNs + "Relationship"))
            {
                var id = rel.Attribute("Id")?.Value;
                var target = rel.Attribute("Target")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target))
                {
                    var normalized = target.TrimStart('/');
                    if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        normalized = "xl/" + normalized;
                    relMap[id] = normalized;
                }
            }

            var wbDoc = LoadXmlSafe(wbEntry);
            foreach (var sheet in wbDoc.Descendants(MainNs + "sheet"))
            {
                var rid = sheet.Attribute(RelNs + "id")?.Value;
                if (!string.IsNullOrWhiteSpace(rid) && relMap.TryGetValue(rid, out var targetPath))
                {
                    paths.Add(targetPath);
                }
            }
        }

        if (paths.Count == 0)
        {
            paths.AddRange(archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.FullName));
        }

        return paths;
    }

    private static List<List<string>> ParseWorksheet(
        ZipArchiveEntry entry,
        List<string> sharedStrings,
        HashSet<int> dateStyleIndices,
        bool useDate1904)
    {
        var doc = LoadXmlSafe(entry);
        var rows = new List<List<string>>();

        foreach (var rowEl in doc.Descendants(MainNs + "row"))
        {
            var cells = new List<string>();
            int nextCol = 0;

            foreach (var cell in rowEl.Elements(MainNs + "c"))
            {
                var cellRef = cell.Attribute("r")?.Value;
                int colIdx = TryParseColumnIndex(cellRef, out var parsedCol) ? parsedCol : nextCol;
                while (cells.Count < colIdx)
                    cells.Add(string.Empty);

                var val = ReadCellValue(cell, sharedStrings, dateStyleIndices, useDate1904);
                if (colIdx < cells.Count)
                    cells[colIdx] = val;
                else
                    cells.Add(val);

                nextCol = colIdx + 1;
            }

            // Trim trailing empty cells for cleanliness
            while (cells.Count > 0 && string.IsNullOrEmpty(cells[^1]))
                cells.RemoveAt(cells.Count - 1);

            if (cells.Count > 0)
                rows.Add(cells);
        }

        return rows;
    }

    private static string ReadCellValue(
        XElement cell,
        List<string> sharedStrings,
        HashSet<int> dateStyleIndices,
        bool useDate1904)
    {
        var cellType = cell.Attribute("t")?.Value ?? string.Empty;

        if (cellType == "inlineStr")
        {
            var isEl = cell.Element(MainNs + "is");
            if (isEl == null) return string.Empty;
            return string.Concat(isEl.Descendants(MainNs + "t").Select(t => t.Value));
        }

        var rawVal = cell.Element(MainNs + "v")?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(rawVal)) return string.Empty;

        if (cellType == "s")
        {
            return int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ssIdx) &&
                   ssIdx >= 0 && ssIdx < sharedStrings.Count
                ? sharedStrings[ssIdx]
                : string.Empty;
        }

        if (cellType == "b")
        {
            return rawVal == "1" ? "TRUE" : "FALSE";
        }

        if (cellType is "str" or "e")
        {
            return rawVal;
        }

        // Numeric cell: check if formatted as a date via style index
        if (int.TryParse(cell.Attribute("s")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleIdx) &&
            dateStyleIndices.Contains(styleIdx) &&
            double.TryParse(rawVal, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var serial) &&
            serial >= 1d && serial < 2958466d)
        {
            var dt = ExcelSerialToDateTime(serial, useDate1904);
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return rawVal;
    }

    private static DateTime ExcelSerialToDateTime(double serial, bool useDate1904)
    {
        if (useDate1904)
            return new DateTime(1904, 1, 1).AddDays(serial);

        // Excel's 1900 date system famously treats 1900 as a leap year (serial 60 = non-existent 1900-02-29)
        var adjusted = serial >= 60d ? serial - 1d : serial;
        return new DateTime(1899, 12, 31).AddDays(adjusted);
    }

    private static bool TryParseColumnIndex(string? cellRef, out int colIndex)
    {
        colIndex = 0;
        if (string.IsNullOrWhiteSpace(cellRef)) return false;

        int result = 0;
        bool sawLetter = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                result = result * 26 + (ch - 'A' + 1);
                sawLetter = true;
            }
            else if (ch is >= 'a' and <= 'z')
            {
                result = result * 26 + (ch - 'a' + 1);
                sawLetter = true;
            }
            else
            {
                break;
            }
        }

        if (!sawLetter) return false;
        colIndex = result - 1;
        return colIndex >= 0;
    }

    private static ZipArchiveEntry? FindEntryCaseInsensitive(ZipArchive archive, string targetPath)
    {
        return archive.GetEntry(targetPath) ??
               archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, targetPath, StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument LoadXmlSafe(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }
}
