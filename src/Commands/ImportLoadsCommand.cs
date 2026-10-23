using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Importar cargas: lê no modelo vinculado (ar-condicionado, bombas, aquecedores, chuveiros, motores...) os
    /// equipamentos que têm potência e cria, na mesma posição, um ponto de carga do modelo elétrico com essa potência.
    /// Rodar de novo atualiza a potência dos já importados (RE_Origem) e cria só os novos.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportLoadsCommand : IExternalCommand
    {
        private const string Title = "Importar cargas do vínculo";

        private static readonly BuiltInCategory[] SourceCategories =
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_PlumbingEquipment,
            BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_GenericModel,
        };

        private class Source
        {
            public FamilyInstance Element;
            public double VA;
            public XYZ Point;      // coordenadas do modelo elétrico
            public string Key;     // vínculo + UniqueId
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            if (links.Count == 0)
            {
                TaskDialog.Show(Title, "Nenhum vínculo do Revit carregado neste projeto.");
                return Result.Cancelled;
            }

            List<FamilySymbol> targets = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures ||
                            s.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment)
                .Where(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased ||
                            s.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased)
                .OrderBy(s => s.FamilyName + " " + s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (targets.Count == 0)
            {
                TaskDialog.Show(Title, "Carregue uma família de ponto de força (Dispositivos ou Equipamentos elétricos) que não seja hospedada em parede.");
                return Result.Cancelled;
            }

            // 1. escolher o vínculo
            var linkForm = new FormWindow(Title, "Escolha o modelo vinculado de onde vêm as cargas (ex.: climatização, hidráulica).");
            var linkCombo = linkForm.Combo("Vínculo:", links, l => l.Name);
            RevitHelpers.ShowOwned(linkForm, data);
            if (linkForm.ShowDialog() != true) return Result.Cancelled;
            RevitLinkInstance link = FormWindow.Selected<RevitLinkInstance>(linkCombo);
            Document linkDoc = link.GetLinkDocument();
            Transform tr = link.GetTotalTransform();

            List<Source> sources = new FilteredElementCollector(linkDoc)
                .WherePasses(new ElementMulticategoryFilter(SourceCategories))
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Select(f => new Source
                {
                    Element = f,
                    VA = PowerVA(f),
                    Point = RevitHelpers.PointOf(f) is XYZ p ? tr.OfPoint(p) : null,
                    Key = link.GetTypeId().ToString() + ":" + f.UniqueId,
                })
                .Where(s => s.VA > 0 && s.Point != null)
                .ToList();
            if (sources.Count == 0)
            {
                TaskDialog.Show(Title, "Nenhum equipamento com potência encontrado nesse vínculo " +
                                       "(procuro conector elétrico ou parâmetro de potência aparente/elétrica).");
                return Result.Cancelled;
            }

            // 2. escolher os tipos
            var groups = sources.GroupBy(s => s.Element.Symbol.Id).ToList();
            var items = groups.Select(g =>
            {
                FamilyInstance f = g.First().Element;
                return new CheckItem($"{f.Category?.Name}: {f.Symbol.FamilyName} : {f.Symbol.Name} — {g.Count()} un. — {g.First().VA:0} VA", g.ToList(), true);
            }).OrderBy(i => i.Label).ToList();

            var window = new ChecklistWindow(Title, "Marque os equipamentos que viram pontos de carga no modelo elétrico.", items, "Importar")
                .WithCombo("Família do ponto de carga:", targets.Select(s => new CheckItem($"{s.FamilyName} : {s.Name}", s)).ToList())
                .WithText("Altura do ponto (m) — vazio = altura do equipamento:", "")
                .WithOption1("Atualizar a potência dos pontos já importados antes", true);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var symbol = (FamilySymbol)window.ComboSelection.Tag;
            double? heightM = FormWindow.ParseNumber(window.Text);
            List<Source> chosen = window.CheckedItems.SelectMany(i => (List<Source>)i.Tag).ToList();
            List<Level> levels = RevitHelpers.Levels(doc);
            if (levels.Count == 0) { TaskDialog.Show(Title, "O projeto não tem níveis."); return Result.Cancelled; }

            int created = 0, updated = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                SharedParameters.Ensure(doc, SharedParameters.Origin);
                doc.Regenerate();
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                Dictionary<string, FamilyInstance> existing = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Select(f => (f, key: SharedParameters.GetText(f, SharedParameters.Origin)))
                    .Where(x => !string.IsNullOrEmpty(x.key))
                    .GroupBy(x => x.key).ToDictionary(g => g.Key, g => g.First().f);

                foreach (Source s in chosen)
                {
                    if (existing.TryGetValue(s.Key, out FamilyInstance old))
                    {
                        if (window.Option1Checked && LaunchPointsCommand.SetPower(old, s.VA)) updated++;
                        continue;
                    }
                    try
                    {
                        Level level = levels.LastOrDefault(l => l.ProjectElevation <= s.Point.Z + RevitHelpers.M(0.05)) ?? levels.First();
                        double h = heightM ?? RevitHelpers.ToM(s.Point.Z - level.ProjectElevation);
                        XYZ p = new XYZ(s.Point.X, s.Point.Y, level.ProjectElevation + RevitHelpers.M(h));
                        FamilyInstance fi = doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
                        LaunchPointsCommand.SetHeight(fi, h);
                        LaunchPointsCommand.SetPower(fi, s.VA);
                        SharedParameters.SetText(fi, SharedParameters.Origin, s.Key);
                        fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(
                            $"{s.Element.Symbol.FamilyName} : {s.Element.Symbol.Name} (vínculo)");
                        created++;
                    }
                    catch (Exception) { fail++; }
                }
                t.Commit();
            }

            TaskDialog.Show(Title,
                $"{created} ponto(s) de carga criado(s)" + (updated > 0 ? $", {updated} atualizado(s)" : "") + "." +
                (fail > 0 ? $"\n{fail} não puderam ser criados." : "") +
                "\n\nDepois ligue-os aos quadros com Criar Circuitos. A potência foi gravada no parâmetro de potência da família " +
                "(se ela não aceitar, ajuste o tipo ou crie um parâmetro de instância \"Potência (VA)\").");
            return Result.Succeeded;
        }

        /// <summary>
        /// Potência do equipamento em VA: conectores elétricos (carga aparente), parâmetros de potência aparente (VA)
        /// ou potência elétrica (W, tomada como VA) da instância ou do tipo.
        /// </summary>
        private static double PowerVA(FamilyInstance f)
        {
            double best = 0;
            foreach (Element e in new Element[] { f, f.Symbol })
            {
                if (e == null) continue;
                foreach (Parameter p in e.Parameters)
                {
                    if (p.StorageType != StorageType.Double || !p.HasValue || p.Definition == null) continue;
                    ForgeTypeId spec;
                    try { spec = p.Definition.GetDataType(); } catch (Exception) { continue; }
                    double v = 0;
                    if (spec == SpecTypeId.ApparentPower) v = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.VoltAmperes);
                    else if (spec == SpecTypeId.ElectricalPower) v = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Watts);
                    if (v > best) best = v;
                }
                if (best > 0) return Math.Round(best, 1);
            }
            return best;
        }
    }
}
