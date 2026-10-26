using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;

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
            List<ElementId> rawIds = uidoc.Selection.GetElementIds()
                .Where(id => deviceFilter.AllowElement(doc.GetElement(id)))
                .ToList();

            FamilyInstance panel;
            try
            {
                if (rawIds.Count == 0)
                {
                    IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, deviceFilter,
                        "Selecione os dispositivos sem circuito (força ou telecom) e clique em Concluir");
                    rawIds = refs.Select(r => r.ElementId).ToList();
                }
                if (rawIds.Count == 0) return Result.Cancelled;

                // Resolve o que foi clicado para as unidades realmente circuitáveis: se a família-pai não tem
                // conector próprio, usa os módulos aninhados que têm; evita duplicar a mesma unidade.
                List<ElementId> deviceIds = ResolveUnits(doc, rawIds);
                if (deviceIds.Count == 0)
                {
                    TaskDialog.Show("Criar circuitos",
                        "Nenhum dos elementos selecionados (nem seus módulos) tem conector elétrico livre para criar circuito.");
                    return Result.Cancelled;
                }

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

                return CreateCircuits(uidoc, panel, rawIds, deviceIds, systemType);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
        }

        /// <summary>Converte os elementos clicados nas instâncias que de fato têm conector livre (sem duplicar).</summary>
        private static List<ElementId> ResolveUnits(Document doc, IEnumerable<ElementId> rawIds)
        {
            var units = new List<ElementId>();
            var seen = new HashSet<ElementId>();
            foreach (ElementId id in rawIds)
                foreach (FamilyInstance u in CircuitDeviceFilter.CircuitableUnits(doc.GetElement(id)))
                    if (seen.Add(u.Id)) units.Add(u.Id);
            return units;
        }

        /// <summary>Diagnóstico do elemento: categoria e conectores elétricos do próprio e dos módulos aninhados.</summary>
        private static string Describe(Document doc, ElementId id)
        {
            if (doc.GetElement(id) is not FamilyInstance fi) return id.ToString();
            string cat = fi.Category?.Name ?? "sem categoria";

            var cons = new List<string>();
            var stack = new Stack<FamilyInstance>();
            stack.Push(fi);
            var walked = new HashSet<ElementId>();
            while (stack.Count > 0)
            {
                FamilyInstance cur = stack.Pop();
                if (!walked.Add(cur.Id)) continue;
                ConnectorManager cm = cur.MEPModel?.ConnectorManager;
                if (cm != null)
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.Domain != Domain.DomainElectrical) continue;
                        string st; try { st = CircuitDeviceFilter.Normalize(c.ElectricalSystemType).ToString(); } catch { st = "?"; }
                        string used; try { used = c.IsConnected ? "ligado" : "livre"; } catch { used = "?"; }
                        string origem = cur.Id == fi.Id ? "" : $" ({cur.Name})";
                        cons.Add($"{st}/{used}{origem}");
                    }
                foreach (ElementId sid in cur.GetSubComponentIds())
                    if (cur.Document.GetElement(sid) is FamilyInstance sub) stack.Push(sub);
            }
            string conText = cons.Count > 0 ? string.Join(", ", cons) : "nenhum conector elétrico";
            return $"\"{fi.Name}\" [{cat}; {conText}]";
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

        /// <summary>Cria cada grupo integralmente; uma falha no quadro preserva o circuito sem atribuição.</summary>
        public static (List<ElementId> Ids, int Connected, List<string> Errors) RunCreate(Document doc,
            FamilyInstance panel, IEnumerable<IEnumerable<ElementId>> groups, ElectricalSystemType systemType)
        {
            var ids = new List<ElementId>();
            var errors = new List<string>();
            var seen = new HashSet<ElementId>();
            int connected = 0;
            using var undo = new TransactionGroup(doc, "Criar circuitos");
            undo.Start();
            foreach (IEnumerable<ElementId> rawGroup in groups)
            {
                List<ElementId> units = ResolveUnits(doc, rawGroup)
                    .Where(id => CircuitDeviceFilter.GetFreeSystemTypes(doc.GetElement(id)).Contains(systemType))
                    .Where(id => !seen.Contains(id)).ToList();
                if (units.Count == 0)
                {
                    errors.Add("Grupo sem módulo/dispositivo com conector livre para o sistema escolhido, ou já incluído em outro grupo.");
                    continue;
                }
                string label = string.Join(", ", units.Select(id => doc.GetElement(id).Name));
                ElementId created = ElementId.InvalidElementId;
                bool ok = RollbackOnError.Try(doc, "Criar circuito pelos módulos", () =>
                {
                    var fi = (FamilyInstance)doc.GetElement(units[0]);
                    Connector connector = fi.MEPModel.ConnectorManager.UnusedConnectors.Cast<Connector>()
                        .First(c => c.Domain == Domain.DomainElectrical && CircuitDeviceFilter.Normalize(c.ElectricalSystemType) == systemType);
                    ElectricalSystem circuit = ElectricalSystem.Create(connector, systemType);
                    if (circuit == null) throw new InvalidOperationException("O Revit não criou o circuito pelo conector livre.");
                    if (units.Count > 1)
                    {
                        var remaining = new ElementSet();
                        foreach (ElementId id in units.Skip(1)) remaining.Insert(doc.GetElement(id));
                        if (!circuit.AddToCircuit(remaining))
                            throw new InvalidOperationException("O Revit recusou incluir todos os módulos no mesmo circuito.");
                    }
                    doc.Regenerate();
                    var members = circuit.Elements.Cast<Element>().Select(e => e.Id).ToHashSet();
                    if (units.Any(id => !members.Contains(id)))
                        throw new InvalidOperationException("O circuito ficou incompleto; o grupo foi desfeito para não omitir cargas.");
                    created = circuit.Id;
                    return true;
                }, out string failure);
                if (!ok)
                {
                    errors.Add($"{label}: {failure}\nDiagnóstico: {string.Join("; ", units.Select(id => Describe(doc, id)))}");
                    continue;
                }
                ids.Add(created);
                seen.UnionWith(units);
                var system = (ElectricalSystem)doc.GetElement(created);
                bool assigned = RollbackOnError.Try(doc, "Ligar circuito ao quadro", () =>
                {
                    system.SelectPanel(panel);
                    return system.BaseEquipment?.Id == panel.Id;
                }, out string panelFailure);
                if (assigned) connected++;
                else
                {
                    double volts = UnitUtils.ConvertFromInternalUnits(system.Voltage, UnitTypeId.Volts);
                    string distribution = panel.get_Parameter(BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM)?.AsValueString();
                    errors.Add($"{label}: circuito criado sem ligação ao quadro. {panelFailure}\n" +
                        $"Circuito: {volts:0.##} V, {system.PolesNumber} polo(s). Quadro: {distribution ?? panel.Name}. " +
                        "Confira tensão, polos e sistema de distribuição; os dados das famílias foram preservados.");
                }
            }
            undo.Assimilate();
            return (ids, connected, errors);
        }

        private static Result CreateCircuits(UIDocument uidoc, FamilyInstance panel, List<ElementId> rawIds,
            List<ElementId> deviceIds, ElectricalSystemType systemType)
        {
            Document doc = uidoc.Document;
            string typeName = CircuitDeviceFilter.DisplayName(systemType);

            var dialog = new TaskDialog("Criar circuitos")
            {
                MainInstruction = $"{deviceIds.Count} módulo(s)/dispositivo(s) de {typeName} → \"{panel.Name}\"",
                MainContent = "Como deseja criar os circuitos?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Um único circuito com todos os dispositivos");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Um circuito para cada dispositivo");

            List<List<ElementId>> groups;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: groups = new List<List<ElementId>> { rawIds }; break;
                case TaskDialogResult.CommandLink2: groups = rawIds.Select(id => new List<ElementId> { id }).ToList(); break;
                default: return Result.Cancelled;
            }

            var result = RunCreate(doc, panel, groups, systemType);
            var newIds = result.Ids;
            var errors = result.Errors;
            int created = newIds.Count;
            if (newIds.Count > 0) uidoc.Selection.SetElementIds(newIds);

            var report = new TaskDialog("Criar circuitos")
            {
                MainInstruction = $"{created} circuito(s) de {typeName} criado(s); {result.Connected} ligado(s) ao quadro \"{panel.Name}\".",
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
