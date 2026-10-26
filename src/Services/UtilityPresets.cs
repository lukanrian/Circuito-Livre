using System.Collections.Generic;
using System.Linq;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Perfis de concessionária prontos para aplicar nas configurações (nome, sistema de tensão e
    /// categorias do padrão de entrada). Os valores vêm de normas da distribuidora; confira a edição
    /// vigente antes de usar em projeto. Editável depois nas Configurações → Concessionária.
    /// </summary>
    public class UtilityPreset
    {
        public string Name { get; init; }
        public string SupplySystem { get; init; }
        /// <summary>
        /// Categorias do padrão de entrada no formato "limite;descrição" (igual ao DimensioningSettings). O limite
        /// aceita o prefixo "kW:" (enquadra por carga instalada) ou "kVA:" (por demanda); sem prefixo é demanda.
        /// </summary>
        public List<string> EntranceCategories { get; init; } = new();

        /// <summary>Aplica o perfil às configurações (marca a concessionária e troca as categorias de entrada).</summary>
        public void ApplyTo(DimensioningSettings s)
        {
            s.RegisterUtility = true;
            s.Utility = Name;
            s.SupplySystem = SupplySystem;
            s.EntranceCategories = EntranceCategories.ToList();
        }

        /// <summary>Perfis disponíveis. Começo com a Neoenergia Brasília (DF).</summary>
        public static readonly List<UtilityPreset> All = new()
        {
            // Neoenergia Brasília — DIS-NOR-030 REV-07, Tabela 2 (sistema 380/220 V) e exemplos da seção 6.27.
            // Monofásico (M2/M3) definido por CARGA INSTALADA (kW); trifásico (T6-T12) por DEMANDA máxima (kVA).
            // A tabela da Brasília é só 380/220 V e não traz categoria bifásica. Fatores de demanda de
            // iluminação/tomadas seguem a Tabela 6 (iguais aos padrões do plugin).
            // O enquadramento respeita o critério de cada linha: M2/M3 pelo prefixo "kW:" (carga instalada) e
            // T6-T12 por "kVA:" (demanda). A carga instalada usada é a soma das cargas em kVA tratada como kW
            // (potência aparente); para fator de potência baixo, confira o enquadramento manualmente.
            new UtilityPreset
            {
                Name = "Neoenergia Brasília (380/220 V)",
                SupplySystem = "380/220V",
                EntranceCategories = new List<string>
                {
                    "kW:10;M2 — Monofásico 380/220 V (carga instalada ≤10 kW), disjuntor 40 A",
                    "kW:15;M3 — Monofásico 380/220 V (carga instalada ≤15 kW), disjuntor 63 A",
                    "kVA:21;T6 — Trifásico 380/220 V (demanda ≤21 kVA), disjuntor 32 A",
                    "kVA:26;T7 — Trifásico 380/220 V (demanda ≤26 kVA), disjuntor 40 A",
                    "kVA:33;T8 — Trifásico 380/220 V (demanda ≤33 kVA), disjuntor 50 A",
                    "kVA:40;T9 — Trifásico 380/220 V (demanda ≤40 kVA), disjuntor 63 A",
                    "kVA:52;T10 — Trifásico 380/220 V (demanda ≤52 kVA), disjuntor 80 A",
                    "kVA:66;T11 — Trifásico 380/220 V (demanda ≤66 kVA), disjuntor 100 A",
                    "kVA:75;T12 — Trifásico 380/220 V (demanda ≤75 kVA), disjuntor 125 A",
                },
            },
        };

        public static UtilityPreset ByName(string name) => All.FirstOrDefault(p => p.Name == name);
    }
}
