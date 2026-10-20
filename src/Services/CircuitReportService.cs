using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RafaelElectrical.Services
{
    public class CircuitRow
    {
        public ElementId Id { get; set; }
        public string Panel { get; set; }
        public string Number { get; set; }
        public string LoadName { get; set; }
        public double VoltageV { get; set; }
        public int Poles { get; set; }
        public double ApparentLoadVA { get; set; }
        public double TrueLoadW { get; set; }
        public double CurrentA { get; set; }
        public double RatingA { get; set; }
        public string WireSize { get; set; }
        public double LengthM { get; set; }
        public int ElementCount { get; set; }
    }

    public static class CircuitReportService
    {
        public const string NoPanel = "(sem quadro)";

        public static List<CircuitRow> Collect(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.PowerCircuit)
                .Select(c => ToRow(c))
                .OrderBy(r => r.Panel, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.Number, new NaturalComparer())
                .ToList();
        }

        private static CircuitRow ToRow(ElectricalSystem c)
        {
            return new CircuitRow
            {
                Id = c.Id,
                Panel = string.IsNullOrWhiteSpace(c.PanelName) ? NoPanel : c.PanelName,
                Number = c.CircuitNumber,
                LoadName = c.LoadName,
                VoltageV = Safe(() => UnitUtils.ConvertFromInternalUnits(c.Voltage, UnitTypeId.Volts)),
                Poles = Safe(() => c.PolesNumber),
                ApparentLoadVA = Safe(() => UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes)),
                TrueLoadW = Safe(() => UnitUtils.ConvertFromInternalUnits(c.TrueLoad, UnitTypeId.Watts)),
                CurrentA = Safe(() => UnitUtils.ConvertFromInternalUnits(c.ApparentCurrent, UnitTypeId.Amperes)),
                RatingA = Safe(() => UnitUtils.ConvertFromInternalUnits(
                    c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0, UnitTypeId.Amperes)),
                WireSize = c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM)?.AsString() ?? "",
                LengthM = Safe(() => UnitUtils.ConvertFromInternalUnits(c.Length, UnitTypeId.Meters)),
                ElementCount = Safe(() => c.Elements?.Size ?? 0),
            };
        }

        private static T Safe<T>(Func<T> getter)
        {
            try { return getter(); } catch (Exception) { return default; }
        }

        /// <summary>Exporta em CSV com ';' e vírgula decimal, que abre direto no Excel em português.</summary>
        public static void ExportXlsx(IEnumerable<CircuitRow> rows, string path)
        {
            var sheet = new List<IList<object>>
            {
                new object[] { "Quadro", "Circuito", "Descrição", "Tensão (V)", "Polos", "Pot. aparente (VA)", "Pot. ativa (W)", "Corrente (A)", "Disjuntor (A)", "Fiação", "Comprimento (m)", "Nº elementos" },
            };
            sheet.AddRange(rows.Select(r => (IList<object>)new object[]
            {
                r.Panel, r.Number, r.LoadName, Math.Round(r.VoltageV), r.Poles, Math.Round(r.ApparentLoadVA), Math.Round(r.TrueLoadW),
                Math.Round(r.CurrentA, 2), Math.Round(r.RatingA), r.WireSize, Math.Round(r.LengthM, 2), r.ElementCount,
            }));
            new XlsxWriter().Sheet("Cargas", sheet).Save(path);
        }

        public static void ExportCsv(IEnumerable<CircuitRow> rows, string path)
        {
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) { ExportXlsx(rows, path); return; }
            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine("Quadro;Circuito;Descrição;Tensão (V);Polos;Pot. aparente (VA);Pot. ativa (W);Corrente (A);Disjuntor (A);Fiação;Comprimento (m);Nº elementos");
            foreach (CircuitRow r in rows)
            {
                sb.AppendLine(string.Join(";",
                    Esc(r.Panel), Esc(r.Number), Esc(r.LoadName),
                    r.VoltageV.ToString("0", br), r.Poles.ToString(br),
                    r.ApparentLoadVA.ToString("0", br), r.TrueLoadW.ToString("0", br),
                    r.CurrentA.ToString("0.00", br), r.RatingA.ToString("0", br),
                    Esc(r.WireSize), r.LengthM.ToString("0.00", br), r.ElementCount.ToString(br)));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Esc(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>Ordena "2" antes de "10" e "2,4" corretamente.</summary>
        private class NaturalComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                a ??= ""; b ??= "";
                bool na = int.TryParse(new string(a.TakeWhile(char.IsDigit).ToArray()), out int ia);
                bool nb = int.TryParse(new string(b.TakeWhile(char.IsDigit).ToArray()), out int ib);
                if (na && nb && ia != ib) return ia.CompareTo(ib);
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            }
        }
    }
}
