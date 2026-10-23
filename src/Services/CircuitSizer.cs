using System;
using System.Linq;

namespace CircuitoLivre.Services
{
    /// <summary>Dados de um circuito lidos do Revit (independentes da API, para recalcular rapidamente).</summary>
    public class CircuitInput
    {
        public Autodesk.Revit.DB.ElementId Id { get; set; }
        public string PanelKey { get; set; }
        public string FeedsPanelKey { get; set; }
        public double? ExistingSectionMm2 { get; set; }
        public bool ExistingSizingValid { get; set; }
        public string Panel { get; set; }
        public string Number { get; set; }
        public string LoadName { get; set; }
        public string FamilyNames { get; set; }
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
        /// <summary>Seção comercial adotada pelo projetista (força o dimensionamento e reverifica); null = usa a calculada.</summary>
        public double? AdoptedSectionMm2 { get; set; }
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
        public double TotalDropPercent => Input.UpstreamDropPercent + VoltageDropPercent;
        public double NeutralMm2 { get; set; }
        public string LengthSource => Input.RealLength ? "infraestrutura" : "Revit";

        /// <summary>Condutores por fase em paralelo (1 = condutor único).</summary>
        public int ConductorsPerPhase { get; set; } = 1;

        public double SectionMm2 { get; set; }
        public double ProtectionMm2 { get; set; }
        public int BreakerA { get; set; }
        public double IzA { get; set; }
        public double VoltageDropPercent { get; set; }
        public string Criterion { get; set; }
        public string Status { get; set; }
        public string Wiring { get; set; }
        public bool Ok { get; set; }

        /// <summary>Material do condutor usado neste circuito (define as seções comerciais adotáveis).</summary>
        public ConductorMaterial Material { get; set; } = ConductorMaterial.Copper;

        /// <summary>Seção que o cálculo livre (NBR) selecionaria, independentemente da seção adotada.</summary>
        public double CalculatedSectionMm2 { get; set; }

        /// <summary>Seção adotada pelo projetista (null = usa a calculada).</summary>
        public double? AdoptedSectionMm2 => Input?.AdoptedSectionMm2;

        public bool IsAdopted => AdoptedSectionMm2.HasValue;
        public bool AdoptedDiffers => AdoptedSectionMm2.HasValue && Math.Abs(AdoptedSectionMm2.Value - CalculatedSectionMm2) > 1e-9;

        public string SectionText => SectionMm2 > 0 ? Nbr5410.FormatSection(SectionMm2) : "—";
        public string CalculatedText => CalculatedSectionMm2 > 0 ? Nbr5410.FormatSection(CalculatedSectionMm2) : "—";

        /// <summary>Opção "sem adoção" mostrada no seletor da coluna Adotada.</summary>
        public const string AdoptNone = "— (calculado)";

        /// <summary>Seções comerciais que o projetista pode adotar (depende do material), com a opção de limpar.</summary>
        public System.Collections.Generic.IEnumerable<string> AdoptChoices =>
            new[] { AdoptNone }.Concat(Nbr5410.SectionsFor(Material).Select(Nbr5410.FormatSection));

        /// <summary>Chamado quando o projetista muda a seção adotada (a UI usa para reverificar o circuito).</summary>
        public Action OnAdoptChanged;

        /// <summary>Seleção atual do seletor de adoção (lê/grava em Input.AdoptedSectionMm2).</summary>
        public string AdoptSelection
        {
            get => Input != null && Input.AdoptedSectionMm2.HasValue ? Nbr5410.FormatSection(Input.AdoptedSectionMm2.Value) : AdoptNone;
            set
            {
                if (Input == null) return;
                double? previous = Input.AdoptedSectionMm2;
                Input.AdoptedSectionMm2 = string.IsNullOrEmpty(value) || value == AdoptNone
                    ? (double?)null
                    : Nbr5410.SectionsFor(Material).Cast<double?>().FirstOrDefault(x => Nbr5410.FormatSection(x.Value) == value);
                if (previous != Input.AdoptedSectionMm2) OnAdoptChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Dimensionamento de circuito terminal (cobre) pela NBR 5410:
    /// Ib ≤ In ≤ Iz (Iz = Itab × FCT × FCA), seção mínima (Tabela 47) e queda de tensão máxima.
    /// Com disjuntores termomagnéticos (I2 = 1,45 In) a condição I2 ≤ 1,45 Iz fica atendida quando In ≤ Iz.
    /// </summary>
    public static class CircuitSizer
    {
        public static SizingResult Size(CircuitInput c, DimensioningSettings s, double? fixedSection = null)
        {
            var r = new SizingResult { Input = c };

            try { s.ValidateForSizing(); }
            catch (ArgumentException ex) { r.Status = ex.Message; return r; }
            if (!double.IsFinite(c.CurrentA) || c.CurrentA < 0 || !double.IsFinite(c.LengthM) || c.LengthM < 0 ||
                !double.IsFinite(c.VoltageV) || c.Poles < 1 || c.Poles > 3 ||
                (c.FcaOverride.HasValue && (!double.IsFinite(c.FcaOverride.Value) || c.FcaOverride <= 0 || c.FcaOverride > 1)) ||
                (c.GroupingOverride.HasValue && c.GroupingOverride < 1) ||
                !double.IsFinite(c.UpstreamDropPercent) || c.UpstreamDropPercent < 0 ||
                (c.DropLimitOverride.HasValue && (!double.IsFinite(c.DropLimitOverride.Value) || c.DropLimitOverride <= 0)))
            { r.Status = "Dados elétricos inválidos"; return r; }
            if (c.VoltageV <= 0)
            {
                r.Status = "Sem tensão definida";
                return r;
            }

            int loaded = c.Poles >= 3 ? 3 : 2;
            CircuitDocumentationRule documentation = s.DocumentationFor(c.LoadName, c.Poles);
            Insulation insulation = documentation == null ? s.Insulation :
                documentation.Insulation == "XLPE" ? Insulation.EPR90 : Insulation.PVC70;
            ConductorMaterial material = documentation != null && documentation.Material == "Alumínio"
                ? ConductorMaterial.Aluminum : s.Material;
            double resistivity = material == ConductorMaterial.Aluminum ? s.ResistivityAluminum : s.Resistivity;
            double fct = Nbr5410.TemperatureFactor(s.Method, insulation, s.TemperatureC);
            double fca = c.IgnoreGrouping ? 1.0
                : c.FcaOverride ?? s.GroupingFactor(GroupingKind.Closed, c.GroupingOverride ?? s.GroupedCircuits);

            double ib = Math.Max(c.CurrentA, 0);
            CircuitClassRule rule = s.ClassOf(c.LoadName);
            double minSection = Math.Max(c.IsLighting ? s.MinSectionLighting : s.MinSectionPower, rule?.MinimumSectionMm2 ?? 0);
            if (material == ConductorMaterial.Aluminum) minSection = Math.Max(minSection, 16); // NBR 5410 6.2.6.1
            double minBreaker = Math.Max(ib, rule?.MinimumBreakerA ?? 0);
            foreach (SizingRule special in s.SizingRules.Where(x => x.Matches(c)))
            {
                minSection = Math.Max(minSection, special.MinimumSectionMm2);
                minBreaker = Math.Max(minBreaker, special.MinimumBreakerA);
            }
            double dropFactor = c.Poles >= 3 ? Math.Sqrt(3) : 2.0;
            double dropLimit = c.DropLimitOverride ?? s.MaxVoltageDropPercent;

            r.Material = material;
            double[] baseCandidates = Nbr5410.SectionsFor(material).Where(x => x >= minSection - 1e-9 && x <= s.MaxSection + 1e-9).ToArray();
            double[] usedCandidates = fixedSection.HasValue
                ? baseCandidates.Where(x => Math.Abs(x - fixedSection.Value) < 1e-9).ToArray() : baseCandidates;
            string maxText = Nbr5410.FormatSection(s.MaxSection);

            // Condutores em paralelo (NBR 5410 6.2.5): usa N condutores por fase quando um único condutor
            // até a seção máxima não atende. A queda cai com 1/N; a capacidade sobe com N, mas derata pelo
            // agrupamento dos N condutores do feixe.
            // shortcut: derating de paralelo = FCA × GroupingFactor(N) (conservador), não a regra de feixe por seção. Revisar antes de produção.
            int maxN = s.MultipleConductorsNcf ? Math.Max(1, s.MaxConductorsPerPhase) : 1;

            double DropSingle(double section) =>
                c.LengthM > 0 ? dropFactor * resistivity * c.LengthM * ib / section / c.VoltageV * 100.0 : 0.0;
            double AmpBase(double section) => Nbr5410.Ampacity(s.Method, insulation, section, loaded, material) * fct;

            // Com o critério "pela corrente de projeto" o disjuntor independe da seção: falha cedo se Ib passa da lista.
            if (s.BreakerMethod == BreakerMethod.DesignCurrent &&
                Nbr5410.BreakerRatings.FirstOrDefault(x => x >= minBreaker - 1e-9) == 0)
            { r.Status = "Corrente acima dos disjuntores da lista"; return r; }

            // Seleciona a menor seção (e nº de condutores) que atende corrente e queda, na lista de candidatas dada.
            (bool found, double section, int conductors, int breaker, double iz, double drop, double sAmp, double sDrop) Pick(double[] cands)
            {
                double sAmpLast = 0, sDropLast = 0;
                int bLast = 0;
                for (int n = 1; n <= maxN; n++)
                {
                    double fcaN = fca * (n == 1 ? 1.0 : s.GroupingFactor(GroupingKind.Closed, n));
                    double Iz(double section) => n * AmpBase(section) * fcaN;
                    double Drop(double section) => DropSingle(section) / n;

                    double sAmp;
                    int b;
                    if (s.BreakerMethod == BreakerMethod.DesignCurrent)
                    {
                        // In ≥ Ib e N·Iz ≥ In
                        b = Nbr5410.BreakerRatings.FirstOrDefault(x => x >= minBreaker - 1e-9);
                        sAmp = cands.FirstOrDefault(x => Iz(x) >= b);
                    }
                    else
                    {
                        // N·Iz ≥ Ib e escolhe o maior disjuntor com Ib ≤ In ≤ N·Iz
                        sAmp = 0;
                        b = 0;
                        foreach (double x in cands)
                        {
                            double iz = Iz(x);
                            if (iz < ib) continue;
                            int bb = Nbr5410.BreakerRatings.Where(v => v >= minBreaker - 1e-9 && v <= iz + 1e-9).DefaultIfEmpty(0).Max();
                            if (bb == 0) continue;
                            sAmp = x;
                            b = bb;
                            break;
                        }
                    }
                    double sDrop = cands.FirstOrDefault(x => Drop(x) <= dropLimit + 1e-9);
                    sAmpLast = sAmp; sDropLast = sDrop; bLast = b;

                    if (sAmp > 0 && sDrop > 0)
                    {
                        double sec = Math.Max(sAmp, sDrop);
                        return (true, sec, n, b, Math.Round(Iz(sec), 1), Drop(sec), sAmp, sDrop);
                    }
                }
                return (false, 0, 1, bLast, 0, 0, sAmpLast, sDropLast);
            }

            var free = Pick(baseCandidates);
            r.CalculatedSectionMm2 = free.found ? free.section : 0;
            var used = fixedSection.HasValue ? Pick(usedCandidates) : free;

            if (!used.found)
            {
                r.BreakerA = used.breaker;
                string ate = maxN > 1 ? $" (até {maxN}× em paralelo)" : "";
                if (fixedSection.HasValue)
                {
                    string sec = Nbr5410.FormatSection(fixedSection.Value);
                    r.Status = used.sAmp == 0 && used.sDrop == 0 ? $"Seção fixada {sec} mm² não atende (corrente e queda)"
                             : used.sAmp == 0 ? $"Seção fixada {sec} mm² não atende à corrente"
                                              : $"Seção fixada {sec} mm² não atende à queda de {dropLimit:0.##} %";
                }
                else
                    r.Status = used.sAmp == 0 ? $"Nenhuma seção até {maxText} mm²{ate} atende à corrente"
                                             : $"Nenhuma seção até {maxText} mm² atende à queda de {dropLimit:0.##} %";
                return r;
            }

            double section = used.section;
            int conductors = used.conductors;
            r.ConductorsPerPhase = conductors;
            r.SectionMm2 = section;
            CableSectionRule cableRule = s.SectionRule(section);
            r.ProtectionMm2 = cableRule?.Ground ?? Nbr5410.ProtectionSection(section);
            r.NeutralMm2 = c.Poles >= 3 && s.ReducedNeutral
                ? cableRule?.Neutral ?? Nbr5410.ReducedNeutral(section) : section;
            r.BreakerA = used.breaker;
            r.IzA = used.iz;
            r.VoltageDropPercent = used.drop;
            r.Criterion = fixedSection.HasValue ? (r.AdoptedDiffers ? "Seção adotada" : "Seção fixada")
                        : (section <= minSection + 1e-9 && conductors == 1) ? "Seção mínima"
                        : used.sDrop > used.sAmp ? (c.DropLimitOverride.HasValue ? "Queda de tensão total" : "Queda de tensão")
                        : "Capacidade de corrente";
            r.Wiring = WiringText(c.Poles, conductors, section, r.NeutralMm2, r.ProtectionMm2);
            r.Ok = true;
            r.Status = c.LengthM > 0 ? "OK" : "OK (sem comprimento: queda não verificada)";
            return r;
        }

        public static string WiringText(int poles, double phase, double pe) => WiringText(poles, 1, phase, phase, pe);

        public static string WiringText(int poles, double phase, double neutral, double pe) => WiringText(poles, 1, phase, neutral, pe);

        public static string WiringText(int poles, int conductors, double phase, double neutral, double pe)
        {
            int n = Math.Max(1, conductors);
            int nGround = Math.Max(1, (int)Math.Ceiling(n / 2.0)); // terra = metade do N.C.F
            string cf = n > 1 ? n + "#" : "#";
            string cg = nGround > 1 ? nGround + "#" : "#";
            string ph = Nbr5410.FormatSection(phase);
            string p = Nbr5410.FormatSection(pe);
            if (poles >= 3 && Math.Abs(neutral - phase) > 1e-9)
                return $"3F {cf}{ph} + N {cf}{Nbr5410.FormatSection(neutral)} + PE {cg}{p} mm²";
            string label = poles >= 3 ? "3F+N" : poles == 2 ? "2F" : "F+N";
            return Math.Abs(phase - pe) < 1e-9 && n == 1
                ? $"{label}+PE #{ph} mm²"
                : $"{label} {cf}{ph} mm² + PE {cg}{p} mm²";
        }
    }
}
