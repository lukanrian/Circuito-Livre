using System.Linq;

namespace CircuitoLivre.Services
{
    /// <summary>Monta o texto de identificação a partir de um padrão com campos entre chaves.</summary>
    public static class IdentificationFormatter
    {
        public static string CircuitLabel(string panel, string circuit, string description)
        {
            string label = $"{(string.IsNullOrWhiteSpace(panel) ? "?" : panel.Trim())} · Circuito {(string.IsNullOrWhiteSpace(circuit) ? "?" : circuit.Trim())}";
            return string.IsNullOrWhiteSpace(description) ? label : label + " — " + description.Trim();
        }

        public static string Format(string pattern, string panel, string circuit, string description, string level)
        {
            pattern ??= string.Empty;
            circuit ??= string.Empty;
            string first = new string(circuit.TakeWhile(char.IsDigit).ToArray());
            bool numeric = int.TryParse(first, out int n);

            return pattern
                .Replace("{QUADRO}", panel ?? string.Empty)
                .Replace("{CIRCUITO2}", numeric ? n.ToString("00") + circuit.Substring(first.Length) : circuit)
                .Replace("{CIRCUITO3}", numeric ? n.ToString("000") + circuit.Substring(first.Length) : circuit)
                .Replace("{CIRCUITO}", circuit)
                .Replace("{DESCRICAO}", description ?? string.Empty)
                .Replace("{NIVEL}", level ?? string.Empty)
                .Trim();
        }
    }
}
