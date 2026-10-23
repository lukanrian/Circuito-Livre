using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitoLivre.Services
{
    /// <summary>Nível lido de uma origem (vínculo ou IFC), com elevação em metros.</summary>
    public class SourceLevel
    {
        public SourceLevel(string name, double elevationM)
        {
            Name = name;
            ElevationM = elevationM;
        }

        public string Name { get; }
        public double ElevationM { get; }
    }

    /// <summary>
    /// Lê os pavimentos (IfcBuildingStorey) de um arquivo .ifc (formato STEP) sem bibliotecas externas.
    /// Usa o atributo Elevation do pavimento; se ele não existir, usa a cota Z do posicionamento.
    /// </summary>
    public static class IfcLevelReader
    {
        private static readonly Regex EntityRegex =
            new Regex(@"#(\d+)\s*=\s*([A-Za-z0-9_]+)\s*\((.*?)\)\s*;", RegexOptions.Singleline | RegexOptions.Compiled);

        public static List<SourceLevel> Read(string path)
        {
            string text = File.ReadAllText(path, Encoding.Latin1);
            int data = text.IndexOf("DATA;", StringComparison.OrdinalIgnoreCase);
            if (data >= 0) text = text.Substring(data + 5);

            var entities = new Dictionary<int, (string Type, string Args)>();
            foreach (Match m in EntityRegex.Matches(text))
                entities[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] =
                    (m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value);

            double toMeters = LengthUnitToMeters(entities);
            var result = new List<SourceLevel>();

            foreach (var kv in entities.Where(e => e.Value.Type == "IFCBUILDINGSTOREY"))
            {
                List<string> a = SplitArgs(kv.Value.Args);

                string name = a.Count > 2 ? DecodeString(a[2]) : "";
                if (string.IsNullOrWhiteSpace(name) && a.Count > 7) name = DecodeString(a[7]);
                if (string.IsNullOrWhiteSpace(name)) name = $"Pavimento #{kv.Key}";

                double? elevation = a.Count > 9 ? ParseNumber(a[9]) : null;
                if (elevation == null && a.Count > 5) elevation = PlacementZ(entities, a[5], 0);

                result.Add(new SourceLevel(name.Trim(), (elevation ?? 0) * toMeters));
            }

            return result.OrderBy(l => l.ElevationM).ToList();
        }

        // ---------- unidades ----------

        private static double LengthUnitToMeters(Dictionary<int, (string Type, string Args)> entities)
        {
            IEnumerable<int> candidates = entities
                .Where(e => e.Value.Type == "IFCUNITASSIGNMENT")
                .SelectMany(e => Regex.Matches(e.Value.Args, @"#(\d+)").Select(m => int.Parse(m.Groups[1].Value)))
                .ToList();
            if (!candidates.Any()) candidates = entities.Keys;

            foreach (int id in candidates)
            {
                if (!entities.TryGetValue(id, out var unit)) continue;
                List<string> a = SplitArgs(unit.Args);

                if (unit.Type == "IFCSIUNIT" && a.Count > 3 && a[1].Trim().Equals(".LENGTHUNIT.", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = a[2].Trim().ToUpperInvariant();
                    return prefix switch
                    {
                        ".MILLI." => 0.001,
                        ".CENTI." => 0.01,
                        ".DECI." => 0.1,
                        ".KILO." => 1000,
                        _ => 1.0,
                    };
                }

                if (unit.Type == "IFCCONVERSIONBASEDUNIT" && a.Count > 2 && a[1].Trim().Equals(".LENGTHUNIT.", StringComparison.OrdinalIgnoreCase))
                {
                    string n = DecodeString(a[2]).ToUpperInvariant();
                    if (n.Contains("FOOT") || n.Contains("FEET")) return 0.3048;
                    if (n.Contains("INCH")) return 0.0254;
                }
            }
            return 0.001; // padrão mais comum em IFC exportado do Revit/ArchiCAD: milímetros
        }

        // ---------- posicionamento ----------

        private static double? PlacementZ(Dictionary<int, (string Type, string Args)> e, string reference, int depth)
        {
            if (depth > 50 || !TryRef(reference, out int id) || !e.TryGetValue(id, out var placement)) return null;
            if (placement.Type != "IFCLOCALPLACEMENT") return null;

            List<string> a = SplitArgs(placement.Args);
            double parentZ = a.Count > 0 ? PlacementZ(e, a[0], depth + 1) ?? 0 : 0;
            double localZ = a.Count > 1 ? AxisZ(e, a[1]) ?? 0 : 0;
            return parentZ + localZ;
        }

        private static double? AxisZ(Dictionary<int, (string Type, string Args)> e, string reference)
        {
            if (!TryRef(reference, out int id) || !e.TryGetValue(id, out var axis)) return null;
            List<string> a = SplitArgs(axis.Args);
            if (a.Count == 0 || !TryRef(a[0], out int pid) || !e.TryGetValue(pid, out var point)) return null;
            if (point.Type != "IFCCARTESIANPOINT") return null;

            string coords = point.Args.Trim().Trim('(', ')');
            string[] parts = coords.Split(',');
            return parts.Length >= 3 ? ParseNumber(parts[2]) : 0;
        }

        // ---------- utilitários STEP ----------

        private static bool TryRef(string token, out int id)
        {
            id = 0;
            token = token?.Trim() ?? "";
            return token.StartsWith("#") && int.TryParse(token.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        private static double? ParseNumber(string token)
        {
            token = token?.Trim() ?? "";
            if (token == "$" || token == "*" || token.Length == 0) return null;
            int paren = token.IndexOf('(');  // ex.: IFCLENGTHMEASURE(3000.)
            if (paren >= 0) token = token.Substring(paren + 1).TrimEnd(')');
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        /// <summary>Divide os argumentos de nível superior, respeitando parênteses e textos entre aspas.</summary>
        private static List<string> SplitArgs(string args)
        {
            var list = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;

            foreach (char c in args)
            {
                if (c == '\'') inString = !inString;
                if (!inString)
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        list.Add(current.ToString());
                        current.Clear();
                        continue;
                    }
                }
                current.Append(c);
            }
            list.Add(current.ToString());
            return list;
        }

        /// <summary>Converte um texto IFC ('...') em string, decodificando \X2\...\X0\, \X\hh e \S\.</summary>
        private static string DecodeString(string token)
        {
            token = token?.Trim() ?? "";
            if (token.Length < 2 || token[0] != '\'' || token[^1] != '\'') return "";
            string s = token.Substring(1, token.Length - 2).Replace("''", "'");

            s = Regex.Replace(s, @"\\X2\\([0-9A-Fa-f]+)\\X0\\", m =>
            {
                string hex = m.Groups[1].Value;
                var sb = new StringBuilder();
                for (int i = 0; i + 4 <= hex.Length; i += 4)
                    sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
                return sb.ToString();
            });
            s = Regex.Replace(s, @"\\X\\([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            s = Regex.Replace(s, @"\\S\\(.)", m => ((char)(m.Groups[1].Value[0] + 128)).ToString());
            return s;
        }
    }
}
