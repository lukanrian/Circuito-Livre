using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>Renomeia as instâncias de vínculo com um padrão (disciplina detectada pelo nome do arquivo).</summary>
    [Transaction(TransactionMode.Manual)]
    public class NameLinksCommand : IExternalCommand
    {
        private static readonly (string Key, string Discipline)[] Disciplines =
        {
            ("ARQ", "ARQUITETURA"), ("EST", "ESTRUTURA"), ("HID", "HIDRÁULICA"), ("HSS", "HIDROSSANITÁRIO"),
            ("SAN", "SANITÁRIO"), ("ELE", "ELÉTRICA"), ("TEL", "TELECOM"), ("CLI", "CLIMATIZAÇÃO"),
            ("AVAC", "CLIMATIZAÇÃO"), ("HVAC", "CLIMATIZAÇÃO"), ("PCI", "INCÊNDIO"), ("INC", "INCÊNDIO"),
            ("GAS", "GÁS"), ("SPDA", "SPDA"), ("PAI", "PAISAGISMO"), ("FUN", "FUNDAÇÃO"),
        };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().ToList();
            if (links.Count == 0)
            {
                TaskDialog.Show("Nomear vínculos", "Não há vínculos no projeto.");
                return Result.Cancelled;
            }

            List<CheckItem> items = links.Select(l => new CheckItem($"{FileName(doc, l)}   ({l.Name})", l, true)).ToList();
            var window = new ChecklistWindow("Nomear vínculos",
                    "Marque os vínculos a renomear. Campos do padrão: {DISCIPLINA} (detectada pelo nome do arquivo), {ARQUIVO}, {N} (número sequencial).",
                    items, "Renomear")
                .WithText("Padrão:", "{DISCIPLINA} - {ARQUIVO}");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            int done = 0, n = 0;
            var notes = new List<string>();
            using (var t = new Transaction(doc, "Nomear vínculos"))
            {
                t.Start();
                foreach (CheckItem item in window.CheckedItems)
                {
                    var link = (RevitLinkInstance)item.Tag;
                    string file = FileName(doc, link);
                    n++;
                    string name = window.Text
                        .Replace("{DISCIPLINA}", Discipline(file))
                        .Replace("{ARQUIVO}", file)
                        .Replace("{N}", n.ToString("00"))
                        .Trim();
                    Parameter p = link.get_Parameter(BuiltInParameter.RVT_LINK_INSTANCE_NAME);
                    try
                    {
                        if (p != null && !p.IsReadOnly && p.Set(name)) done++;
                        else notes.Add($"{file}: nome não pôde ser alterado.");
                    }
                    catch (Exception ex) { notes.Add($"{file}: {ex.Message}"); }
                }
                t.Commit();
            }

            TaskDialog.Show("Nomear vínculos", $"{done} vínculo(s) renomeado(s)." + (notes.Count > 0 ? "\n\n" + RevitHelpers.Join(notes) : ""));
            return Result.Succeeded;
        }

        internal static string FileName(Document doc, RevitLinkInstance link)
        {
            string name = (doc.GetElement(link.GetTypeId()) as RevitLinkType)?.Name ?? link.Name;
            return Path.GetFileNameWithoutExtension(name.Split(':')[0].Trim());
        }

        internal static string Discipline(string file)
        {
            string upper = file.ToUpperInvariant();
            foreach (var (key, discipline) in Disciplines)
                if (Regex.IsMatch(upper, $@"(^|[^A-Z]){key}([^A-Z]|$)")) return discipline;
            return "VÍNCULO";
        }
    }

    /// <summary>Copia um vínculo (ex.: pavimento tipo) para outros níveis, deslocando na vertical.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CopyLinksToLevelsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            RevitLinkInstance link = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<RevitLinkInstance>().FirstOrDefault();
            if (link == null)
            {
                try
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, new LinkFilter(), "Selecione o vínculo a copiar");
                    link = (RevitLinkInstance)doc.GetElement(r);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }

            List<Level> levels = RevitHelpers.Levels(doc);
            List<CheckItem> targets = levels.Select(l => new CheckItem($"{l.Name}  ({RevitHelpers.ToM(l.ProjectElevation):N2} m)", l)).ToList();
            List<CheckItem> bases = levels.Select(l => new CheckItem(l.Name, l)).ToList();
            int baseIndex = Math.Max(0, levels.FindIndex(l => l.Id == (doc.ActiveView as ViewPlan)?.GenLevel?.Id));

            var window = new ChecklistWindow("Copiar vínculos entre pavimentos",
                    $"Vínculo: {link.Name}\nEscolha o nível em que ele está posicionado hoje e marque os níveis que devem receber uma cópia.",
                    targets, "Copiar")
                .WithCombo("Nível de origem:", bases, baseIndex);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var baseLevel = (Level)window.ComboSelection.Tag;
            int copies = 0;
            using (var t = new Transaction(doc, "Copiar vínculos entre pavimentos"))
            {
                t.Start();
                foreach (CheckItem item in window.CheckedItems)
                {
                    var level = (Level)item.Tag;
                    if (level.Id == baseLevel.Id) continue;
                    var offset = new XYZ(0, 0, level.ProjectElevation - baseLevel.ProjectElevation);
                    ElementTransformUtils.CopyElement(doc, link.Id, offset);
                    copies++;
                }
                t.Commit();
            }

            TaskDialog.Show("Copiar vínculos entre pavimentos", $"{copies} cópia(s) do vínculo criada(s).");
            return Result.Succeeded;
        }

        private class LinkFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is RevitLinkInstance;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }

    /// <summary>
    /// Configura, nas plantas do projeto, a exibição de um vínculo: "pela vista vinculada" usando a planta
    /// do vínculo do mesmo nível (por elevação) e, opcionalmente, em meio-tom.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ConfigureLinkedViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            if (links.Count == 0)
            {
                TaskDialog.Show("Configurar vistas vinculadas", "Não há vínculos carregados.");
                return Result.Cancelled;
            }

            List<ViewPlan> plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
                .OrderBy(v => v.GenLevel.ProjectElevation).ThenBy(v => v.Name).ToList();

            List<CheckItem> items = plans.Select(v => new CheckItem($"{v.Name}  [{v.GenLevel.Name}]", v,
                v.ViewTemplateId == ElementId.InvalidElementId)).ToList();
            var window = new ChecklistWindow("Configurar vistas vinculadas",
                    "Marque as plantas do projeto. Plantas com modelo de vista aplicado podem ignorar a alteração (o modelo controla os vínculos).",
                    items, "Configurar")
                .WithCombo("Vínculo:", links.Select(l => new CheckItem(l.Name, l)).ToList())
                .WithOption1("Mostrar pela planta do vínculo no mesmo nível (por vista vinculada)", true)
                .WithOption2("Vínculo em meio-tom", true);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var link = (RevitLinkInstance)window.ComboSelection.Tag;
            Document linkDoc = link.GetLinkDocument();
            double dz = link.GetTotalTransform().Origin.Z;
            List<ViewPlan> linkedPlans = new FilteredElementCollector(linkDoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null).ToList();

            int done = 0;
            var notes = new List<string>();
            using (var t = new Transaction(doc, "Configurar vistas vinculadas"))
            {
                t.Start();
                foreach (CheckItem item in window.CheckedItems)
                {
                    var view = (ViewPlan)item.Tag;
                    try
                    {
                        if (window.Option1Checked)
                        {
                            double elevation = view.GenLevel.ProjectElevation;
                            ViewPlan match = linkedPlans
                                .Where(v => v.ViewType == view.ViewType && Math.Abs(v.GenLevel.ProjectElevation + dz - elevation) < RevitHelpers.M(0.05))
                                .OrderBy(v => v.Name.Length)
                                .FirstOrDefault();
                            if (match == null)
                            {
                                notes.Add($"{view.Name}: o vínculo não tem planta neste nível.");
                            }
                            else
                            {
                                var settings = new RevitLinkGraphicsSettings
                                {
                                    LinkVisibilityType = LinkVisibility.ByLinkView,
                                    LinkedViewId = match.Id,
                                };
                                view.SetLinkOverrides(link.Id, settings);
                            }
                        }

                        if (window.Option2Checked)
                        {
                            OverrideGraphicSettings ogs = view.GetElementOverrides(link.Id);
                            ogs.SetHalftone(true);
                            view.SetElementOverrides(link.Id, ogs);
                        }
                        done++;
                    }
                    catch (Exception ex) { notes.Add($"{view.Name}: {ex.Message}"); }
                }
                t.Commit();
            }

            TaskDialog.Show("Configurar vistas vinculadas", $"{done} vista(s) configurada(s)." + (notes.Count > 0 ? "\n\n" + RevitHelpers.Join(notes) : ""));
            return Result.Succeeded;
        }
    }
}
