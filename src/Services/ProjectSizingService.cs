using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CircuitoLivre.Selection;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Hierarquia de quadros do projeto: carga instalada, demanda (fatores editáveis da concessionária),
    /// corrente do alimentador, curto-circuito estimado em cada quadro e categoria do padrão de entrada.
    /// </summary>
    public class ProjectSizingService
    {
        /// <summary>Sem alimentador é origem; alimentador existente sem painel é uma ligação pendente.</summary>
        public static (List<long> Origins, List<long> Unassigned) AnalyzeSupply(IEnumerable<long> feedingPanels,
            IEnumerable<(long? Source, long Load)> feeders)
        {
            var links = feeders.ToList();
            return (feedingPanels.Distinct().Except(links.Select(e => e.Load)).ToList(),
                links.Where(e => !e.Source.HasValue).Select(e => e.Load).Distinct().ToList());
        }

        public class PanelRow
        {
            public FamilyInstance Panel;
            public string Name;
            public int Depth;
            public string Parent;
            public ElectricalSystem Feeder;
            public double InstalledVa;
            public double LightingTugVa;
            public double TueVa;
            public int TueCount;
            public double DemandVa;
            public double FeederCurrentA;
            public double IccKa;
            public double VoltageV;
            public int Poles;
            public string Status = "";
            public double Factor => InstalledVa > 0 ? DemandVa / InstalledVa : 1;
        }

        private readonly Document _doc;
        private readonly DimensioningSettings _cfg;
        private readonly Dictionary<ElementId, List<ElectricalSystem>> _fed = new();
        private readonly Dictionary<ElementId, ElectricalSystem> _feeder = new();

        public List<PanelRow> Rows { get; } = new();
        public double TotalDemandVa { get; private set; }
        public double TotalInstalledVa { get; private set; }
        public string Entrance { get; private set; }

        public ProjectSizingService(Document doc, DimensioningSettings cfg)
        {
            _doc = doc;
            _cfg = cfg;
        }

        private static double Va(double internalValue) => UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.VoltAmperes);

        public void Run()
        {
            List<FamilyInstance> panels = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(CircuitDeviceFilter.IsPanel).ToList();
            var panelIds = new HashSet<ElementId>(panels.Select(p => p.Id));

            foreach (ElectricalSystem c in new FilteredElementCollector(_doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
            {
                if (c.BaseEquipment == null || c.SystemType != ElectricalSystemType.PowerCircuit) continue;
                if (!_fed.TryGetValue(c.BaseEquipment.Id, out var list)) _fed[c.BaseEquipment.Id] = list = new List<ElectricalSystem>();
                list.Add(c);
                foreach (Element e in c.Elements)
                    if (panelIds.Contains(e.Id)) _feeder[e.Id] = c;
            }

            foreach (FamilyInstance root in panels.Where(p => !_feeder.ContainsKey(p.Id)).OrderBy(p => p.Name))
            {
                double line = ThreePhaseLineVoltage(root);
                Visit(root, 0, null, ShortCircuitEstimate.SourceImpedance(line, _cfg.SourceShortCircuitKa), new HashSet<ElementId>(), line);
            }

            TotalDemandVa = Rows.Where(r => r.Depth == 0).Sum(r => r.DemandVa);
            TotalInstalledVa = Rows.Where(r => r.Depth == 0).Sum(r => r.InstalledVa);
            // Carga instalada em kW aproximada por kVA (potência aparente), para o critério das categorias por kW.
            Entrance = _cfg.EntranceFor(TotalDemandVa / 1000.0, TotalInstalledVa / 1000.0);
        }

        private static double ThreePhaseLineVoltage(FamilyInstance panel)
        {
            var system = (panel.MEPModel as ElectricalEquipment)?.DistributionSystem;
            return system != null && system.ElectricalPhase == ElectricalPhase.ThreePhase && system.VoltageLineToLine != null
                ? UnitUtils.ConvertFromInternalUnits(system.VoltageLineToLine.ActualValue, UnitTypeId.Volts) : double.NaN;
        }

        private double Visit(FamilyInstance panel, int depth, string parent, double upstreamZ, HashSet<ElementId> seen, double sourceVoltage)
        {
            if (!seen.Add(panel.Id)) return 0;
            var row = new PanelRow
            {
                Panel = panel,
                Name = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name,
                Depth = depth,
                Parent = parent,
            };
            Rows.Add(row);
            _feeder.TryGetValue(panel.Id, out row.Feeder);

            // impedância até este quadro (fonte + alimentador)
            double z = upstreamZ;
            if (row.Feeder != null)
            {
                double lengthM = SharedParameters.GetNumber(row.Feeder, SharedParameters.RealLength) ?? RevitHelpers.ToM(row.Feeder.Length);
                double section = SharedParameters.HasValidSizing(row.Feeder) ? SharedParameters.GetNumber(row.Feeder, SharedParameters.Section) ?? 0 : 0;
                if (double.IsFinite(lengthM) && lengthM > 0 && section > 0) z += _cfg.Resistivity * lengthM / section;
                else z = double.NaN;
                row.VoltageV = Safe(() => UnitUtils.ConvertFromInternalUnits(row.Feeder.Voltage, UnitTypeId.Volts));
                row.Poles = Math.Max(1, Safe(() => row.Feeder.PolesNumber));
            }
            if (row.VoltageV <= 0) row.VoltageV = 220;
            if (row.Poles <= 0) row.Poles = 3;
            double actualLine = ThreePhaseLineVoltage(panel);
            if (!double.IsFinite(actualLine) || Math.Abs(actualLine - sourceVoltage) > 0.1 ||
                (row.Feeder != null && row.Poles != 3)) z = double.NaN;
            row.IccKa = ShortCircuitEstimate.CurrentKa(actualLine, z);

            List<ElectricalSystem> circuits = _fed.TryGetValue(panel.Id, out var list) ? list : new List<ElectricalSystem>();
            double subDemand = 0;
            foreach (ElectricalSystem c in circuits)
            {
                FamilyInstance sub = c.Elements.Cast<Element>().Select(e => _doc.GetElement(e.Id)).OfType<FamilyInstance>()
                    .FirstOrDefault(CircuitDeviceFilter.IsPanel);
                if (sub != null)
                {
                    subDemand += Visit(sub, depth + 1, row.Name, z, seen, sourceVoltage);
                    continue;
                }
                double va = Va(c.ApparentLoad);
                row.InstalledVa += va;
                if (IsTue(c, va)) { row.TueVa += va; row.TueCount++; }
                else row.LightingTugVa += va;
            }

            row.DemandVa = LightingDemand(row.LightingTugVa) + TueDemand(row.TueVa, row.TueCount) + subDemand;
            row.InstalledVa += Rows.Where(r => r.Parent == row.Name && r.Depth == depth + 1).Sum(r => r.InstalledVa);
            row.FeederCurrentA = row.Poles >= 3 ? row.DemandVa / (Math.Sqrt(3) * LineVoltage(row.VoltageV)) : row.DemandVa / row.VoltageV;

            var notes = new List<string>();
            if (!double.IsFinite(row.IccKa)) notes.Add("Curto-circuito não verificado: exige rede trifásica, mesma tensão e alimentadores aprovados com comprimento");
            if (row.IccKa > _cfg.BreakerIcuKa) notes.Add($"Icc {row.IccKa:0.0} kA > {_cfg.BreakerIcuKa:0.#} kA dos disjuntores");
            double breaker = row.Feeder != null && SharedParameters.HasValidSizing(row.Feeder) ? (SharedParameters.GetNumber(row.Feeder, SharedParameters.Breaker) ?? 0) : 0;
            if (breaker > 0 && row.FeederCurrentA > breaker) notes.Add($"Corrente {row.FeederCurrentA:0.0} A > disjuntor {breaker:0} A");
            row.Status = notes.Count == 0 ? "OK" : string.Join("; ", notes);
            return row.DemandVa;
        }

        /// <summary>Tensão de linha: se o valor do circuito for de fase (127/220), converte para linha.</summary>
        private static double LineVoltage(double v) => v < 200 ? v * Math.Sqrt(3) : v;

        private bool IsTue(ElectricalSystem c, double va)
        {
            CircuitClassRule rule = _cfg.ClassOf(c.LoadName);
            if (rule != null && (rule.Name.IndexOf("específico", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 rule.Name.IndexOf("motor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 rule.Name.IndexOf("ar", StringComparison.OrdinalIgnoreCase) == 0)) return true;
            if (rule != null) return false;
            return c.Elements.Size == 1 && va >= 1000;
        }

        public double LightingDemand(double va)
        {
            if (va <= 0) return 0;
            double kva = va / 1000.0;
            for (int i = 0; i < _cfg.DemandLightingLimitsKva.Length; i++)
                if (kva <= _cfg.DemandLightingLimitsKva[i]) return va * _cfg.DemandLightingFactors[i];
            return va * _cfg.DemandLightingFactors.Last();
        }

        public double TueDemand(double va, int count)
        {
            if (va <= 0 || count <= 0) return 0;
            double[] f = _cfg.DemandTueFactors;
            return va * f[Math.Min(count, f.Length) - 1];
        }

        private static T Safe<T>(Func<T> f)
        {
            try { return f(); } catch (Exception) { return default; }
        }
    }
}
