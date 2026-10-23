using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Planilha Excel (.xlsx) mínima, sem dependências: várias abas, textos e números, primeira linha em negrito.
    /// </summary>
    public class XlsxWriter
    {
        private readonly List<(string Name, List<IList<object>> Rows)> _sheets = new();

        /// <summary>Adiciona uma aba. Valores double/int viram números; o resto vira texto.</summary>
        public XlsxWriter Sheet(string name, IEnumerable<IList<object>> rows)
        {
            string clean = new string((name ?? "Planilha").Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            if (clean.Length > 31) clean = clean.Substring(0, 31);
            if (clean.Length == 0) clean = "Planilha";
            string unique = clean;
            for (int i = 2; _sheets.Any(s => s.Name == unique); i++) unique = clean.Length > 27 ? clean.Substring(0, 27) + $" ({i})" : $"{clean} ({i})";
            _sheets.Add((unique, rows.ToList()));
            return this;
        }

        public void Save(string path)
        {
            if (_sheets.Count == 0) Sheet("Planilha", new List<IList<object>>());
            if (File.Exists(path)) File.Delete(path);
            using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);

            var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= _sheets.Count; i++)
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            types.Append("</Types>");
            Write(zip, "[Content_Types].xml", types.ToString());

            Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= _sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{Esc(_sheets[i - 1].Name)}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
                rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            }
            wb.Append("</sheets></workbook>");
            rels.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            rels.Append("</Relationships>");
            Write(zip, "xl/workbook.xml", wb.ToString());
            Write(zip, "xl/_rels/workbook.xml.rels", rels.ToString());

            Write(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "</styleSheet>");

            for (int i = 1; i <= _sheets.Count; i++)
                Write(zip, $"xl/worksheets/sheet{i}.xml", SheetXml(_sheets[i - 1].Rows));
        }

        private static string SheetXml(List<IList<object>> rows)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            if (cols > 0)
            {
                sb.Append("<cols>");
                for (int c = 1; c <= cols; c++)
                {
                    int width = Math.Min(60, Math.Max(10, rows.Select(r => c - 1 < r.Count ? (r[c - 1]?.ToString()?.Length ?? 0) : 0).Max() + 2));
                    sb.Append($"<col min=\"{c}\" max=\"{c}\" width=\"{width}\" customWidth=\"1\"/>");
                }
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < rows[r].Count; c++)
                {
                    object v = rows[r][c];
                    string reference = Column(c) + (r + 1);
                    string style = r == 0 ? " s=\"1\"" : "";
                    switch (v)
                    {
                        case null:
                            break;
                        case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                            sb.Append($"<c r=\"{reference}\"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                            break;
                        case int n:
                            sb.Append($"<c r=\"{reference}\"{style}><v>{n}</v></c>");
                            break;
                        default:
                            sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Esc(v.ToString())}</t></is></c>");
                            break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string Column(int index)
        {
            string s = "";
            for (int i = index + 1; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
            return s;
        }

        private static string Esc(string s) => SecurityElement.Escape(s ?? "") ?? "";

        private static void Write(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
