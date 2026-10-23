using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Cria circuitos (força, dados, telefone, comunicação, segurança, alarme...) a partir de dispositivos
    /// selecionados e os conecta a um quadro/painel. O tipo de sistema é detectado pelos conectores livres.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateCircuitsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            var deviceFilter = new CircuitDeviceFilter();

            // 1. Dispositivos: pré-seleção válida ou seleção agora
            List<ElementId> deviceIds = uidoc.Selection.GetElementIds()
                .Where(id => deviceFilter.AllowElement(doc.GetElement(id)))
                .ToList();

            FamilyInstance panel;
            try
            {
                if (deviceIds.Count == 0)
                {
                    IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, deviceFilter,
                        "Selecione os dispositivos sem circuito (força ou telecom) e clique em Concluir");
                    deviceIds = refs.Select(r => r.ElementId).ToList();
                }
                if (deviceIds.Count == 0) return Result.Cancelled;

                // 2. Tipo de sistema
                ElectricalSystemType? chosen = ChooseSystemType(doc, deviceIds);
                if (chosen == null) return Result.Cancelled;
                ElectricalSystemType systemType = chosen.Value;

                deviceIds = deviceIds
                    .Where(id => CircuitDeviceFilter.GetFreeSystemTypes(doc.GetElement(id)).Contains(systemType))
                    .ToList();

                // 3. Quadro
                Reference panelRef = uidoc.Selection.PickObject(ObjectType.Element, new PanelFilter(),
                    $"Selecione o quadro/painel que vai alimentar os circuitos de {CircuitDeviceFilter.DisplayName(systemType)}");
                panel = (FamilyInstance)doc.GetElement(panelRef);

                return CreateCircuits(uidoc, panel, deviceIds, systemType);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
        }

        private static ElectricalSystemType? ChooseSystemType(Document doc, List<ElementId> deviceIds)
        {
            List<(ElectricalSystemType Type, int Count)> types = deviceIds
                .SelectMany(id => CircuitDeviceFilter.GetFreeSystemTypes(doc.GetElement(id)))
                .GroupBy(t => t)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(x => x.Item2)
                .ToList();

            if (types.Count == 0) return null;
            if (types.Count == 1) return types[0].Type;

            var dialog = new TaskDialog("Criar circuitos")
            {
                MainInstruction = "Os dispositivos selecionados têm conectores de mais de um sistema.",
                MainContent = "Qual tipo de circuito deseja criar agora?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            var links = new[]
            {
                TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2,
                TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4,
            };
            var results = new[]
            {
                TaskDialogResult.CommandLink1, TaskDialogResult.CommandLink2,
                TaskDialogResult.CommandLink3, TaskDialogResult.CommandLink4,
            };

            int n = Math.Min(4, types.Count);
            for (int i = 0; i < n; i++)
                dialog.AddCommandLink(links[i], CircuitDeviceFilter.DisplayName(types[i].Type), $"{types[i].Count} dispositivo(s)");

            TaskDialogResult r = dialog.Show();
            int index = Array.IndexOf(results, r);
            return index >= 0 && index < n ? types[index].Type : null;
        }

        private static Result CreateCircuits(UIDocument uidoc, FamilyInstance panel, List<ElementId> deviceIds, ElectricalSystemType systemType)
        {
            Document doc = uidoc.Document;
            string typeName = CircuitDeviceFilter.DisplayName(systemType);

            var dialog = new TaskDialog("Criar circuitos")
            {
                MainInstruction = $"{deviceIds.Count} dispositivo(s) de {typeName} → \"{panel.Name}\"",
                MainContent = "Como deseja criar os circuitos?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Um único circuito com todos os dispositivos");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Um circuito para cada dispositivo");

            List<List<ElementId>> groups;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: groups = new List<List<ElementId>> { deviceIds }; break;
                case TaskDialogResult.CommandLink2: groups = deviceIds.Select(id => new List<ElementId> { id }).ToList(); break;
                default: return Result.Cancelled;
            }

            int created = 0;
            var errors = new List<string>();
            var newIds = new List<ElementId>();

            using (var t = new Transaction(doc, $"Criar circuitos de {typeName}"))
            {
                t.Start();
                foreach (List<ElementId> group in groups)
                {
                    string label = string.Join(", ", group.Select(id => doc.GetElement(id)?.Name ?? id.ToString()));
                    try
                    {
                        ElectricalSystem circuit = ElectricalSystem.Create(doc, group, systemType);
                        if (circuit == null)
                        {
                            errors.Add($"{label}: o Revit não conseguiu criar o circuito.");
                            continue;
                        }
                        newIds.Add(circuit.Id);
                        created++;

                        try { circuit.SelectPanel(panel); }
                        catch (Exception ex) { errors.Add($"{label}: circuito criado, mas não foi ligado ao quadro ({ex.Message})."); }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{label}: {ex.Message}");
                    }
                }
                t.Commit();
            }

            if (newIds.Count > 0) uidoc.Selection.SetElementIds(newIds);

            var report = new TaskDialog("Criar circuitos")
            {
                MainInstruction = $"{created} circuito(s) de {typeName} criado(s) em \"{panel.Name}\".",
            };
            if (errors.Count > 0)
            {
                report.MainContent = $"{errors.Count} aviso(s). Veja os detalhes abaixo.";
                report.ExpandedContent = string.Join("\n", errors);
            }
            report.Show();

            return created > 0 ? Result.Succeeded : Result.Failed;
        }
    }
}
