using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RafaelElectrical.Services
{
    /// <summary>Capacity: seção pela corrente de projeto e disjuntor até a capacidade corrigida do cabo; DesignCurrent: disjuntor ≥ Ib e cabo com Iz ≥ In.</summary>
    public enum BreakerMethod { Capacity, DesignCurrent }

    /// <summary>Configurações do dimensionamento, salvas em %AppData%\RafaelElectrical\dimensionamento.json.</summary>
    public class DimensioningSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public InstallMethod Method { get; set; } = InstallMethod.B1;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public Insulation Insulation { get; set; } = Insulation.PVC70;

        /// <summary>Temperatura ambiente (ar) ou do solo (método D), °C.</summary>
        public double TemperatureC { get; set; } = 30;

        /// <summary>Quantidade de circuitos agrupados considerada no pior trecho (até existir o roteamento).</summary>
        public int GroupedCircuits { get; set; } = 3;

        /// <summary>Queda de tensão máxima nos circuitos terminais (%).</summary>
        public double MaxVoltageDropPercent { get; set; } = 4.0;

        /// <summary>Resistividade do cobre (Ω·mm²/m). Padrão 1/56.</summary>
        public double Resistivity { get; set; } = 1.0 / 56.0;

        public double MinSectionLighting { get; set; } = 1.5;
        public double MinSectionPower { get; set; } = 2.5;

        /// <summary>Atualiza também o parâmetro nativo de disjuntor (Rating) do circuito no Revit.</summary>
        public bool UpdateRevitRating { get; set; } = true;

        // ---------------- Geral ----------------
        /// <summary>Maior seção usada no dimensionamento (mm²).</summary>
        public double MaxSection { get; set; } = 300;

        /// <summary>Seção que não aparece nos identificadores de fiação (0 = mostrar todas).</summary>
        public double OmitSectionInTags { get; set; } = 2.5;

        // ---------------- Dimensionamento ----------------
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BreakerMethod BreakerMethod { get; set; } = BreakerMethod.Capacity;

        /// <summary>Queda de tensão total admitida do ponto de entrega/origem até o ponto de utilização (%): 5 ou 7.</summary>
        public double TotalVoltageDropPercent { get; set; } = 5.0;

        /// <summary>Verifica a queda acumulada (alimentadores + circuito terminal).</summary>
        public bool CheckTotalVoltageDrop { get; set; } = true;

        /// <summary>Neutro reduzido pela Tabela 48 nos circuitos trifásicos (fase &gt; 25 mm²).</summary>
        public bool ReducedNeutral { get; set; } = false;

        // ---------------- Fiação ----------------
        public string ColorPhaseA { get; set; } = "Vermelho";
        public string ColorPhaseB { get; set; } = "Preto";
        public string ColorPhaseC { get; set; } = "Branco";
        public string ColorNeutral { get; set; } = "Azul";
        public string ColorGround { get; set; } = "Verde";
        public string ColorReturn { get; set; } = "Amarelo";

        /// <summary>Fator de correção aplicado ao comprimento no quantitativo de fiação (sobras, subidas, emendas).</summary>
        public double WiringFactor { get; set; } = 1.20;

        // ---------------- Agrupamento (Tabela 42, editável) ----------------
        public double[] GroupingClosed { get; set; } = Nbr5410.DefaultGrouping(GroupingKind.Closed);
        public double[] GroupingSmoothTray { get; set; } = Nbr5410.DefaultGrouping(GroupingKind.SmoothTray);
        public double[] GroupingPerforatedTray { get; set; } = Nbr5410.DefaultGrouping(GroupingKind.PerforatedTray);
        public double[] GroupingLadder { get; set; } = Nbr5410.DefaultGrouping(GroupingKind.Ladder);

        public double[] GroupingTable(GroupingKind kind) => kind switch
        {
            GroupingKind.SmoothTray => GroupingSmoothTray,
            GroupingKind.PerforatedTray => GroupingPerforatedTray,
            GroupingKind.Ladder => GroupingLadder,
            _ => GroupingClosed,
        };

        public double GroupingFactor(GroupingKind kind, int circuits) => Nbr5410.GroupingFactor(GroupingTable(kind), circuits);

        // ---------------- Ocupação ----------------
        public double OccupancyOneCable { get; set; } = 53;
        public double OccupancyTwoCables { get; set; } = 31;
        public double OccupancyMoreCables { get; set; } = 40;

        public double OccupancyLimit(int cables) =>
            (cables <= 1 ? OccupancyOneCable : cables == 2 ? OccupancyTwoCables : OccupancyMoreCables) / 100.0;

        // ---------------- Telecom ----------------
        public string CableData { get; set; } = "UTP Cat6";
        public string CableTelephone { get; set; } = "CCI 2 pares";
        public string CableCommunication { get; set; } = "Coaxial RG6";
        public string CableSecurity { get; set; } = "Coaxial RG59 + alimentação";
        /// <summary>Diâmetro externo médio dos cabos de telecom (mm), para a taxa de ocupação.</summary>
        public double TelecomCableDiameterMm { get; set; } = 6.0;

        // ---------------- Documentação ----------------
        public bool DiagramUseDps { get; set; } = false;
        public string DiagramDpsText { get; set; } = "DPS classe II 275 V 20 kA (F e N)";

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RafaelElectrical", "dimensionamento.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static DimensioningSettings Load() => LoadFrom(FilePath) ?? new DimensioningSettings();

        /// <summary>Lê um arquivo de configurações (null se não existir ou for inválido).</summary>
        public static DimensioningSettings LoadFrom(string path)
        {
            try
            {
                if (File.Exists(path))
                    return Validate(JsonSerializer.Deserialize<DimensioningSettings>(File.ReadAllText(path)));
            }
            catch (Exception) { }
            return null;
        }

        private static DimensioningSettings Validate(DimensioningSettings s)
        {
            if (s == null) return null;
            if (s.GroupingClosed == null || s.GroupingClosed.Length != 12) s.GroupingClosed = Nbr5410.DefaultGrouping(GroupingKind.Closed);
            if (s.GroupingSmoothTray == null || s.GroupingSmoothTray.Length != 9) s.GroupingSmoothTray = Nbr5410.DefaultGrouping(GroupingKind.SmoothTray);
            if (s.GroupingPerforatedTray == null || s.GroupingPerforatedTray.Length != 9) s.GroupingPerforatedTray = Nbr5410.DefaultGrouping(GroupingKind.PerforatedTray);
            if (s.GroupingLadder == null || s.GroupingLadder.Length != 9) s.GroupingLadder = Nbr5410.DefaultGrouping(GroupingKind.Ladder);
            if (s.MaxSection <= 0) s.MaxSection = 300;
            if (s.WiringFactor <= 0) s.WiringFactor = 1.0;
            return s;
        }

        public DimensioningSettings Clone() =>
            Validate(JsonSerializer.Deserialize<DimensioningSettings>(JsonSerializer.Serialize(this, Json)));

        public void Save() => SaveTo(FilePath);

        public bool SaveTo(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
