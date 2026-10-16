using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace RafaelElectrical.Services
{
    /// <summary>
    /// Gerador mínimo de documentos Word (.docx) sem bibliotecas externas: títulos, parágrafos,
    /// listas, tabelas, quebra de página, sumário (campo TOC) e rodapé com número de página.
    /// Trechos entre [[ e ]] saem destacados em amarelo (campos a completar).
    /// </summary>
    public class DocxBuilder
    {
        private readonly StringBuilder _body = new StringBuilder();

        private static string X(string s) => SecurityElement.Escape(s ?? string.Empty);

        private static string Runs(string text, bool bold = false, int sizeHalfPoints = 0)
        {
            var sb = new StringBuilder();
            string rest = text ?? string.Empty;
            while (rest.Length > 0)
            {
                int start = rest.IndexOf("[[", System.StringComparison.Ordinal);
                int end = start >= 0 ? rest.IndexOf("]]", start + 2, System.StringComparison.Ordinal) : -1;
                if (start < 0 || end < 0)
                {
                    sb.Append(Run(rest, bold, sizeHalfPoints, false));
                    break;
                }
                if (start > 0) sb.Append(Run(rest.Substring(0, start), bold, sizeHalfPoints, false));
                sb.Append(Run(rest.Substring(start + 2, end - start - 2), bold, sizeHalfPoints, true));
                rest = rest.Substring(end + 2);
            }
            return sb.ToString();
        }

        private static string Run(string text, bool bold, int size, bool highlight)
        {
            var props = new StringBuilder();
            if (bold) props.Append("<w:b/>");
            if (highlight) props.Append("<w:highlight w:val=\"yellow\"/>");
            if (size > 0) props.Append($"<w:sz w:val=\"{size}\"/>");
            string rpr = props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "";
            return $"<w:r>{rpr}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";
        }

        public DocxBuilder Title(string text, string subtitle = null)
        {
            _body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Title\"/><w:jc w:val=\"center\"/></w:pPr>{Runs(text)}</w:p>");
            if (!string.IsNullOrEmpty(subtitle))
                _body.Append($"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>{Runs(subtitle, false, 28)}</w:p>");
            return this;
        }

        public DocxBuilder Heading1(string text)
        {
            _body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>{Runs(text)}</w:p>");
            return this;
        }

        public DocxBuilder Heading2(string text)
        {
            _body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr>{Runs(text)}</w:p>");
            return this;
        }

        public DocxBuilder Paragraph(string text, bool bold = false, bool center = false)
        {
            string ppr = center ? "<w:pPr><w:jc w:val=\"center\"/></w:pPr>" : "<w:pPr><w:jc w:val=\"both\"/></w:pPr>";
            _body.Append($"<w:p>{ppr}{Runs(text, bold)}</w:p>");
            return this;
        }

        public DocxBuilder Bullet(string text)
        {
            _body.Append($"<w:p><w:pPr><w:ind w:left=\"567\" w:hanging=\"283\"/></w:pPr>{Runs("•  " + text)}</w:p>");
            return this;
        }

        public DocxBuilder Blank(int count = 1)
        {
            for (int i = 0; i < count; i++) _body.Append("<w:p/>");
            return this;
        }

        public DocxBuilder PageBreak()
        {
            _body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            return this;
        }

        public DocxBuilder TableOfContents()
        {
            _body.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" + Run("SUMÁRIO", false, 0, false) + "</w:p>");
            _body.Append("<w:p><w:r><w:fldChar w:fldCharType=\"begin\" w:dirty=\"true\"/></w:r>" +
                         "<w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-2\" \\h \\z \\u </w:instrText></w:r>" +
                         "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
                         Run("Clique com o botão direito e escolha \"Atualizar campo\" para gerar o sumário.", false, 0, true) +
                         "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>");
            return this;
        }

        public DocxBuilder Table(IList<string> header, IEnumerable<IList<string>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/>" +
                      "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/>" +
                      "<w:right w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>");
            sb.Append(Row(header, true));
            foreach (IList<string> r in rows) sb.Append(Row(r, false));
            sb.Append("</w:tbl>");
            _body.Append(sb).Append("<w:p/>");
            return this;
        }

        private static string Row(IList<string> cells, bool header)
        {
            var sb = new StringBuilder("<w:tr>");
            foreach (string c in cells)
            {
                string shade = header ? "<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"D9E2F3\"/></w:tcPr>" : "";
                sb.Append($"<w:tc>{shade}<w:p>{Runs(c, header, 18)}</w:p></w:tc>");
            }
            return sb.Append("</w:tr>").ToString();
        }

        public void Save(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);

            void Put(string name, string content)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                              "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

            Put("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
                "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>" +
                "</Types>");

            Put("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            Put("word/_rels/document.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>" +
                "</Relationships>");

            Put("word/settings.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings {ns}><w:updateFields w:val=\"true\"/></w:settings>");

            Put("word/footer1.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr {ns}><w:p><w:pPr><w:jc w:val=\"right\"/></w:pPr>" +
                "<w:r><w:t xml:space=\"preserve\">Página </w:t></w:r>" +
                "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r>" +
                "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
                "</w:p></w:ftr>");

            Put("word/styles.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {ns}>" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"22\"/><w:lang w:val=\"pt-BR\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:before=\"2400\" w:after=\"240\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"44\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"360\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"28\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"24\"/></w:rPr></w:style>" +
                "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/></w:style>" +
                "</w:styles>");

            Put("word/document.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>{_body}" +
                "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rId3\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                "<w:pgMar w:top=\"1418\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr>" +
                "</w:body></w:document>");
        }
    }
}
