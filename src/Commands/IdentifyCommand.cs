using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Preenche a identificação (ex.: QD1-05) e o nível nos circuitos de força/telecom e nos dispositivos ligados a eles.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class IdentifyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) return Result.Cancelled;

            AutoIdOptions saved = AutoIdOptions.Read(doc);
            var window = new IdentifyWindow(saved);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;

            // Quadros
            List<FamilyInstance> panels;
            if (window.AllPanels)
            {
                panels = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(CircuitDeviceFilter.IsPanel)
                    .ToList();
            }
            else
            {
                panels = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilyInstance>()
                    .Where(CircuitDeviceFilter.IsPanel)
                    .ToList();
                if (panels.Count == 0)
                {
                    try
                    {
                        panels = uidoc.Selection.PickObjects(ObjectType.Element, new PanelFilter(),
                                "Selecione os quadros/painéis e clique em Concluir")
                            .Select(r => doc.GetElement(r))
                            .OfType<FamilyInstance>()
                            .ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                }
            }

            if (panels.Count == 0)
            {
                TaskDialog.Show("Preencher identificação", "Nenhum quadro encontrado.");
                return Result.Cancelled;
            }

            var panelIds = new HashSet<ElementId>(panels.Select(p => p.Id));
            List<ElectricalSystem> circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment != null && panelIds.Contains(c.BaseEquipment.Id))
                .ToList();

            int circuitCount = 0, deviceCount = 0;
            var notes = new List<string>();

            using (var t = new Transaction(doc, "Preencher identificação"))
            {
                t.Start();
                try
                {
                    SharedParameters.EnsureIdentification(doc);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    TaskDialog.Show("Preencher identificação", $"Não foi possível criar os parâmetros do plugin:\n{ex.Message}");
                    return Result.Failed;
                }
                doc.Regenerate();

                if (window.AutoUpdate || saved?.Enabled == true)
                {
                    try
                    {
                        new AutoIdOptions
                        {
                            Enabled = window.AutoUpdate,
                            Pattern = window.Pattern,
                            IncludeDevices = window.IncludeDevices,
                            FillLevel = window.FillLevel,
                        }.Write(doc);
                    }
                    catch (Exception ex) { notes.Add("Não foi possível gravar a identificação automática no projeto: " + ex.Message); }
                }

                var deviceSeen = new HashSet<ElementId>();
                foreach (ElectricalSystem c in circuits)
                {
                    List<Element> devices = c.Elements.Cast<Element>().ToList();
                    string levelName = LevelName(doc, devices.FirstOrDefault()) ?? LevelName(doc, c.BaseEquipment);
                    string id = IdentificationFormatter.Format(window.Pattern, c.PanelName, c.CircuitNumber, c.LoadName, levelName);

                    if (Set(c, SharedParameters.IdentificationGuid, id)) circuitCount++;
                    if (window.FillLevel) Set(c, SharedParameters.LevelGuid, levelName);

                    if (!window.IncludeDevices) continue;
                    foreach (Element d in devices)
                    {
                        Element device = doc.GetElement(d.Id);
                        if (device == null) continue;
                        if (!deviceSeen.Add(device.Id))
                            notes.Add($"{device.Name} (id {device.Id}) está em mais de um circuito; ficou com \"{id}\".");

                        string deviceLevel = LevelName(doc, device) ?? levelName;
                        string deviceId = IdentificationFormatter.Format(window.Pattern, c.PanelName, c.CircuitNumber, c.LoadName, deviceLevel);
                        if (Set(device, SharedParameters.IdentificationGuid, deviceId)) deviceCount++;
                        if (window.FillLevel) Set(device, SharedParameters.LevelGuid, deviceLevel);
                    }
                }

                t.Commit();
            }

            var report = new TaskDialog("Preencher identificação")
            {
                MainInstruction = $"{circuitCount} circuito(s) e {deviceCount} dispositivo(s) identificados em {panels.Count} quadro(s).",
                MainContent = $"Parâmetros: {SharedParameters.IdentificationName}" + (window.FillLevel ? $" e {SharedParameters.LevelName}" : "") +
                              " (grupo Dados de identidade). Use-os nas suas tags e tabelas." +
                              (window.AutoUpdate ? "\n\nIdentificação automática ligada neste projeto: os circuitos se atualizam sozinhos ao mudar."
                                  : saved?.Enabled == true ? "\n\nIdentificação automática desligada neste projeto." : ""),
            };
            if (notes.Count > 0) report.ExpandedContent = string.Join("\n", notes.Take(50));
            report.Show();

            return Result.Succeeded;
        }

        private static bool Set(Element e, Guid guid, string value)
        {
            Parameter p = e?.get_Parameter(guid);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value ?? string.Empty); } catch (Exception) { return false; }
        }

        internal static string LevelName(Document doc, Element e)
        {
            if (e == null) return null;
            ElementId levelId = e.LevelId;
            if ((levelId == null || levelId == ElementId.InvalidElementId) && e is FamilyInstance fi && fi.Host != null)
                levelId = fi.Host.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                Parameter p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                              ?? e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (p != null && p.StorageType == StorageType.ElementId) levelId = p.AsElementId();
            }
            if (levelId == null || levelId == ElementId.InvalidElementId) return null;
            return (doc.GetElement(levelId) as Level)?.Name;
        }
    }
}
