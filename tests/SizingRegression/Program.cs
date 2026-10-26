using CircuitoLivre.Services;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
CircuitInput Circuit(string panel = "Origem") => new() { Panel = panel, VoltageV = 127, Poles = 1, CurrentA = 10, LengthM = 100 };
var settings = new DimensioningSettings();
var baseline = CircuitSizer.Size(Circuit(), settings);
Check(baseline.Ok && baseline.SectionMm2 == 10 && Math.Abs(baseline.VoltageDropPercent - 2.812148) < .00001, "baseline físico preservado");
var customCable = new DimensioningSettings();
customCable.CableSections.First(r => r.Phase == 10).Ground = 6;
var customResult = CircuitSizer.Size(Circuit(), customCable);
Check(customResult.Ok && customResult.SectionMm2 == 10 && customResult.ProtectionMm2 == 6,
    "tabela personalizada aplica seção de proteção");
Check(Math.Abs(DimensioningSettings.FromJson(customCable.ToJson()).CableDiameter(2.5) - 3.5) < 1e-9,
    "diâmetro do cabo persiste na configuração");
customCable.CableSections.First(r => r.Phase == 10).Ground = 16;
Check(DimensioningSettings.FromJson(customCable.ToJson()) == null,
    "importação rejeita proteção maior que a fase");
var telecom = new DimensioningSettings();
telecom.CableData = "UTP_CAT-6";
telecom.TelecomCableDiameters["UTP_CAT-6"] = 6.4;
telecom.TelecomCableNames["UTP_CAT-6"] = "Cabo de dados CAT6";
var telecomLoaded = DimensioningSettings.FromJson(telecom.ToJson());
Check(telecomLoaded != null && telecomLoaded.TelecomDiameter(telecomLoaded.CableData) == 6.4 &&
    telecomLoaded.TelecomName(telecomLoaded.CableData) == "Cabo de dados CAT6",
    "catálogo de telecom preserva nome e diâmetro por cabo");
var tags = new DimensioningSettings { TagIgnoreConduitDiameterMm = 32, TagIgnoreTrayWidthMm = 100 };
tags.TagByDeviceType["OST_DataDevices|Tomada : RJ45"] = "Tag Dados : Tipo A";
tags.TagCenterByDeviceType["OST_DataDevices|Tomada : RJ45"] = true;
var tagsLoaded = DimensioningSettings.FromJson(tags.ToJson());
Check(tagsLoaded != null && tagsLoaded.TagIgnoreConduitDiameterMm == 32 &&
    tagsLoaded.TagIgnoreTrayWidthMm == 100 && tagsLoaded.TagCenterByDeviceType["OST_DataDevices|Tomada : RJ45"] &&
    tagsLoaded.TagByDeviceType["OST_DataDevices|Tomada : RJ45"] == "Tag Dados : Tipo A",
    "identificadores por tipo e dimensões ignoradas persistem");
var conditional = new DimensioningSettings();
conditional.SizingRules.Add(new SizingRule { Name = "Carga longa", MinimumBreakerA = 32,
    MinimumSectionMm2 = 16, Conditions = new() { new SizingCondition { Type = "Potência aparente (VA)",
        Operator = "Maior ou igual a", Value = "1000" } } });
var qualifying = Circuit(); qualifying.ApparentLoadVA = 1200;
Check(CircuitSizer.Size(qualifying, conditional).SectionMm2 >= 16 && CircuitSizer.Size(qualifying, conditional).BreakerA >= 32,
    "regra condicional aplica mínimos quando potência atende");
Check(CircuitSizer.Size(Circuit(), conditional).SectionMm2 == baseline.SectionMm2,
    "regra condicional não altera circuito fora do critério");
conditional.SizingRules[0].Conditions[0].Value = "inválido";
Check(DimensioningSettings.FromJson(conditional.ToJson()) == null,
    "importação rejeita condição numérica inválida");
var documentation = new DimensioningSettings();
documentation.CircuitDocumentation.Add(new CircuitDocumentationRule { LoadName = "Carga", Poles = 1,
    CableType = "Unipolar", Insulation = "XLPE", Neutral = true, Ground = true, Curve = "B" });
var documented = Circuit(); documented.LoadName = "Carga";
Check(CircuitSizer.Size(documented, documentation).IzA > CircuitSizer.Size(documented, settings).IzA,
    "isolação por circuito altera capacidade de condução");
documentation.TagViewByLevel["Térreo"] = "Planta elétrica";
Check(DimensioningSettings.FromJson(documentation.ToJson()).TagViewByLevel["Térreo"] == "Planta elétrica",
    "mapeamento de vista persiste no JSON");
var classSettings = new DimensioningSettings();
classSettings.CircuitClasses[0].MinimumSectionMm2 = 16;
classSettings.CircuitClasses[0].MinimumBreakerA = 32;
var classified = Circuit(); classified.LoadName = "Iluminação corredor";
var classResult = CircuitSizer.Size(classified, classSettings);
Check(classResult.Ok && classResult.SectionMm2 >= 16 && classResult.BreakerA >= 32 && classResult.IzA >= classResult.BreakerA,
    "classe impõe seção e proteção sem violar capacidade");
Check(CircuitSizer.Size(Circuit(), classSettings).SectionMm2 == baseline.SectionMm2,
    "regra da classe não altera circuitos sem correspondência");
classSettings.MaxSection = 10;
Check(!CircuitSizer.Size(classified, classSettings).Ok, "classe acima da seção máxima reprova circuito");
classSettings.CircuitClasses[0].MinimumSectionMm2 = 2;
Check(DimensioningSettings.FromJson(classSettings.ToJson()) == null, "configuração rejeita seção de classe fora da tabela");
Check(DimensioningSettings.FromJson("{\"Resistivity\":-0.02}") == null, "importação rejeita resistividade negativa");
Check(DimensioningSettings.FromJson("{\"MaxSection\":0}") == null, "importação rejeita seção máxima inválida");
foreach (double value in new[] { -1.0, double.NaN, double.PositiveInfinity })
{
 var c = Circuit(); c.CurrentA = value;
 Check(!CircuitSizer.Size(c, settings).Ok, "corrente inválida " + value);
 c = Circuit(); c.LengthM = value;
 Check(!CircuitSizer.Size(c, settings).Ok, "comprimento inválido " + value);
}
foreach (var method in Enum.GetValues<InstallMethod>())
{
 double max = method == InstallMethod.D ? 50 : 60;
 Check(Nbr5410.TemperatureFactor(method, Insulation.PVC70, max) > 0, "temperatura limite " + method);
 foreach (double invalid in new[] { 9.0, max + 1, double.NaN, double.PositiveInfinity })
 {
  bool rejected = false;
  try { Nbr5410.TemperatureFactor(method, Insulation.PVC70, invalid); } catch (ArgumentException) { rejected = true; }
  Check(rejected, "temperatura fora da tabela " + method + " " + invalid);
 }
}
var tiny = new DimensioningSettings { TotalVoltageDropPercent = .05 };
Check(!SizingCoordinator.SizeAll(new() { Circuit() }, tiny, out _)[0].Ok, "queda total reprova resultado");
var feeder = Circuit(); feeder.FeedsPanel = "QD1"; feeder.Poles = 3; feeder.VoltageV = 220; feeder.CurrentA = 50;
var terminal = Circuit("QD1");
var results = SizingCoordinator.SizeAll(new() { feeder, terminal }, settings, out _);
Check(results.All(x => x.Ok) && terminal.UpstreamDropPercent > 2 && results[1].SectionMm2 == 16, "queda a montante considerada no projeto");
feeder.ExistingSizingValid = true; feeder.ExistingSectionMm2 = 25;
results = SizingCoordinator.SizeAll(new() { terminal }, settings, out _, new() { feeder, terminal });
Check(results[0].Ok && results[0].SectionMm2 == 16 && terminal.UpstreamDropPercent > 2, "seleção parcial usa seção existente do alimentador");
feeder.ExistingSizingValid = false;
Check(!SizingCoordinator.SizeAll(new() { terminal }, settings, out _, new() { feeder, terminal })[0].Ok, "alimentador sem aprovação bloqueia seleção parcial");
feeder.ExistingSizingValid = true; feeder.LengthM = 0;
Check(!SizingCoordinator.SizeAll(new() { terminal }, settings, out _, new() { feeder, terminal })[0].Ok, "alimentador sem comprimento bloqueia queda total");
feeder.LengthM = 100; feeder.Panel = "QD1";
Check(SizingCoordinator.SizeAll(new() { feeder, terminal }, settings, out _).All(x => !x.Ok), "ciclo de alimentação reprovado");
feeder.Panel = "Origem";
var duplicate = Circuit(); duplicate.FeedsPanel = "QD1";
Check(!SizingCoordinator.SizeAll(new() { feeder, duplicate, terminal }, settings, out _)[2].Ok, "alimentação ambígua reprovada");
for (int j = 0; j < 2; j++)
{
 double voltage = j == 0 ? 220 : 380;
 double impedance = ShortCircuitEstimate.SourceImpedance(voltage, 5);
 Check(Math.Abs(ShortCircuitEstimate.CurrentKa(voltage, impedance) - 5) < 1e-10, "Icc usa tensão real " + voltage);
 Check(ShortCircuitEstimate.CurrentKa(voltage, impedance + .1) < 5, "impedância do alimentador reduz Icc " + voltage);
}
Check(double.IsNaN(ShortCircuitEstimate.CurrentKa(220, double.NaN)), "Icc desconhecido permanece não verificado");
var tighter = new DimensioningSettings { TotalVoltageDropPercent = 2 };
var resized = SizingCoordinator.SizeAll(new() { Circuit() }, tighter, out _)[0];
Check(resized.Ok && resized.TotalDropPercent <= 2 && resized.SectionMm2 == 16, "origem redimensiona para limite total menor");
var badSettings = new DimensioningSettings { Resistivity = double.NaN };
Check(!CircuitSizer.Size(Circuit(), badSettings).Ok, "configuração direta inválida também rejeitada");
// Condutores em paralelo: 2,5 mm² (B1/PVC, Iz=24 A) não atende 30 A sozinho; 2 em paralelo sim (2×24×0,80=38,4 A).
var parallelOn = new DimensioningSettings { MultipleConductorsNcf = true, MaxSection = 2.5, GroupedCircuits = 1 };
var bigLoad = Circuit(); bigLoad.CurrentA = 30; bigLoad.LengthM = 5;
var parallelResult = CircuitSizer.Size(bigLoad, parallelOn);
Check(parallelResult.Ok && parallelResult.ConductorsPerPhase >= 2 && parallelResult.SectionMm2 == 2.5,
    "condutores em paralelo atendem carga acima do condutor único");
var parallelOff = new DimensioningSettings { MultipleConductorsNcf = false, MaxSection = 2.5, GroupedCircuits = 1 };
Check(!CircuitSizer.Size(bigLoad, parallelOff).Ok, "sem paralelo, a mesma carga é reprovada");
// Alumínio: usa a tabela de alumínio e a seção mínima de 16 mm²; mesma carga exige seção maior que o cobre.
var alSettings = new DimensioningSettings { GroupedCircuits = 1 };
alSettings.CircuitDocumentation.Add(new CircuitDocumentationRule { LoadName = "Alim", Poles = 3, Material = "Alumínio", Insulation = "PVC", Curve = "C" });
var alCircuit = Circuit(); alCircuit.LoadName = "Alim"; alCircuit.Poles = 3; alCircuit.VoltageV = 220; alCircuit.CurrentA = 30; alCircuit.LengthM = 10;
var alResult = CircuitSizer.Size(alCircuit, alSettings);
Check(alResult.Ok && alResult.SectionMm2 >= 16, "alumínio respeita seção mínima de 16 mm²");
var cuCircuit = Circuit(); cuCircuit.Poles = 3; cuCircuit.VoltageV = 220; cuCircuit.CurrentA = 30; cuCircuit.LengthM = 10;
var cuResult = CircuitSizer.Size(cuCircuit, new DimensioningSettings { GroupedCircuits = 1 });
Check(cuResult.Ok && cuResult.SectionMm2 < alResult.SectionMm2, "alumínio exige seção maior que cobre para a mesma carga");

// Calculado vs adotado: a seção calculada é preservada e a adotada força e reverifica o circuito.
var adoptCalc = CircuitSizer.Size(Circuit(), settings);
Check(adoptCalc.Ok && adoptCalc.CalculatedSectionMm2 == 10 && adoptCalc.SectionMm2 == 10 && !adoptCalc.IsAdopted,
    "seção calculada preenchida quando não há adoção");
var adoptUp = Circuit(); adoptUp.AdoptedSectionMm2 = 16;
var adoptUpRes = CircuitSizer.Size(adoptUp, settings, adoptUp.AdoptedSectionMm2);
Check(adoptUpRes.Ok && adoptUpRes.SectionMm2 == 16 && adoptUpRes.CalculatedSectionMm2 == 10 &&
      adoptUpRes.AdoptedDiffers && adoptUpRes.Criterion == "Seção adotada",
    "adotar seção maior força, reverifica e mantém a calculada");
var adoptSame = Circuit(); adoptSame.AdoptedSectionMm2 = 10;
var adoptSameRes = CircuitSizer.Size(adoptSame, settings, adoptSame.AdoptedSectionMm2);
Check(adoptSameRes.Ok && adoptSameRes.SectionMm2 == 10 && !adoptSameRes.AdoptedDiffers,
    "adotar a própria seção calculada não diverge");
var adoptLow = Circuit(); adoptLow.AdoptedSectionMm2 = 1.5;
var adoptLowRes = CircuitSizer.Size(adoptLow, settings, adoptLow.AdoptedSectionMm2);
Check(!adoptLowRes.Ok && adoptLowRes.CalculatedSectionMm2 == 10 && adoptLowRes.Status.Contains("fixada"),
    "adotar seção insuficiente reprova e mostra a calculada");

// Roteamento em planta do lançamento de eletrodutos (trava de ângulo + orientação).
var inv = System.Globalization.CultureInfo.InvariantCulture;
string N(double v) => v.ToString("0.#", inv);
string Way(System.Collections.Generic.List<(double X, double Y)> p) => string.Join(" ", p.Select(v => $"({N(v.X)},{N(v.Y)})"));
Check(Way(ConduitRouting.PlanWaypoints(0, 0, 3, 2, ConduitAngleLock.Deg90, ConduitRunOrientation.Default)) == "(0,0) (3,0) (3,2)",
    "90° padrão faz L pelo eixo X primeiro");
Check(Way(ConduitRouting.PlanWaypoints(0, 0, 3, 2, ConduitAngleLock.Deg90, ConduitRunOrientation.Vertical)) == "(0,0) (0,2) (3,2)",
    "90° vertical faz L pelo eixo Y primeiro");
Check(Way(ConduitRouting.PlanWaypoints(0, 0, 3, 2, ConduitAngleLock.Free, ConduitRunOrientation.Default)) == "(0,0) (3,2)",
    "livre vai direto (diagonal)");
Check(Way(ConduitRouting.PlanWaypoints(0, 0, 3, 2, ConduitAngleLock.Deg45, ConduitRunOrientation.Default)) == "(0,0) (2,2) (3,2)",
    "45° faz diagonal no menor eixo e reta no restante");
Check(Way(ConduitRouting.PlanWaypoints(0, 0, 3, 0, ConduitAngleLock.Deg90, ConduitRunOrientation.Default)) == "(0,0) (3,0)",
    "trecho colinear não cria vértice repetido");

// Caminho 3D da perna (teto×parede e ângulo de parede).
string Way3(System.Collections.Generic.List<(double X, double Y, double Z)> p) => string.Join(" ", p.Select(v => $"({N(v.X)},{N(v.Y)},{N(v.Z)})"));
Check(Way3(ConduitRouting.LegWaypoints(0, 0, 0, 3, 2, 1.3, 2.8, ConduitAngleLock.Deg90, ConduitRunOrientation.Default, ConduitAngleLock.Deg90, true))
    == "(0,0,0) (0,0,2.8) (3,0,2.8) (3,2,2.8) (3,2,1.3)",
    "teto 90°/parede 90°: sobe reto, L no teto e desce reto");
Check(Way3(ConduitRouting.LegWaypoints(0, 0, 0, 3, 2, 1.3, 2.8, ConduitAngleLock.Deg90, ConduitRunOrientation.Default, ConduitAngleLock.Free, true))
    == "(0,0,0) (3,0,2.8) (3,2,1.3)",
    "parede livre: diagonal direto ao vértice do teto e ao dispositivo");
Check(Way3(ConduitRouting.LegWaypoints(0, 0, 0, 3, 0, 1.3, 2.8, ConduitAngleLock.Deg90, ConduitRunOrientation.Default, ConduitAngleLock.Deg90, false))
    == "(0,0,0) (3,0,0) (3,0,1.3)",
    "mesma parede 90°: anda em planta e depois sobe");
Check(Way3(ConduitRouting.LegWaypoints(0, 0, 0, 3, 0, 1.3, 2.8, ConduitAngleLock.Deg90, ConduitRunOrientation.Default, ConduitAngleLock.Free, false))
    == "(0,0,0) (3,0,1.3)",
    "mesma parede livre: trecho direto (diagonal)");

// Enquadramento do padrão de entrada por critério (carga instalada kW × demanda kVA).
var legacyEntrance = new DimensioningSettings();
Check(legacyEntrance.EntranceFor(10, 999) == "Monofásico — disjuntor 63 A",
    "categoria sem prefixo enquadra por demanda e ignora a carga instalada");
Check(legacyEntrance.EntranceFor(200, 200).StartsWith("Acima da tabela"),
    "acima da última faixa retorna aviso de consulta à concessionária");
var neo = new DimensioningSettings();
UtilityPreset.ByName("Neoenergia Brasília (380/220 V)").ApplyTo(neo);
Check(neo.SupplySystem == "380/220V" && neo.RegisterUtility && neo.EntranceCategories.Count == 9,
    "preset Neoenergia Brasília aplica tensão e categorias");
Check(neo.EntranceFor(6, 8).StartsWith("M2"), "carga instalada ≤10 kW enquadra em M2 (monofásico)");
Check(neo.EntranceFor(9, 13).StartsWith("M3"), "carga instalada entre 10 e 15 kW enquadra em M3 (monofásico)");
Check(neo.EntranceFor(20, 30).StartsWith("T6"), "carga acima do monofásico cai no trifásico por demanda (T6)");
Check(neo.EntranceFor(25, 30).StartsWith("T7"), "trifásico enquadra pela demanda em kVA (T7)");
Check(neo.EntranceFor(70, 80).StartsWith("T12"), "demanda de 70 kVA ainda cabe em T12 (≤75 kVA)");
Check(neo.EntranceFor(80, 90).StartsWith("Acima da tabela"), "demanda acima de T12 exige consulta especial");

Console.WriteLine($"{checks} verificações aprovadas.");
