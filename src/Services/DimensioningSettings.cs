using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RafaelElectrical.Services
{
    /// <summary>Capacity: seção pela corrente de projeto e disjuntor até a capacidade corrigida do cabo; DesignCurrent: disjuntor ≥ Ib e cabo com Iz ≥ In.</summary>
    public enum BreakerMethod { Capacity, DesignCurrent }

    /// <summary>Como o condutor de proteção aparece nos identificadores de fiação.</summary>
    public enum GroundGrouping { None, OnePerRun, LightingOnly }

    /// <summary>Regra por tipo de circuito: curva do disjuntor e se leva neutro/terra.</summary>
    public class CircuitClassRule
    {
        public string Name { get; set; }
        public string Keywords { get; set; }
        public string Curve { get; set; } = "C";
        public bool Neutral { get; set; } = true;
        public bool Ground { get; set; } = true;
        public double MinimumSectionMm2 { get; set; }
        public int MinimumBreakerA { get; set; }
    }

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
        public bool DiagramDpsOnNeutral { get; set; } = true;
        public bool DiagramMultifilar { get; set; } = false;
        public bool DiagramShowSpares { get; set; } = true;
        public bool DiagramShowEmpty { get; set; } = false;
        public bool DiagramProtectionDetail { get; set; } = false;

        /// <summary>Identificadores de fiação: mostrar a seção.</summary>
        public bool TagShowSection { get; set; } = true;
        /// <summary>Identificadores de fiação alinhados ao eletroduto (senão, horizontais).</summary>
        public bool TagAlongConduit { get; set; } = false;
        /// <summary>Tamanho do texto dos identificadores de fiação (mm no papel).</summary>
        public double TagTextMm { get; set; } = 2.0;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GroundGrouping GroundGrouping { get; set; } = GroundGrouping.None;
        /// <summary>Retornos de luminárias e interruptores unidos no mesmo identificador.</summary>
        public bool JoinReturns { get; set; } = true;
        /// <summary>Legendas de fiação numeradas (1, 2, 3) ou com letras (A, B, C).</summary>
        public bool LegendAlphabetic { get; set; } = false;

        // ---------------- Conduítes / bandejas / identificadores / cotas ----------------
        /// <summary>Diâmetro padrão (mm) por nome de tipo de eletroduto; trechos com outro diâmetro recebem identificador de dimensão.</summary>
        public Dictionary<string, double> ConduitDefaultDiameter { get; set; } = new();
        public double ConduitDefaultDiameterMm { get; set; } = 25;
        public double TrayDefaultWidthMm { get; set; } = 50;
        public double TrayDefaultHeightMm { get; set; } = 50;
        /// <summary>Tamanhos comerciais de eletrocalha (largura x altura em mm, ex.: "50x50").</summary>
        public List<string> TraySizes { get; set; } = new() { "50x50", "100x50", "150x50", "200x50", "200x100", "300x100", "400x100", "500x100" };
        /// <summary>Tipo de identificador por categoria de dispositivo ("Família : Tipo").</summary>
        public Dictionary<string, string> TagByCategory { get; set; } = new();
        /// <summary>Altura padrão da infraestrutura por nível (m acima do nível).</summary>
        public Dictionary<string, double> InfraHeightByLevel { get; set; } = new();
        public double InfraDefaultHeightM { get; set; } = 2.80;

        public double InfraHeight(string levelName) =>
            levelName != null && InfraHeightByLevel.TryGetValue(levelName, out double h) ? h : InfraDefaultHeightM;

        public double ConduitStandardDiameter(string typeName) =>
            typeName != null && ConduitDefaultDiameter.TryGetValue(typeName, out double d) ? d : ConduitDefaultDiameterMm;

        // ---------------- Proteção / DR ----------------
        /// <summary>Disjuntor antes do grupo de DR quando a corrente nominal do DR é menor que a do disjuntor geral.</summary>
        public bool BreakerBeforeDr { get; set; } = true;
        public int DrMaxCircuits { get; set; } = 4;
        public double DrSensitivityMa { get; set; } = 30;
        /// <summary>Palavras que indicam circuitos que exigem DR (NBR 5410 5.1.3.2.2).</summary>
        public string DrKeywords { get; set; } = "banh;wc;lavabo;cozinha;copa;serviço;servico;lavanderia;área externa;area externa;garagem;varanda;tomada;tug;tue;chuveiro";
        /// <summary>Temperatura de sobrecarga acima do tempo normativo (usa a tabela 35 de temperatura máxima de sobrecarga).</summary>
        public bool OverloadAboveNormativeTime { get; set; } = true;
        /// <summary>Condutores em paralelo: quantidade por neutro e por terra = N.C.F e metade do N.C.F.</summary>
        public bool MultipleConductorsNcf { get; set; } = false;
        public List<CircuitClassRule> CircuitClasses { get; set; } = new()
        {
            new CircuitClassRule { Name = "Iluminação", Keywords = "ilum;luz;lumin", Curve = "B" },
            new CircuitClassRule { Name = "Tomadas de uso geral", Keywords = "tug;tomada", Curve = "C" },
            new CircuitClassRule { Name = "Tomadas de uso específico", Keywords = "tue;chuveiro;forno;micro;máquina;maquina;secadora", Curve = "C" },
            new CircuitClassRule { Name = "Ar-condicionado / motores", Keywords = "ar cond;ar-cond;split;motor;bomba;portão;portao", Curve = "D", Neutral = false },
        };

        public CircuitClassRule ClassOf(string loadName)
        {
            string n = (loadName ?? "").ToLowerInvariant();
            return CircuitClasses.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Keywords) &&
                                                      r.Keywords.Split(';').Any(k => k.Trim().Length > 0 && n.Contains(k.Trim().ToLowerInvariant())));
        }

        public bool NeedsDr(string loadName)
        {
            string n = (loadName ?? "").ToLowerInvariant();
            return DrKeywords.Split(';').Any(k => k.Trim().Length > 0 && n.Contains(k.Trim().ToLowerInvariant()));
        }

        // ---------------- Concessionária / demanda / curto-circuito ----------------
        public bool RegisterUtility { get; set; } = false;
        public string Utility { get; set; } = "";
        public string SupplySystem { get; set; } = "127/220 V";
        public string EntranceType { get; set; } = "Aéreo";
        public string EntranceTypeCollective { get; set; } = "";
        /// <summary>Faixas de carga de iluminação + TUG (kVA, limite superior) e o fator de demanda de cada faixa.</summary>
        public double[] DemandLightingLimitsKva { get; set; } = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 1e9 };
        public double[] DemandLightingFactors { get; set; } = { 0.86, 0.75, 0.66, 0.59, 0.52, 0.45, 0.40, 0.35, 0.31, 0.27, 0.24 };
        /// <summary>Fator de demanda de TUE pela quantidade de aparelhos (índice 0 = 1 aparelho).</summary>
        public double[] DemandTueFactors { get; set; } = { 1.00, 1.00, 0.84, 0.76, 0.70, 0.65, 0.60, 0.57, 0.54, 0.52, 0.49, 0.48 };
        /// <summary>Corrente de curto-circuito presumida na origem (kA).</summary>
        public double SourceShortCircuitKa { get; set; } = 5.0;
        /// <summary>Capacidade de interrupção dos disjuntores de circuito terminal (kA).</summary>
        public double BreakerIcuKa { get; set; } = 3.0;
        /// <summary>Faixas do padrão de entrada: demanda máxima (kVA) e a categoria/disjuntor correspondente.</summary>
        public List<string> EntranceCategories { get; set; } = new()
        {
            "8;Monofásico — disjuntor 40 A",
            "12;Monofásico — disjuntor 63 A",
            "15;Bifásico — disjuntor 50 A",
            "25;Bifásico — disjuntor 70 A",
            "38;Trifásico — disjuntor 63 A",
            "57;Trifásico — disjuntor 100 A",
            "75;Trifásico — disjuntor 125 A",
        };

        public string EntranceFor(double demandKva)
        {
            foreach (string row in EntranceCategories)
            {
                string[] p = row.Split(';');
                if (p.Length >= 2 && double.TryParse(p[0].Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double max) && demandKva <= max)
                    return p[1].Trim();
            }
            return "Acima da tabela: consultar a concessionária (atendimento especial/subestação)";
        }

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RafaelElectrical", "dimensionamento.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Configurações gravadas no projeto ativo (JSON), ou null quando o projeto usa as do computador.
        /// Atualizado pelo App ao ativar uma vista/documento.
        /// </summary>
        public static string ActiveProjectJson { get; set; }

        /// <summary>As configurações do projeto mudaram e ainda precisam ser gravadas (ProjectStorage.FlushSettings).</summary>
        public static bool ProjectDirty { get; set; }

        /// <summary>Configurações em uso: as do projeto ativo, se houver; senão as do computador.</summary>
        public static DimensioningSettings Load() => FromJson(ActiveProjectJson) ?? LoadComputer();

        /// <summary>Configurações deste computador (%AppData%\RafaelElectrical\dimensionamento.json).</summary>
        public static DimensioningSettings LoadComputer() => LoadFrom(FilePath) ?? new DimensioningSettings();

        public static DimensioningSettings FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return Validate(JsonSerializer.Deserialize<DimensioningSettings>(json)); }
            catch (Exception) { return null; }
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

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

            if (s.WiringFactor <= 0) s.WiringFactor = 1.0;
            var d = new DimensioningSettings();
            s.ConduitDefaultDiameter ??= new Dictionary<string, double>();
            s.TagByCategory ??= new Dictionary<string, string>();
            s.InfraHeightByLevel ??= new Dictionary<string, double>();
            if (s.TraySizes == null || s.TraySizes.Count == 0) s.TraySizes = d.TraySizes;
            if (s.CircuitClasses == null || s.CircuitClasses.Count == 0) s.CircuitClasses = d.CircuitClasses;
            if (s.EntranceCategories == null || s.EntranceCategories.Count == 0) s.EntranceCategories = d.EntranceCategories;
            if (s.DemandLightingLimitsKva == null || s.DemandLightingFactors == null ||
                s.DemandLightingLimitsKva.Length != s.DemandLightingFactors.Length || s.DemandLightingFactors.Length == 0)
            {
                s.DemandLightingLimitsKva = d.DemandLightingLimitsKva;
                s.DemandLightingFactors = d.DemandLightingFactors;
            }
            if (s.DemandTueFactors == null || s.DemandTueFactors.Length == 0) s.DemandTueFactors = d.DemandTueFactors;
            s.DrKeywords ??= d.DrKeywords;
            if (s.DrMaxCircuits < 1) s.DrMaxCircuits = 4;
            if (s.TagTextMm <= 0) s.TagTextMm = 2.0;
            s.ValidateForSizing();
            return s;
        }

        public void ValidateForSizing()
        {
            Nbr5410.TemperatureFactor(Method, Insulation, TemperatureC);
            if (!Enum.IsDefined(BreakerMethod) || GroupedCircuits < 1 ||
                !double.IsFinite(Resistivity) || Resistivity <= 0 ||
                !double.IsFinite(MaxVoltageDropPercent) || MaxVoltageDropPercent <= 0 ||
                !double.IsFinite(TotalVoltageDropPercent) || TotalVoltageDropPercent <= 0 ||
                !Nbr5410.Sections.Contains(MinSectionLighting) || !Nbr5410.Sections.Contains(MinSectionPower) ||
                !Nbr5410.Sections.Contains(MaxSection) || MaxSection < Math.Max(MinSectionLighting, MinSectionPower) ||
                !double.IsFinite(SourceShortCircuitKa) || SourceShortCircuitKa <= 0 ||
                !double.IsFinite(BreakerIcuKa) || BreakerIcuKa <= 0)
                throw new ArgumentException("Configuração elétrica inválida: confira resistividade, quedas, seções e correntes de curto.");
            foreach (var factors in new[] { GroupingClosed, GroupingSmoothTray, GroupingPerforatedTray, GroupingLadder })
                if (factors == null || factors.Any(f => !double.IsFinite(f) || f <= 0 || f > 1))
                    throw new ArgumentException("Fatores de agrupamento devem estar entre zero (exclusivo) e um.");
            if (CircuitClasses == null || CircuitClasses.Any(r => r == null ||
                !double.IsFinite(r.MinimumSectionMm2) || r.MinimumSectionMm2 < 0 ||
                (r.MinimumSectionMm2 > 0 && !Nbr5410.Sections.Contains(r.MinimumSectionMm2)) ||
                r.MinimumBreakerA < 0 ||
                (r.MinimumBreakerA > 0 && !Nbr5410.BreakerRatings.Contains(r.MinimumBreakerA))))
                throw new ArgumentException("Mínimos por classe inválidos: use seção e disjuntor da tabela.");
        }

        public DimensioningSettings Clone() =>
            Validate(JsonSerializer.Deserialize<DimensioningSettings>(JsonSerializer.Serialize(this, Json)));

        /// <summary>
        /// Grava onde as configurações estão em uso: no projeto ativo (marca para gravar na próxima transação)
        /// ou no arquivo deste computador.
        /// </summary>
        public void Save()
        {
            if (ActiveProjectJson != null)
            {
                ActiveProjectJson = ToJson();
                ProjectDirty = true;
                return;
            }
            SaveTo(FilePath);
        }

        /// <summary>Grava sempre no arquivo deste computador.</summary>
        public void SaveComputer() => SaveTo(FilePath);

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
