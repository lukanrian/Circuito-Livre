using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitoLivre.Services
{
    /// <summary>Capacity: seção pela corrente de projeto e disjuntor até a capacidade corrigida do cabo; DesignCurrent: disjuntor ≥ Ib e cabo com Iz ≥ In.</summary>
    public enum BreakerMethod { Capacity, DesignCurrent }

    /// <summary>Como o condutor de proteção aparece nos identificadores de fiação.</summary>
    public enum GroundGrouping { None, OnePerRun, LightingOnly }

    /// <summary>Trava de ângulo do lançamento de eletroduto (planta e prumada).</summary>
    public enum ConduitAngleLock { Free, Deg90, Deg45 }

    /// <summary>Por qual eixo o trecho horizontal começa ao fazer o "L" (planta).</summary>
    public enum ConduitRunOrientation { Default, Horizontal, Vertical }

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

    public class CableSectionRule
    {
        public double Phase { get; set; }
        public double Neutral { get; set; }
        public double Ground { get; set; }
        public double Return { get; set; }
    }

    public class CableDiameterRule
    {
        public double Section { get; set; }
        public double DiameterMm { get; set; }
    }

    public class CircuitDocumentationRule
    {
        public string LoadName { get; set; }
        public int Poles { get; set; }
        public string CableType { get; set; } = "";
        public string Insulation { get; set; } = "PVC";
        /// <summary>Material do condutor deste tipo de circuito: "Cobre" ou "Alumínio".</summary>
        public string Material { get; set; } = "Cobre";
        public bool Neutral { get; set; }
        public bool Ground { get; set; }
        public string Curve { get; set; } = "C";
    }

    public class SizingCondition
    {
        public string Type { get; set; } = "Potência aparente (VA)";
        public string Operator { get; set; } = "Maior ou igual a";
        public string Value { get; set; } = "";

        public bool Matches(CircuitInput input)
        {
            if (Type == "Descrição da carga" || Type == "Família específica")
            {
                string source = Type == "Família específica" ? input.FamilyNames : input.LoadName;
                bool contains = (source ?? "").IndexOf(Value ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
                return Operator == "Não contém" ? !contains : contains;
            }
            if (!double.TryParse((Value ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double threshold)) return false;
            double actual = Type switch
            {
                "Potência aparente (VA)" => input.ApparentLoadVA,
                "Corrente (A)" => input.CurrentA,
                "Nº fases" => input.Poles,
                _ => double.NaN
            };
            return double.IsFinite(actual) && (Operator switch
            {
                "Igual a" => Math.Abs(actual - threshold) < 1e-9,
                "Não é igual a" => Math.Abs(actual - threshold) >= 1e-9,
                "Maior que" => actual > threshold,
                "Maior ou igual a" => actual >= threshold,
                "Menor que" => actual < threshold,
                "Menor ou igual a" => actual <= threshold,
                _ => false
            });
        }
    }

    public class SizingRule
    {
        public string Name { get; set; } = "Nova regra";
        public int MinimumBreakerA { get; set; } = 10;
        public double MinimumSectionMm2 { get; set; } = 1.5;
        public bool MatchAny { get; set; }
        public List<SizingCondition> Conditions { get; set; } = new();
        public bool Matches(CircuitInput input) => Conditions.Count > 0 &&
            (MatchAny ? Conditions.Any(c => c.Matches(input)) : Conditions.All(c => c.Matches(input)));
    }

    /// <summary>Configurações do dimensionamento, salvas em %AppData%\CircuitoLivre\dimensionamento.json.</summary>
    public class DimensioningSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public InstallMethod Method { get; set; } = InstallMethod.B1;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public Insulation Insulation { get; set; } = Insulation.PVC70;

        /// <summary>Material do condutor padrão do projeto (por circuito, use a documentação).</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConductorMaterial Material { get; set; } = ConductorMaterial.Copper;

        /// <summary>Temperatura ambiente (ar) ou do solo (método D), °C.</summary>
        public double TemperatureC { get; set; } = 30;

        /// <summary>Quantidade de circuitos agrupados considerada no pior trecho (até existir o roteamento).</summary>
        public int GroupedCircuits { get; set; } = 3;

        /// <summary>Queda de tensão máxima nos circuitos terminais (%).</summary>
        public double MaxVoltageDropPercent { get; set; } = 4.0;

        /// <summary>Resistividade do cobre (Ω·mm²/m). Padrão 1/56.</summary>
        public double Resistivity { get; set; } = 1.0 / 56.0;

        /// <summary>Resistividade do alumínio (Ω·mm²/m). Padrão 1/35.</summary>
        public double ResistivityAluminum { get; set; } = 1.0 / 35.0;

        public double MinSectionLighting { get; set; } = 1.5;
        public double MinSectionPower { get; set; } = 2.5;

        /// <summary>Atualiza também o parâmetro nativo de disjuntor (Rating) do circuito no Revit.</summary>
        public bool UpdateRevitRating { get; set; } = true;

        // ---------------- Geral ----------------
        /// <summary>Maior seção usada no dimensionamento (mm²).</summary>
        public double MaxSection { get; set; } = 300;

        /// <summary>Seção que não aparece nos identificadores de fiação (0 = mostrar todas).</summary>
        public double OmitSectionInTags { get; set; } = 2.5;
        public string CircuitSectionParameter { get; set; } = "CL_Seção (mm²)";
        public string InterfaceLanguage { get; set; } = "Português Brasileiro";

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
        public bool WiringReportExpandedDescription { get; set; }
        public bool BipolarSensorTwoReturns { get; set; } = true;
        public bool SpaceTagsWithoutGround { get; set; } = true;
        public List<CableSectionRule> CableSections { get; set; } = DefaultCableSections();
        public Dictionary<string, List<CableDiameterRule>> CableDiameters { get; set; } = DefaultCableDiameters();

        private static Dictionary<string, List<CableDiameterRule>> DefaultCableDiameters()
        {
            double[] sections = { 1.5, 2.5, 4, 6, 10, 16, 25, 35, 50, 70, 95, 120, 150, 185, 240, 300, 400, 500 };
            double[] diameters = { 2.9, 3.5, 4, 4.6, 6, 6.8, 8.8, 10.2, 12.3, 14, 16, 17.8, 19.8, 22, 24.6, 27.8, 32.2, 35.8 };
            return new Dictionary<string, List<CableDiameterRule>>
            {
                ["Isolado|PVC"] = sections.Select((section, i) =>
                    new CableDiameterRule { Section = section, DiameterMm = diameters[i] }).ToList()
            };
        }

        private static List<CableSectionRule> DefaultCableSections() => Nbr5410.Sections
            .Where(x => x >= 1.5).Select(x => new CableSectionRule
            {
                Phase = x, Neutral = Nbr5410.ReducedNeutral(x),
                Ground = Nbr5410.ProtectionSection(x), Return = x
            }).Concat(new[]
            {
                new CableSectionRule { Phase = 400, Neutral = 185, Ground = 185, Return = 400 },
                new CableSectionRule { Phase = 500, Neutral = 400, Ground = 300, Return = 500 },
                new CableSectionRule { Phase = 630, Neutral = 400, Ground = 400, Return = 630 },
                new CableSectionRule { Phase = 800, Neutral = 400, Ground = 400, Return = 800 },
                new CableSectionRule { Phase = 1000, Neutral = 400, Ground = 500, Return = 1000 }
            }).ToList();

        public CableSectionRule SectionRule(double phase) => CableSections?.FirstOrDefault(x => Math.Abs(x.Phase - phase) < 1e-9);

        public double CableDiameter(double section, string cableType = "Isolado", string insulation = "PVC")
        {
            string key = cableType + "|" + insulation;
            if (CableDiameters != null && CableDiameters.TryGetValue(key, out var rows))
            {
                double value = rows.FirstOrDefault(r => Math.Abs(r.Section - section) < 1e-9)?.DiameterMm ?? 0;
                if (value > 0) return value;
            }
            return section > 0 ? 3.6 * Math.Sqrt(section / 2.5) : 0;
        }

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
        public string CableData { get; set; } = "UTP_CAT-5e";
        public string CableTelephone { get; set; } = "CCI_1P";
        public string CableCommunication { get; set; } = "RG59_47%";
        public string CableSecurity { get; set; } = "RG59_47%";
        /// <summary>Diâmetro externo médio dos cabos de telecom (mm), para a taxa de ocupação.</summary>
        public double TelecomCableDiameterMm { get; set; } = 6.0;
        public Dictionary<string, double> TelecomCableDiameters { get; set; } = new()
        {
            ["CCI_1P"] = 2.6, ["CCI_2P"] = 2.8, ["CCI_3P"] = 3.6, ["CCI_4P"] = 4.0,
            ["CI_10P"] = 8.1, ["CI_20P"] = 9.25, ["CI_30P"] = 11.5, ["CI_40P"] = 13.8,
            ["FO_N2"] = 4.8, ["FO_N4"] = 5.2, ["FO_N6"] = 5.6, ["FO_N8"] = 6.0, ["FO_N12"] = 6.5,
            ["UTP_CAT-5e"] = 5.5, ["UTP_CAT-6"] = 6.0,
            ["FTP_CAT-6"] = 7.5, ["FTP_CAT-6a"] = 8.8, ["FTP_CAT-7"] = 8.8,
            ["RG59_47%"] = 5.7, ["RG59_67%"] = 5.7, ["RG59_95%"] = 5.7,
            ["RG6_67%"] = 6.4, ["RG6_90%"] = 6.4, ["RG6_95%"] = 6.4
        };
        public Dictionary<string, string> TelecomCableNames { get; set; } = new();
        public bool TelecomVerboseNomenclature { get; set; }
        public string TelecomPanelSchedule { get; set; } = "Painel de Dados";
        public bool TelecomTagDimension { get; set; }

        public string TelecomName(string cable) => cable != null && TelecomCableNames.TryGetValue(cable, out string name) &&
            !string.IsNullOrWhiteSpace(name) ? name : cable;

        public double TelecomDiameter(string cable) => cable != null && TelecomCableDiameters.TryGetValue(cable, out double value)
            ? value : TelecomCableDiameterMm;

        // ---------------- Documentação ----------------
        public bool DiagramUseDps { get; set; } = false;
        public string DiagramDpsText { get; set; } = "DPS classe II 275 V 20 kA (F e N)";
        public bool DiagramDpsOnNeutral { get; set; } = true;
        public bool DiagramMultifilar { get; set; } = false;
        public bool DiagramShowSpares { get; set; } = true;
        public bool DiagramShowEmpty { get; set; } = false;
        public bool DiagramProtectionDetail { get; set; } = false;
        public bool DiagramUnifilar { get; set; } = true;
        public string DiagramSize { get; set; } = "Grande";
        public int DiagramDpsVoltageV { get; set; } = 275;
        public int DiagramDpsCurrentKa { get; set; } = 45;
        public bool DiagramDpsBreaker { get; set; } = true;
        public int DiagramDpsBreakerA { get; set; } = 25;
        public bool ProtectionShowSummary { get; set; } = true;
        public bool ProtectionShowNeutralBus { get; set; } = true;
        public bool ProtectionShowGroundBus { get; set; } = true;
        public string ProtectionBusType { get; set; } = "DIN";
        public int ProtectionBreakersPerRow { get; set; } = 4;
        public int ProtectionFishboneMinCircuits { get; set; } = 4;
        public Dictionary<string, string> TagViewByLevel { get; set; } = new();
        public List<CircuitDocumentationRule> CircuitDocumentation { get; set; } = new();

        public CircuitDocumentationRule DocumentationFor(string loadName, int poles) =>
            CircuitDocumentation.FirstOrDefault(r => r.LoadName == loadName && r.Poles == poles);

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
        public double TagIgnoreConduitDiameterMm { get; set; } = 25;
        public double TagIgnoreTrayWidthMm { get; set; } = 50;
        public double TagIgnoreTrayHeightMm { get; set; } = 50;
        /// <summary>Tamanhos comerciais de eletrocalha (largura x altura em mm, ex.: "50x50").</summary>
        public List<string> TraySizes { get; set; } = new() { "50x50", "100x50", "150x50", "200x50", "200x100", "300x100", "400x100", "500x100" };
        /// <summary>Tipo de identificador por categoria de dispositivo ("Família : Tipo").</summary>
        public Dictionary<string, string> TagByCategory { get; set; } = new();
        public Dictionary<string, string> TagByDeviceType { get; set; } = new();
        public Dictionary<string, bool> TagCenterByDeviceType { get; set; } = new();
        /// <summary>Altura padrão da infraestrutura por nível (m acima do nível).</summary>
        public Dictionary<string, double> InfraHeightByLevel { get; set; } = new();
        public double InfraDefaultHeightM { get; set; } = 2.80;

        // ---- Lançamento de eletrodutos (Construir infraestrutura) ----
        /// <summary>Tipo de eletroduto/eletrocalha dos trechos horizontais (teto/laje); null = primeiro da lista.</summary>
        public string ConduitCeilingTypeName { get; set; }
        /// <summary>Tipo das subidas/descidas (parede/prumada); null = mesmo do teto.</summary>
        public string ConduitWallTypeName { get; set; }
        /// <summary>Diâmetro adotado no lançamento (mm); aplicado aos eletrodutos criados.</summary>
        public double ConduitBuilderDiameterMm { get; set; } = 25;
        /// <summary>Disciplina/serviço gravado nos trechos criados (campo Comentários).</summary>
        public string ConduitDiscipline { get; set; } = "Elétrica";
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConduitAngleLock ConduitPlanAngle { get; set; } = ConduitAngleLock.Deg90;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConduitAngleLock ConduitWallAngle { get; set; } = ConduitAngleLock.Deg90;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConduitRunOrientation ConduitOrientation { get; set; } = ConduitRunOrientation.Default;
        /// <summary>True: conecta subindo ao teto; false: conecta direto pela mesma parede.</summary>
        public bool ConduitPrioritizeCeiling { get; set; } = true;

        public Dictionary<string, double> FloorOffsetByLevel { get; set; } = new();
        public List<string> IgnoredInfraLevels { get; set; } = new();

        public bool ConsiderInfraLevel(string name) => !IgnoredInfraLevels.Contains(name);
        public double FloorOffset(string name) => FloorOffsetByLevel.TryGetValue(name, out double value) ? value : 0.20;

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
        /// <summary>Máximo de condutores por fase em paralelo quando MultipleConductorsNcf está ligado.</summary>
        public int MaxConductorsPerPhase { get; set; } = 4;
        public List<CircuitClassRule> CircuitClasses { get; set; } = new()
        {
            new CircuitClassRule { Name = "Iluminação", Keywords = "ilum;luz;lumin", Curve = "B" },
            new CircuitClassRule { Name = "Tomadas de uso geral", Keywords = "tug;tomada", Curve = "C" },
            new CircuitClassRule { Name = "Tomadas de uso específico", Keywords = "tue;chuveiro;forno;micro;máquina;maquina;secadora", Curve = "C" },
            new CircuitClassRule { Name = "Ar-condicionado / motores", Keywords = "ar cond;ar-cond;split;motor;bomba;portão;portao", Curve = "D", Neutral = false },
        };
        public List<SizingRule> SizingRules { get; set; } = new();

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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CircuitoLivre", "dimensionamento.json");

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

        /// <summary>Configurações deste computador (%AppData%\CircuitoLivre\dimensionamento.json).</summary>
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
            s.CableSections ??= d.CableSections;
            s.CableDiameters ??= d.CableDiameters;
            s.TelecomCableDiameters ??= d.TelecomCableDiameters;
            s.TelecomCableNames ??= new Dictionary<string, string>();
            s.TagByCategory ??= new Dictionary<string, string>();
            s.TagByDeviceType ??= new Dictionary<string, string>();
            s.TagCenterByDeviceType ??= new Dictionary<string, bool>();
            s.InfraHeightByLevel ??= new Dictionary<string, double>();
            s.FloorOffsetByLevel ??= new Dictionary<string, double>();
            s.TagViewByLevel ??= new Dictionary<string, string>();
            s.CircuitDocumentation ??= new List<CircuitDocumentationRule>();
            s.IgnoredInfraLevels ??= new List<string>();
            if (s.TraySizes == null || s.TraySizes.Count == 0) s.TraySizes = d.TraySizes;
            if (s.CircuitClasses == null || s.CircuitClasses.Count == 0) s.CircuitClasses = d.CircuitClasses;
            s.SizingRules ??= new List<SizingRule>();
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
                !double.IsFinite(ResistivityAluminum) || ResistivityAluminum <= 0 ||
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
            if (SizingRules == null || SizingRules.Any(r => r == null || string.IsNullOrWhiteSpace(r.Name) ||
                !Nbr5410.BreakerRatings.Contains(r.MinimumBreakerA) || !Nbr5410.Sections.Contains(r.MinimumSectionMm2) ||
                r.Conditions == null || r.Conditions.Count == 0 || r.Conditions.Any(c => c == null ||
                    string.IsNullOrWhiteSpace(c.Value) || !new[] { "Potência aparente (VA)", "Corrente (A)", "Nº fases", "Descrição da carga", "Família específica" }.Contains(c.Type) ||
                    !(c.Type == "Descrição da carga" || c.Type == "Família específica"
                        ? new[] { "Contém", "Não contém" }.Contains(c.Operator)
                        : new[] { "Igual a", "Não é igual a", "Maior que", "Maior ou igual a", "Menor que", "Menor ou igual a" }.Contains(c.Operator)) ||
                    (c.Type != "Descrição da carga" && c.Type != "Família específica" &&
                     (!double.TryParse(c.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < 0)))))
                throw new ArgumentException("Regras condicionais de dimensionamento inválidas.");
            if (CableSections == null || CableSections.Count == 0 ||
                CableSections.Any(r => r == null || !double.IsFinite(r.Phase) || r.Phase <= 0 ||
                    !double.IsFinite(r.Neutral) || r.Neutral <= 0 || r.Neutral > r.Phase ||
                    !double.IsFinite(r.Ground) || r.Ground <= 0 || r.Ground > r.Phase ||
                    !double.IsFinite(r.Return) || r.Return <= 0 || r.Return > r.Phase) ||
                CableSections.Select(r => r.Phase).Distinct().Count() != CableSections.Count)
                throw new ArgumentException("Tabela de seções de fase, neutro, terra e retorno inválida.");
            if (CableDiameters == null || CableDiameters.Values.Any(rows => rows == null ||
                rows.Any(r => r == null || !double.IsFinite(r.Section) || r.Section <= 0 ||
                    !double.IsFinite(r.DiameterMm) || r.DiameterMm <= 0 || r.DiameterMm > 100)))
                throw new ArgumentException("Tabela de diâmetros de cabos inválida.");
            if (TelecomCableDiameters == null || TelecomCableDiameters.Any(kv => string.IsNullOrWhiteSpace(kv.Key) ||
                !double.IsFinite(kv.Value) || kv.Value <= 0 || kv.Value > 100))
                throw new ArgumentException("Diâmetros dos cabos de telecom inválidos.");
            if (!double.IsFinite(TagIgnoreConduitDiameterMm) || TagIgnoreConduitDiameterMm <= 0 ||
                !double.IsFinite(TagIgnoreTrayWidthMm) || TagIgnoreTrayWidthMm <= 0 ||
                !double.IsFinite(TagIgnoreTrayHeightMm) || TagIgnoreTrayHeightMm <= 0)
                throw new ArgumentException("Dimensões a ignorar nos identificadores inválidas.");
            if (FloorOffsetByLevel == null || FloorOffsetByLevel.Any(kv => !double.IsFinite(kv.Value) || kv.Value < -5 || kv.Value > 5))
                throw new ArgumentException("Cota de piso inválida.");
            if (CircuitDocumentation == null || CircuitDocumentation.Any(r => r == null || string.IsNullOrWhiteSpace(r.LoadName) ||
                r.Poles < 1 || r.Poles > 3 || !new[] { "", "Isolado", "Unipolar", "Multipolar" }.Contains(r.CableType) ||
                !new[] { "PVC", "XLPE" }.Contains(r.Insulation) || !new[] { "B", "C", "D" }.Contains(r.Curve)))
                throw new ArgumentException("Configuração dos circuitos inválida.");
            if (!new[] { "Pequeno", "Médio", "Grande" }.Contains(DiagramSize) ||
                !new[] { "DIN", "Espinha de peixe" }.Contains(ProtectionBusType) ||
                !new[] { 75, 175, 275, 385, 460 }.Contains(DiagramDpsVoltageV) ||
                !new[] { 20, 30, 45 }.Contains(DiagramDpsCurrentKa) ||
                !Nbr5410.BreakerRatings.Contains(DiagramDpsBreakerA) ||
                ProtectionBreakersPerRow < 4 || ProtectionBreakersPerRow > 24 ||
                ProtectionFishboneMinCircuits < 4 || ProtectionFishboneMinCircuits > 100)
                throw new ArgumentException("Opções de documentação inválidas.");
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
