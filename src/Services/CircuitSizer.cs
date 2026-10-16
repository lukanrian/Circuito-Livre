using System;
using System.Linq;

namespace RafaelElectrical.Services
{
    /// <summary>Dados de um circuito lidos do Revit (independentes da API, para recalcular rapidamente).</summary>
    public class CircuitInput
    {
        public Autodesk.Revit.DB.ElementId Id { get; set; }
        public string Panel { get; set; }
        public string Number { get; set; }
        public string LoadName { get; set; }
        public double VoltageV { get; set; }
        public int Poles { get; set; }
        public double ApparentLoadVA { get; set; }
        public double CurrentA { get; set; }
        public double LengthM { get; set; }
        public bool IsLighting { get; set; }
        public bool IgnoreGrouping { get; set; }
        /// <summary>Agrupamento real vindo do roteamento (null = usa o valor da janela).</summary>
        public int? GroupingOverride { get; set; }
        /// <summary>True quando o comprimento veio do roteamento pela infraestrutura.</summary>
        public bool RealLength { get; set; }
        /// <summary>Fator de agrupamento do pior trecho, calculado no roteamento pelo tipo de infraestrutura.</summary>
        public double? FcaOverride { get; set; }
        /// <summary>Quadro alimentado por este circuito (alimentador), se houver.</summary>
        public string FeedsPanel { get; set; }
        /// <summary>Queda acumulada a montante do quadro deste circuito (%), preenchida na verificação de queda total.</summary>
        public double UpstreamDropPercent { get; set; }
        /// <summary>Limite de queda deste circuito (%) — null usa o da configuração.</summary>
        public double? DropLimitOverride { get; set; }
    }

    /// <summary>Resultado do dimensionamento de um circuito.</summary>
    public class SizingResult
    {
        public CircuitInput Input { get; set; }

        public string Panel => Input.Panel;
        public string Number => Input.Number;
        public string LoadName => Input.LoadName;
        public double VoltageV => Input.VoltageV;
        public int Poles => Input.Poles;
        public double CurrentA => Input.CurrentA;
        public double LengthM => Input.LengthM;
        public string Grouping => Input.IgnoreGrouping ? "Não"
            : Input.FcaOverride.HasValue ? $"{Input.GroupingOverride} (FCA {Input.FcaOverride:0.00})"
            : Input.GroupingOverride.HasValue ? $"{Input.GroupingOverride} (real)" : "padrão";
        public double TotalDropPercent => Math.Round(Input.UpstreamDropPercent + VoltageDropPercent, 2);
        public double NeutralMm2 { get; set; }
        public string LengthSource => Input.RealLength ? "infraestrutura" : "Revit";

        public double SectionMm2 { get; set; }
        public double ProtectionMm2 { get; set; }
        public int BreakerA { get; set; }
        public double IzA { get; set; }
        public double VoltageDropPercent { get; set; }
        public string Criterion { get; set; }
        public string Status { get; set; }
        public string Wiring { get; set; }
        public bool Ok { get; set; }

        public string SectionText => SectionMm2 > 0 ? Nbr5410.FormatSection(SectionMm2) : "—";
    }

    /// <summary>
    /// Dimensionamento de circuito terminal (cobre) pela NBR 5410:
    /// Ib ≤ In ≤ Iz (Iz = Itab × FCT × FCA), seção mínima (Tabela 47) e queda de tensão máxima.
    /// Com disjuntores termomagnéticos (I2 = 1,45 In) a condição I2 ≤ 1,45 Iz fica atendida quando In ≤ Iz.
    /// </summary>
    public static class CircuitSizer
    {
        public static SizingResult Size(CircuitInput c, DimensioningSettings s)
        {
            var r = new SizingResult { Input = c };

            if (c.VoltageV <= 0)
            {
                r.Status = "Sem tensão definida";
                return r;
            }

            int loaded = c.Poles >= 3 ? 3 : 2;
            double fct = Nbr5410.TemperatureFactor(s.Method, s.Insulation, s.TemperatureC);
            double fca = c.IgnoreGrouping ? 1.0
                : c.FcaOverride ?? s.GroupingFactor(GroupingKind.Closed, c.GroupingOverride ?? s.GroupedCircuits);

            double ib = Math.Max(c.CurrentA, 0);
            double minSection = c.IsLighting ? s.MinSectionLighting : s.MinSectionPower;
            double dropFactor = c.Poles >= 3 ? Math.Sqrt(3) : 2.0;
            double dropLimit = c.DropLimitOverride ?? s.MaxVoltageDropPercent;

            double Drop(double section) =>
                c.LengthM > 0 ? dropFactor * s.Resistivity * c.LengthM * ib / section / c.VoltageV * 100.0 : 0.0;
            double Iz(double section) =>
                Nbr5410.Ampacity(s.Method, s.Insulation, section, loaded) * fct * fca;

            double[] candidates = Nbr5410.Sections.Where(x => x >= minSection - 1e-9 && x <= s.MaxSection + 1e-9).ToArray();
            string maxText = Nbr5410.FormatSection(s.MaxSection);

            int breaker;
            double sAmp;
            if (s.BreakerMethod == BreakerMethod.DesignCurrent)
            {
                // In ≥ Ib e Iz ≥ In
                breaker = Nbr5410.BreakerRatings.FirstOrDefault(b => b >= ib - 1e-9);
                if (breaker == 0) { r.Status = "Corrente acima dos disjuntores da lista"; return r; }
                sAmp = candidates.FirstOrDefault(x => Iz(x) >= breaker);
            }
            else
            {
                // Iz ≥ Ib e escolhe o maior disjuntor com Ib ≤ In ≤ Iz
                sAmp = 0;
                breaker = 0;
                foreach (double x in candidates)
                {
                    double iz = Iz(x);
                    if (iz < ib) continue;
                    int b = Nbr5410.BreakerRatings.Where(v => v >= ib - 1e-9 && v <= iz + 1e-9).DefaultIfEmpty(0).Max();
                    if (b == 0) continue;
                    sAmp = x;
                    breaker = b;
                    break;
                }
            }

            double sDrop = candidates.FirstOrDefault(x => Drop(x) <= dropLimit + 1e-9);

            if (sAmp == 0 || sDrop == 0)
            {
                r.BreakerA = breaker;
                r.Status = sAmp == 0 ? $"Nenhuma seção até {maxText} mm² atende à corrente"
                                     : $"Nenhuma seção até {maxText} mm² atende à queda de {dropLimit:0.##} %";
                return r;
            }

            double section = Math.Max(sAmp, sDrop);
            r.SectionMm2 = section;
            r.ProtectionMm2 = Nbr5410.ProtectionSection(section);
            r.NeutralMm2 = c.Poles >= 3 && s.ReducedNeutral ? Nbr5410.ReducedNeutral(section) : section;
            r.BreakerA = breaker;
            r.IzA = Math.Round(Iz(section), 1);
            r.VoltageDropPercent = Math.Round(Drop(section), 2);
            r.Criterion = section <= minSection + 1e-9 ? "Seção mínima"
                        : sDrop > sAmp ? (c.DropLimitOverride.HasValue ? "Queda de tensão total" : "Queda de tensão")
                        : "Capacidade de corrente";
            r.Wiring = WiringText(c.Poles, section, r.NeutralMm2, r.ProtectionMm2);
            r.Ok = true;
            r.Status = c.LengthM > 0 ? "OK" : "OK (sem comprimento: queda não verificada)";
            return r;
        }

        public static string WiringText(int poles, double phase, double pe) => WiringText(poles, phase, phase, pe);

        public static string WiringText(int poles, double phase, double neutral, double pe)
        {
            if (poles >= 3 && Math.Abs(neutral - phase) > 1e-9)
                return $"3F #{Nbr5410.FormatSection(phase)} + N #{Nbr5410.FormatSection(neutral)} + PE #{Nbr5410.FormatSection(pe)} mm²";
            string conductors = poles >= 3 ? "3F+N" : poles == 2 ? "2F" : "F+N";
            string ph = Nbr5410.FormatSection(phase);
            string p = Nbr5410.FormatSection(pe);
            return Math.Abs(phase - pe) < 1e-9
                ? $"{conductors}+PE #{ph} mm²"
                : $"{conductors} #{ph} mm² + PE #{p} mm²";
        }
    }
}
