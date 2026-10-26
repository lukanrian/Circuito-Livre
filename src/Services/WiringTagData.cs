using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitoLivre.Services
{
    public static class WiringTagData
    {
        public record Mark(string Role, double Section);

        public static List<Mark> Parse(string wiring)
        {
            var result = new List<Mark>();
            const string pattern = @"(?<roles>(?:\d*(?:PE|F|N|T|R)\s*(?:\+\s*)?)+)\s*(?<parallel>\d*)#\s*(?<section>[\d.,]+)";
            string remaining = Regex.Replace(wiring ?? "", pattern, "", RegexOptions.IgnoreCase);
            if (Regex.Replace(remaining, @"mm[²2]|\s|\+", "", RegexOptions.IgnoreCase).Length != 0) return result;
            foreach (Match group in Regex.Matches(wiring ?? "", pattern, RegexOptions.IgnoreCase))
            {
                if (!double.TryParse(group.Groups["section"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double section) || !double.IsFinite(section) || section <= 0) return new();
                if (!int.TryParse(group.Groups["parallel"].Length == 0 ? "1" : group.Groups["parallel"].Value, out int parallel)) return new();
                foreach (Match role in Regex.Matches(group.Groups["roles"].Value, @"(?<count>\d*)(?<role>PE|F|N|T|R)", RegexOptions.IgnoreCase))
                {
                    if (!int.TryParse(role.Groups["count"].Length == 0 ? "1" : role.Groups["count"].Value, out int count) ||
                        count < 1 || parallel < 1 || count > 64 || parallel > 64 || result.Count + (long)count * parallel > 64) return new();
                    string kind = role.Groups["role"].Value.ToUpperInvariant();
                    for (int i = 0; i < count * parallel; i++) result.Add(new(kind == "PE" ? "T" : kind, section));
                }
            }
            return result;
        }

        public static void AssignNumbers(Dictionary<string, int> numbers, IEnumerable<string> orderedIds)
        {
            var used = new HashSet<int>();
            foreach (string key in numbers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                if (numbers[key] <= 0 || !used.Add(numbers[key])) numbers.Remove(key);
            int next = used.DefaultIfEmpty(0).Max();
            foreach (string id in orderedIds.Distinct())
                if (!numbers.ContainsKey(id)) numbers[id] = checked(++next);
        }
    }
}
