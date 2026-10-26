using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>Identificadores de fiação nos eletrodutos/eletrocalhas da vista ativa.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateWiringTagsCommand : CreateTagsCommand
    {
        protected override TaskDialogResult? Preset => TaskDialogResult.CommandLink3;
    }

    /// <summary>
    /// Central de documentação: roda em sequência o roteamento dos quadros (fiação e quantitativos nos trechos),
    /// os identificadores de fiação na vista ativa e o diagrama unifilar.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CentralDocumentationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(CircuitDeviceFilter.IsPanel)
                .ToList();
            var powerCircuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.PowerCircuit && c.Elements.Size > 0)
                .ToList();
            var circuits = powerCircuits.Where(c => c.BaseEquipment != null).ToList();
            if (panels.Count == 0 || circuits.Count == 0)
            {
                TaskDialog.Show("Central de documentação", "Insira um quadro de distribuição e atribua a ele ao menos um circuito de força antes de gerar a documentação.");
                return Result.Cancelled;
            }

            var panelIds = panels.Select(p => p.Id.Value).ToHashSet();
            var supply = ProjectSizingService.AnalyzeSupply(circuits.Select(c => c.BaseEquipment.Id.Value).Where(panelIds.Contains),
                powerCircuits.SelectMany(c => c.Elements.Cast<Element>().Where(e => panelIds.Contains(e.Id.Value))
                    .Select(e => (c.BaseEquipment?.Id.Value, e.Id.Value))));
            string PanelNames(IEnumerable<long> ids) => string.Join(", ", panels.Where(p => ids.Contains(p.Id.Value))
                .Select(p => p.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? p.Name).OrderBy(n => n));
            if (supply.Unassigned.Count > 0)
            {
                TaskDialog.Show("Central de documentação", "Os quadros " + PanelNames(supply.Unassigned) +
                    " têm circuito alimentador criado, mas sem painel de origem atribuído. Selecione o painel que alimenta esses circuitos. " +
                    "O primeiro equipamento da instalação pode ficar sem circuito alimentador.");
                return Result.Cancelled;
            }
            if (supply.Origins.Count == 0)
            {
                TaskDialog.Show("Central de documentação", "Não foi encontrada uma origem da instalação. Confira se há ligações de alimentação formando um ciclo entre os quadros.");
                return Result.Cancelled;
            }

            var routing = new RoutingService(doc);
            routing.RouteCircuits(circuits);
            var incomplete = routing.Routes.Where(r => !r.Routed || r.DevicesReached < r.DevicesTotal).ToList();
            var withoutCircuit = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[] {
                    BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures,
                    BuiltInCategory.OST_LightingDevices }))
                .WhereElementIsNotElementType().OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => CircuitDeviceFilter.GetFreeSystemTypes(fi).Contains(ElectricalSystemType.PowerCircuit))
                .ToList();
            if (incomplete.Count > 0 || withoutCircuit.Count > 0)
            {
                var warning = new TaskDialog("Avisos do roteamento")
                {
                    MainInstruction = "Há pontos sem circuito ou sem caminho completo na infraestrutura.",
                    MainContent = $"{withoutCircuit.Count} dispositivo(s) sem circuito; {incomplete.Count} circuito(s) com rota incompleta.\n\n" +
                        "Dispositivos sem circuito: " + RevitHelpers.Join(withoutCircuit.Select(fi => fi.Id.ToString()), 12) +
                        "\nCircuitos incompletos: " + RevitHelpers.Join(incomplete.Select(r => RoutingService.Label(r.Circuit)), 12),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                warning.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continuar mesmo assim");
                if (warning.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
            }

            var form = new FormWindow("Central de documentação", "Escolha as etapas. Cada uma abre a própria janela, na ordem abaixo.");
            form.Note("Origens da instalação: " + PanelNames(supply.Origins) +
                ". Esses equipamentos não precisam de painel a montante. Confira se todas as origens listadas são intencionais.");
            var route = form.Check("1. Atualizar quadros: rotear os circuitos e gravar a fiação e o quantitativo nos trechos (CL_Cabos)", true);
            var tags = form.Check("2. Criar identificadores de fiação na vista ativa (textos do plugin, com visibilidade e legenda)", true);
            var familyTags = form.Check("2b. Usar famílias de identificador de eletroduto (CL_Cabos) em vez dos textos", false);
            var diagram = form.Check("3. Gerar o diagrama unifilar (e multifilar/DR, conforme as Configurações) dos quadros", false);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            Result last = Result.Cancelled;
            if (route.IsChecked == true)
            {
                last = new RouteCircuitsCommand().Execute(data, ref message, elements);
                if (last == Result.Failed) return last;
            }
            if (tags.IsChecked == true)
            {
                var cfg = DimensioningSettings.Load();
                List<View> mapped = cfg.TagViewByLevel.Values.Distinct()
                    .Select(name => new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .FirstOrDefault(v => !v.IsTemplate && v.Name == name))
                    .Where(v => v != null).Cast<View>().ToList();
                if (mapped.Count > 0 && familyTags.IsChecked != true)
                {
                    var wiring = new WiringAnnotations(doc);
                    WiringViews.Regenerate(doc, wiring, mapped, "Identificadores de fiação");
                    last = Result.Succeeded;
                }
                else last = familyTags.IsChecked == true
                    ? new CreateWiringTagsCommand().Execute(data, ref message, elements)
                    : new WiringTagsCommand().Execute(data, ref message, elements);
            }
            if (diagram.IsChecked == true) last = new SingleLineDiagramCommand().Execute(data, ref message, elements);
            return last == Result.Failed ? Result.Failed : Result.Succeeded;
        }
    }
}
