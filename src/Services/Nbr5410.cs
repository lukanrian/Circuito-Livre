using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitoLivre.Services
{
    public enum InstallMethod { A1, A2, B1, B2, C, D }

    public enum Insulation { PVC70, EPR90 }

    public enum ConductorMaterial { Copper, Aluminum }

    /// <summary>Tipo de infraestrutura para o fator de agrupamento (NBR 5410, Tabela 42).</summary>
    public enum GroupingKind { Closed, SmoothTray, PerforatedTray, Ladder }

    /// <summary>
    /// Tabelas da ABNT NBR 5410:2004 usadas no dimensionamento de condutores de cobre.
    /// Tabela 36 (PVC 70 °C) e 37 (EPR/XLPE 90 °C): capacidade de condução de corrente (A),
    /// para 2 e 3 condutores carregados, temperatura de referência 30 °C (ar) / 20 °C (solo, método D).
    /// Tabela 40: fatores de temperatura. Tabela 42: fatores de agrupamento (feixe/eletroduto).
    /// Tabela 47: seções mínimas. Tabela 58: condutor de proteção.
    /// Confira os valores com a sua edição da norma antes de usar em projeto.
    /// </summary>
    public static class Nbr5410
    {
        public static readonly double[] Sections =
            { 0.5, 0.75, 1, 1.5, 2.5, 4, 6, 10, 16, 25, 35, 50, 70, 95, 120, 150, 185, 240, 300 };

        // [método] -> pares (2 carregados, 3 carregados) na ordem de Sections
        private static readonly Dictionary<InstallMethod, double[,]> Pvc = new()
        {
            [InstallMethod.A1] = new double[,] { {7,7},{9,9},{11,10},{14.5,13.5},{19.5,18},{26,24},{34,31},{46,42},{61,56},{80,73},{99,89},{119,108},{151,136},{182,164},{210,188},{240,216},{273,245},{321,286},{367,328} },
            [InstallMethod.A2] = new double[,] { {7,7},{9,9},{11,10},{14,13},{18.5,17.5},{25,23},{32,29},{43,39},{57,52},{75,68},{92,83},{110,99},{139,125},{167,150},{192,172},{219,196},{248,223},{291,261},{334,298} },
            [InstallMethod.B1] = new double[,] { {9,8},{11,10},{14,12},{17.5,15.5},{24,21},{32,28},{41,36},{57,50},{76,68},{101,89},{125,110},{151,134},{192,171},{232,207},{269,239},{309,275},{353,314},{415,370},{477,426} },
            [InstallMethod.B2] = new double[,] { {9,8},{11,10},{13,12},{16.5,15},{23,20},{30,27},{38,34},{52,46},{69,62},{90,80},{111,99},{133,118},{168,149},{201,179},{232,206},{265,236},{300,268},{351,313},{401,358} },
            [InstallMethod.C]  = new double[,] { {10,9},{13,11},{15,14},{19.5,17.5},{27,24},{36,32},{46,41},{63,57},{85,76},{112,96},{138,119},{168,144},{213,184},{258,223},{299,259},{344,299},{392,341},{461,403},{530,464} },
            [InstallMethod.D]  = new double[,] { {12,10},{15,12},{18,15},{22,18},{29,24},{38,31},{47,39},{63,52},{81,67},{104,86},{125,103},{148,122},{183,151},{216,179},{246,203},{278,230},{312,258},{361,297},{408,336} },
        };

        private static readonly Dictionary<InstallMethod, double[,]> Epr = new()
        {
            [InstallMethod.A1] = new double[,] { {10,9},{12,11},{15,13},{19,17},{26,23},{35,31},{45,40},{61,54},{81,73},{106,95},{131,117},{158,141},{200,179},{241,216},{278,249},{318,285},{362,324},{424,380},{486,435} },
            [InstallMethod.A2] = new double[,] { {10,9},{12,11},{14,13},{18.5,16.5},{25,22},{33,30},{42,38},{57,51},{76,68},{99,89},{121,109},{145,130},{183,164},{220,197},{253,227},{290,259},{329,295},{386,346},{442,396} },
            [InstallMethod.B1] = new double[,] { {12,10},{15,13},{18,16},{23,20},{31,28},{42,37},{54,48},{75,66},{100,88},{133,117},{164,144},{198,175},{253,222},{306,269},{354,312},{407,358},{464,408},{546,481},{628,553} },
            [InstallMethod.B2] = new double[,] { {11,10},{15,13},{17,15},{22,19.5},{30,26},{40,35},{51,44},{69,60},{91,80},{119,105},{146,128},{175,154},{221,194},{265,233},{305,268},{349,307},{395,348},{462,407},{529,465} },
            [InstallMethod.C]  = new double[,] { {12,11},{16,14},{19,17},{24,22},{33,30},{45,40},{58,52},{80,71},{107,96},{138,119},{171,147},{209,179},{269,229},{328,278},{382,322},{441,371},{506,424},{599,500},{693,576} },
            [InstallMethod.D]  = new double[,] { {14,12},{18,15},{21,17},{26,22},{34,29},{44,37},{56,46},{73,61},{95,79},{121,101},{146,122},{173,144},{213,178},{252,211},{287,240},{324,271},{363,304},{419,351},{474,396} },
        };

        // ============================ ALUMÍNIO ============================
        // Condutores de ALUMÍNIO — ABNT NBR 5410:2004, Tabela 36 (PVC 70 °C) e Tabela 37 (EPR/XLPE 90 °C),
        // blocos de alumínio, colunas (2 e 3 condutores carregados) dos métodos A1, A2, B1, B2, C e D.
        // Seções 16–300 mm². Seção mínima do alumínio em potência = 16 mm² (NBR 5410 6.2.6.1).
        public static readonly double[] AluminumSections = { 16, 25, 35, 50, 70, 95, 120, 150, 185, 240, 300 };

        private static readonly Dictionary<InstallMethod, double[,]> AlPvc = new()
        {
            [InstallMethod.A1] = new double[,] { {48,43},{63,57},{77,70},{93,84},{118,107},{142,129},{164,149},{189,170},{215,194},{252,227},{289,261} },
            [InstallMethod.A2] = new double[,] { {44,41},{58,53},{71,65},{86,78},{108,98},{130,118},{150,135},{172,155},{195,176},{229,207},{263,237} },
            [InstallMethod.B1] = new double[,] { {60,53},{79,70},{97,86},{118,104},{150,133},{181,161},{210,186},{241,214},{275,245},{324,288},{372,331} },
            [InstallMethod.B2] = new double[,] { {54,48},{71,62},{86,77},{104,92},{131,116},{157,139},{181,160},{206,183},{234,208},{274,243},{313,278} },
            [InstallMethod.C]  = new double[,] { {66,59},{83,73},{103,90},{125,110},{160,140},{195,170},{226,197},{261,227},{298,259},{352,305},{406,351} },
            [InstallMethod.D]  = new double[,] { {62,52},{80,66},{96,80},{113,94},{140,117},{166,138},{189,157},{213,178},{240,200},{277,230},{313,260} },
        };

        private static readonly Dictionary<InstallMethod, double[,]> AlEpr = new()
        {
            [InstallMethod.A1] = new double[,] { {64,58},{84,76},{103,94},{125,113},{158,142},{191,171},{220,197},{253,226},{288,256},{338,300},{387,344} },
            [InstallMethod.A2] = new double[,] { {60,55},{78,71},{96,87},{115,104},{145,131},{175,157},{201,180},{230,206},{262,233},{307,273},{352,313} },
            [InstallMethod.B1] = new double[,] { {79,71},{105,93},{130,116},{157,140},{200,179},{242,217},{281,251},{323,289},{368,330},{433,389},{499,447} },
            [InstallMethod.B2] = new double[,] { {72,64},{94,84},{115,103},{138,124},{175,156},{210,188},{242,216},{277,248},{314,281},{368,329},{421,377} },
            [InstallMethod.C]  = new double[,] { {84,76},{101,90},{126,112},{154,136},{198,174},{241,211},{280,245},{324,283},{371,323},{439,382},{508,440} },
            [InstallMethod.D]  = new double[,] { {73,61},{93,78},{112,94},{132,112},{163,138},{193,164},{220,186},{249,210},{279,236},{322,272},{364,308} },
        };

        /// <summary>Capacidade de condução tabelada (A). Padrão cobre; alumínio usa Tab. 36/37 (seção ≥ 16 mm²).</summary>
        public static double Ampacity(InstallMethod method, Insulation insulation, double section, int loadedConductors,
            ConductorMaterial material = ConductorMaterial.Copper)
        {
            int col = loadedConductors >= 3 ? 1 : 0;
            if (material == ConductorMaterial.Aluminum)
            {
                int ai = Array.IndexOf(AluminumSections, section);
                if (ai < 0) throw new ArgumentException($"Seção {section} mm² fora da tabela de alumínio (16–300 mm²).");
                return (insulation == Insulation.EPR90 ? AlEpr : AlPvc)[method][ai, col];
            }
            int i = Array.IndexOf(Sections, section);
            if (i < 0) throw new ArgumentException($"Seção {section} mm² fora da tabela.");
            return (insulation == Insulation.EPR90 ? Epr : Pvc)[method][i, col];
        }

        /// <summary>Seções candidatas pelo material (cobre = todas; alumínio ≥ 16 mm²).</summary>
        public static double[] SectionsFor(ConductorMaterial material) =>
            material == ConductorMaterial.Aluminum ? AluminumSections : Sections;

        /// <summary>Tabela 40 — fator de correção de temperatura (ar para A–C, solo para D).</summary>
        public static double TemperatureFactor(InstallMethod method, Insulation insulation, double temperatureC)
        {
            if (!Enum.IsDefined(method) || !Enum.IsDefined(insulation) ||
                !double.IsFinite(temperatureC) || temperatureC < 10 || temperatureC > (method == InstallMethod.D ? 50 : 60))
                throw new ArgumentException("Temperatura fora da tabela implementada: 10–50 °C no solo ou 10–60 °C no ar.");
            (double T, double F)[] table;
            if (method == InstallMethod.D)
                table = insulation == Insulation.EPR90
                    ? new[] { (10.0, 1.07), (15.0, 1.04), (20.0, 1.00), (25.0, 0.96), (30.0, 0.93), (35.0, 0.89), (40.0, 0.85), (45.0, 0.80), (50.0, 0.76) }
                    : new[] { (10.0, 1.10), (15.0, 1.05), (20.0, 1.00), (25.0, 0.95), (30.0, 0.89), (35.0, 0.84), (40.0, 0.77), (45.0, 0.71), (50.0, 0.63) };
            else
                table = insulation == Insulation.EPR90
                    ? new[] { (10.0, 1.15), (15.0, 1.12), (20.0, 1.08), (25.0, 1.04), (30.0, 1.00), (35.0, 0.96), (40.0, 0.91), (45.0, 0.87), (50.0, 0.82), (55.0, 0.76), (60.0, 0.71) }
                    : new[] { (10.0, 1.22), (15.0, 1.17), (20.0, 1.12), (25.0, 1.06), (30.0, 1.00), (35.0, 0.94), (40.0, 0.87), (45.0, 0.79), (50.0, 0.71), (55.0, 0.61), (60.0, 0.50) };

            // usa a linha igual ou imediatamente superior (a favor da segurança)
            foreach (var row in table)
                if (temperatureC <= row.T + 1e-9) return row.F;
            return table[^1].F;
        }

        /// <summary>Tabela 42 — fator de agrupamento para circuitos em feixe / no mesmo eletroduto.</summary>
        public static double GroupingFactor(int circuits)
        {
            if (circuits <= 1) return 1.00;
            return circuits switch
            {
                2 => 0.80,
                3 => 0.70,
                4 => 0.65,
                5 => 0.60,
                6 => 0.57,
                7 => 0.54,
                8 => 0.52,
                <= 11 => 0.50,
                <= 15 => 0.45,
                <= 19 => 0.41,
                _ => 0.38,
            };
        }

        /// <summary>Tabela 58 — seção mínima do condutor de proteção (mesmo material da fase).</summary>
        public static double ProtectionSection(double phase)
        {
            if (phase <= 16) return phase;
            if (phase <= 35) return 16;
            double half = phase / 2;
            return Sections.First(s => s >= half - 1e-9);
        }

        /// <summary>
        /// Tabela 48 — seção reduzida do neutro em circuitos trifásicos com fase acima de 25 mm²
        /// (só quando a taxa de 3ª harmônica e o desequilíbrio permitem; caso contrário neutro = fase).
        /// </summary>
        public static double ReducedNeutral(double phase) => phase switch
        {
            <= 25 => phase,
            35 => 25, 50 => 25, 70 => 35, 95 => 50, 120 => 70, 150 => 70, 185 => 95, 240 => 120, 300 => 150,
            _ => phase,
        };

        /// <summary>Fatores de agrupamento padrão (Tabela 42), índice 0 = 1 circuito.</summary>
        public static double[] DefaultGrouping(GroupingKind kind) => kind switch
        {
            // 1..8, 9-11, 12-15, 16-19, >=20
            GroupingKind.Closed => new[] { 1.00, 0.80, 0.70, 0.65, 0.60, 0.57, 0.54, 0.52, 0.50, 0.45, 0.41, 0.38 },
            // 1..8, >=9 (camada única)
            GroupingKind.SmoothTray => new[] { 1.00, 0.85, 0.79, 0.75, 0.73, 0.72, 0.72, 0.71, 0.70 },
            GroupingKind.PerforatedTray => new[] { 1.00, 0.88, 0.82, 0.77, 0.75, 0.73, 0.73, 0.72, 0.72 },
            _ => new[] { 1.00, 0.87, 0.82, 0.80, 0.80, 0.79, 0.79, 0.78, 0.78 },
        };

        /// <summary>Fator de agrupamento de uma tabela (fechada: 12 colunas; bandejas/leitos: 9 colunas).</summary>
        public static double GroupingFactor(double[] table, int circuits)
        {
            if (table == null || table.Length == 0) return GroupingFactor(circuits);
            if (circuits <= 1) return table[0];
            if (table.Length >= 12)
            {
                if (circuits <= 8) return table[circuits - 1];
                if (circuits <= 11) return table[8];
                if (circuits <= 15) return table[9];
                if (circuits <= 19) return table[10];
                return table[11];
            }
            return table[Math.Min(circuits, table.Length) - 1];
        }

        /// <summary>Correntes nominais usuais de disjuntores (A).</summary>
        public static readonly int[] BreakerRatings =
            { 6, 10, 13, 16, 20, 25, 32, 40, 50, 63, 70, 80, 90, 100, 125, 150, 175, 200, 225, 250, 300, 350, 400 };

        public static string FormatSection(double s) =>
            s.ToString(s < 1 ? "0.##" : "0.#", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    }
}
