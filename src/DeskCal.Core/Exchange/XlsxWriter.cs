using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DeskCal.Core.Exchange;

public enum XlsxStyle { Normal = 0, Header = 1, Cell = 2, CellOff = 3, CellMuted = 4, Date = 5, Title = 6, Wrap = 7, Bold = 8 }

public sealed record XlsxCell(object? Value, XlsxStyle Style = XlsxStyle.Normal);

/// <summary>One worksheet. Values: string, int/long/double (number), DateOnly (date), or null (empty, still styled).</summary>
public sealed class XlsxSheet(string name)
{
    public string Name { get; } = name;
    public List<List<XlsxCell?>> Rows { get; } = [];
    public List<double> ColumnWidths { get; } = [];
    public Dictionary<int, double> RowHeights { get; } = [];      // 0-based row index -> points
    public List<(int Row1, int Col1, int Row2, int Col2)> Merges { get; } = [];   // 0-based, inclusive
    public bool FreezeFirstRow { get; set; }
    public bool AutoFilter { get; set; }
    public bool LandscapeFitToPage { get; set; }

    public int AddRow(params XlsxCell?[] cells) { Rows.Add([.. cells]); return Rows.Count - 1; }
}

/// <summary>
/// A small, dependency-free writer for .xlsx (Office Open XML): text is stored inline, styles come from a fixed table.
/// Enough for list and calendar sheets; nothing here reads workbooks.
/// </summary>
public static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly DateOnly Epoch = new(1899, 12, 30);

    public static void Write(Stream output, IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.", nameof(sheets));
        var names = UniqueNames(sheets.Select(s => s.Name));

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 1; i <= sheets.Count; i++) Override(w, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            w.WriteEndElement();
        });
        Part(zip, "_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        Part(zip, "xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", Main);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            w.WriteStartElement("sheets");
            for (int i = 0; i < sheets.Count; i++)
            {
                w.WriteStartElement("sheet");
                w.WriteAttributeString("name", names[i]);
                w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("id", RelNs, "rId" + (i + 1));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        });
        Part(zip, "xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 1; i <= sheets.Count; i++) Rel(w, "rId" + i, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i}.xml");
            Rel(w, "rId" + (sheets.Count + 1), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            w.WriteEndElement();
        });
        Part(zip, "xl/styles.xml", WriteStyles);
        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet));
        }
    }

    // ---- workbook parts ---------------------------------------------------------------

    private static void WriteStyles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", Main);

        w.WriteStartElement("numFmts"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("numFmt"); w.WriteAttributeString("numFmtId", "164"); w.WriteAttributeString("formatCode", "yyyy\\-mm\\-dd"); w.WriteEndElement();
        w.WriteEndElement();

        // fonts: 0 normal, 1 bold, 2 red, 3 grey, 4 title
        w.WriteStartElement("fonts"); w.WriteAttributeString("count", "5");
        Font(w, false, 10, null); Font(w, true, 10, null); Font(w, false, 10, "FFC00000"); Font(w, false, 10, "FF808080"); Font(w, true, 14, null);
        w.WriteEndElement();

        // fills: 0 none, 1 gray125 (required), 2 header blue-grey, 3 muted grey
        w.WriteStartElement("fills"); w.WriteAttributeString("count", "4");
        PatternFill(w, "none", null); PatternFill(w, "gray125", null); PatternFill(w, "solid", "FFD9E1F2"); PatternFill(w, "solid", "FFF2F2F2");
        w.WriteEndElement();

        // borders: 0 none, 1 thin all round
        w.WriteStartElement("borders"); w.WriteAttributeString("count", "2");
        w.WriteStartElement("border"); w.WriteElementString("left", null); w.WriteElementString("right", null); w.WriteElementString("top", null); w.WriteElementString("bottom", null); w.WriteElementString("diagonal", null); w.WriteEndElement();
        w.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            w.WriteStartElement(side); w.WriteAttributeString("style", "thin"); w.WriteStartElement("color"); w.WriteAttributeString("rgb", "FFBFBFBF"); w.WriteEndElement(); w.WriteEndElement();
        }
        w.WriteElementString("diagonal", null);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("xf"); w.WriteAttributeString("numFmtId", "0"); w.WriteAttributeString("fontId", "0"); w.WriteAttributeString("fillId", "0"); w.WriteAttributeString("borderId", "0"); w.WriteEndElement();
        w.WriteEndElement();

        // cellXfs, indexed by XlsxStyle
        w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "9");
        Xf(w, 0, 0, 0, 0, null, null, false);          // Normal
        Xf(w, 0, 1, 2, 1, "center", "center", true);   // Header
        Xf(w, 0, 0, 0, 1, "left", "top", true);        // Cell
        Xf(w, 0, 2, 0, 1, "left", "top", true);        // CellOff
        Xf(w, 0, 3, 3, 1, "left", "top", true);        // CellMuted
        Xf(w, 164, 0, 0, 0, "left", null, false);      // Date
        Xf(w, 0, 4, 0, 0, "center", "center", false);  // Title
        Xf(w, 0, 0, 0, 0, "left", "top", true);        // Wrap
        Xf(w, 0, 1, 0, 0, null, null, false);          // Bold
        w.WriteEndElement();

        w.WriteStartElement("cellStyles"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle"); w.WriteAttributeString("name", "Normal"); w.WriteAttributeString("xfId", "0"); w.WriteAttributeString("builtinId", "0"); w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private static void Font(XmlWriter w, bool bold, int size, string? rgb)
    {
        w.WriteStartElement("font");
        if (bold) w.WriteElementString("b", null);
        w.WriteStartElement("sz"); w.WriteAttributeString("val", size.ToString(CultureInfo.InvariantCulture)); w.WriteEndElement();
        if (rgb is not null) { w.WriteStartElement("color"); w.WriteAttributeString("rgb", rgb); w.WriteEndElement(); }
        w.WriteStartElement("name"); w.WriteAttributeString("val", "Malgun Gothic"); w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void PatternFill(XmlWriter w, string pattern, string? rgb)
    {
        w.WriteStartElement("fill"); w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", pattern);
        if (rgb is not null) { w.WriteStartElement("fgColor"); w.WriteAttributeString("rgb", rgb); w.WriteEndElement(); w.WriteStartElement("bgColor"); w.WriteAttributeString("indexed", "64"); w.WriteEndElement(); }
        w.WriteEndElement(); w.WriteEndElement();
    }

    private static void Xf(XmlWriter w, int numFmt, int font, int fill, int border, string? horizontal, string? vertical, bool wrap)
    {
        w.WriteStartElement("xf");
        w.WriteAttributeString("numFmtId", numFmt.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("borderId", border.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("xfId", "0");
        if (numFmt != 0) w.WriteAttributeString("applyNumberFormat", "1");
        if (font != 0) w.WriteAttributeString("applyFont", "1");
        if (fill != 0) w.WriteAttributeString("applyFill", "1");
        if (border != 0) w.WriteAttributeString("applyBorder", "1");
        if (horizontal is not null || vertical is not null || wrap)
        {
            w.WriteAttributeString("applyAlignment", "1");
            w.WriteStartElement("alignment");
            if (horizontal is not null) w.WriteAttributeString("horizontal", horizontal);
            if (vertical is not null) w.WriteAttributeString("vertical", vertical);
            if (wrap) w.WriteAttributeString("wrapText", "1");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter w, XlsxSheet sheet)
    {
        w.WriteStartElement("worksheet", Main);
        if (sheet.LandscapeFitToPage)
        {
            w.WriteStartElement("sheetPr"); w.WriteStartElement("pageSetUpPr"); w.WriteAttributeString("fitToPage", "1"); w.WriteEndElement(); w.WriteEndElement();
        }

        int maxCols = Math.Max(sheet.Rows.Select(r => r.Count).DefaultIfEmpty(0).Max(), sheet.ColumnWidths.Count);
        w.WriteStartElement("dimension"); w.WriteAttributeString("ref", maxCols == 0 || sheet.Rows.Count == 0 ? "A1" : $"A1:{Column(maxCols - 1)}{sheet.Rows.Count}"); w.WriteEndElement();

        w.WriteStartElement("sheetViews"); w.WriteStartElement("sheetView"); w.WriteAttributeString("workbookViewId", "0");
        if (sheet.FreezeFirstRow)
        {
            w.WriteStartElement("pane"); w.WriteAttributeString("ySplit", "1"); w.WriteAttributeString("topLeftCell", "A2");
            w.WriteAttributeString("activePane", "bottomLeft"); w.WriteAttributeString("state", "frozen"); w.WriteEndElement();
        }
        w.WriteEndElement(); w.WriteEndElement();

        w.WriteStartElement("sheetFormatPr"); w.WriteAttributeString("defaultRowHeight", "15"); w.WriteEndElement();

        if (sheet.ColumnWidths.Count > 0)
        {
            w.WriteStartElement("cols");
            for (int i = 0; i < sheet.ColumnWidths.Count; i++)
            {
                w.WriteStartElement("col");
                w.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture)); w.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", sheet.ColumnWidths[i].ToString("0.##", CultureInfo.InvariantCulture)); w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("sheetData");
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            w.WriteStartElement("row"); w.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
            if (sheet.RowHeights.TryGetValue(r, out double h)) { w.WriteAttributeString("ht", h.ToString("0.##", CultureInfo.InvariantCulture)); w.WriteAttributeString("customHeight", "1"); }
            for (int c = 0; c < sheet.Rows[r].Count; c++) WriteCell(w, sheet.Rows[r][c], Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        if (sheet.AutoFilter && sheet.Rows.Count > 1 && maxCols > 0)
        {
            w.WriteStartElement("autoFilter"); w.WriteAttributeString("ref", $"A1:{Column(maxCols - 1)}{sheet.Rows.Count}"); w.WriteEndElement();
        }
        if (sheet.Merges.Count > 0)
        {
            w.WriteStartElement("mergeCells"); w.WriteAttributeString("count", sheet.Merges.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var (r1, c1, r2, c2) in sheet.Merges)
            {
                w.WriteStartElement("mergeCell"); w.WriteAttributeString("ref", $"{Column(c1)}{r1 + 1}:{Column(c2)}{r2 + 1}"); w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("pageMargins");
        foreach (var (k, v) in new[] { ("left", "0.5"), ("right", "0.5"), ("top", "0.6"), ("bottom", "0.6"), ("header", "0.3"), ("footer", "0.3") }) w.WriteAttributeString(k, v);
        w.WriteEndElement();
        if (sheet.LandscapeFitToPage)
        {
            w.WriteStartElement("pageSetup"); w.WriteAttributeString("orientation", "landscape"); w.WriteAttributeString("fitToWidth", "1"); w.WriteAttributeString("fitToHeight", "1"); w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteCell(XmlWriter w, XlsxCell? cell, string reference)
    {
        if (cell is null) return;
        w.WriteStartElement("c");
        w.WriteAttributeString("r", reference);
        if (cell.Style != XlsxStyle.Normal) w.WriteAttributeString("s", ((int)cell.Style).ToString(CultureInfo.InvariantCulture));
        switch (cell.Value)
        {
            case null: break;
            case string s:
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is"); w.WriteStartElement("t"); w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(CleanXml(s)); w.WriteEndElement(); w.WriteEndElement();
                break;
            case DateOnly d:
                w.WriteElementString("v", (d.DayNumber - Epoch.DayNumber).ToString(CultureInfo.InvariantCulture));
                break;
            case int or long or double or float or decimal:
                w.WriteElementString("v", Convert.ToString(cell.Value, CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is"); w.WriteStartElement("t"); w.WriteString(CleanXml(cell.Value.ToString() ?? "")); w.WriteEndElement(); w.WriteEndElement();
                break;
        }
        w.WriteEndElement();
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>"A", "B", ... "Z", "AA" for a 0-based column index.</summary>
    public static string Column(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    /// <summary>Drops characters XML 1.0 cannot carry (most control characters, lone surrogates).</summary>
    public static string CleanXml(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) sb.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>Sheet names: at most 31 characters, none of []:*?/\, not empty, unique ignoring case.</summary>
    internal static List<string> UniqueNames(IEnumerable<string> wanted)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in wanted)
        {
            var name = new string(raw.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim().Trim('\'');
            if (name.Length == 0) name = "Sheet";
            if (name.Length > 31) name = name[..31];
            string candidate = name;
            for (int n = 2; !used.Add(candidate); n++)
            {
                string suffix = " (" + n + ")";
                candidate = name[..Math.Min(name.Length, 31 - suffix.Length)] + suffix;
            }
            result.Add(candidate);
        }
        return result;
    }

    private static void Part(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        w.WriteStartDocument(standalone: true);
        write(w);
        w.WriteEndDocument();
    }

    private static void Default(XmlWriter w, string ext, string type)
    { w.WriteStartElement("Default"); w.WriteAttributeString("Extension", ext); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }

    private static void Override(XmlWriter w, string part, string type)
    { w.WriteStartElement("Override"); w.WriteAttributeString("PartName", part); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }

    private static void Rel(XmlWriter w, string id, string type, string target)
    { w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", id); w.WriteAttributeString("Type", type); w.WriteAttributeString("Target", target); w.WriteEndElement(); }
}
