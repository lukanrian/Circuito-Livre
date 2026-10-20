using RafaelElectrical.Services;

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
Console.WriteLine($"{checks} verificações aprovadas.");
