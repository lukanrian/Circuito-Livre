using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Dimensiona condutores e disjuntores dos circuitos de força pela NBR 5410
    /// (quadros selecionados ou projeto inteiro) e grava o resultado nos parâmetros RE_ dos circuitos.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DimensionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) return Result.Cancelled;

            // 1. Abrangência
            var scopeDialog = new TaskDialog("Dimensionar")
            {
                MainInstruction = "O que deseja dimensionar?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            scopeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Instalação (quadros que eu selecionar)");
            scopeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Projeto inteiro (todos os circuitos de força)");

            HashSet<ElementId> panelIds = null;
            string scope;
            switch (scopeDialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    List<FamilyInstance> panels = uidoc.Selection.GetElementIds()
                        .Select(id => doc.GetElement(id)).OfType<FamilyInstance>()
                        .Where(CircuitDeviceFilter.IsPanel).ToList();
                    if (panels.Count == 0)
                    {
                        try
                        {
                            panels = uidoc.Selection.PickObjects(ObjectType.Element, new PanelFilter(), "Selecione os quadros e clique em Concluir")
                                .Select(r => doc.GetElement(r)).OfType<FamilyInstance>().ToList();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                    }
                    if (panels.Count == 0) return Result.Cancelled;
                    panelIds = new HashSet<ElementId>(panels.Select(p => p.Id));
                    scope = string.Join(", ", panels.Select(p => p.Name));
                    break;
                case TaskDialogResult.CommandLink2:
                    scope = "projeto inteiro";
                    break;
                default:
                    return Result.Cancelled;
            }

            // 2. Circuitos de força
            List<ElectricalSystem> circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.PowerCircuit)
                .Where(c => panelIds == null || (c.BaseEquipment != null && panelIds.Contains(c.BaseEquipment.Id)))
                .ToList();

            if (circuits.Count == 0)
            {
                TaskDialog.Show("Dimensionar", "Nenhum circuito de força encontrado.");
                return Result.Cancelled;
            }

            Dictionary<ElementId, ElectricalSystem> byId = circuits.ToDictionary(c => c.Id);
            List<CircuitInput> inputs = circuits.Select(ToInput)
                .OrderBy(i => i.Panel, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(i => FirstNumber(i.Number))
                .ToList();

            // 3. Janela
            var window = new DimensionWindow(inputs, DimensioningSettings.Load(), scope);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            window.ShowDialog();
            if (!window.ApplyRequested) return Result.Cancelled;

            // 4. Gravação
            int written = 0, ratingFail = 0;
            using (var t = new Transaction(doc, "Dimensionar circuitos"))
            {
                t.Start();
                try { SharedParameters.EnsureSizing(doc); }
                catch (Exception ex)
                {
                    t.RollBack();
                    TaskDialog.Show("Dimensionar", $"Não foi possível criar os parâmetros do plugin:\n{ex.Message}");
                    return Result.Failed;
                }
                doc.Regenerate();

                foreach (SizingResult r in window.Results)
                {
                    if (!byId.TryGetValue(r.Input.Id, out ElectricalSystem c)) continue;

                    SharedParameters.SetNumber(c, SharedParameters.Current, Math.Round(r.CurrentA, 2));
                    SharedParameters.SetText(c, SharedParameters.SizingStatus,
                        r.Ok ? $"{r.Status} — {r.Criterion}" : r.Status);

                    if (!r.Ok) continue;

                    SharedParameters.SetNumber(c, SharedParameters.Section, r.SectionMm2);
                    SharedParameters.SetNumber(c, SharedParameters.ProtectionSection, r.ProtectionMm2);
                    SharedParameters.SetNumber(c, SharedParameters.NeutralSection, r.NeutralMm2);
                    SharedParameters.SetNumber(c, SharedParameters.TotalDrop, r.TotalDropPercent);
                    SharedParameters.SetNumber(c, SharedParameters.Breaker, r.BreakerA);
                    SharedParameters.SetNumber(c, SharedParameters.VoltageDrop, r.VoltageDropPercent);
                    SharedParameters.SetText(c, SharedParameters.Wiring, r.Wiring);
                    written++;

                    if (window.Settings.UpdateRevitRating)
                    {
                        Parameter rating = c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM);
                        try
                        {
                            if (rating == null || rating.IsReadOnly ||
                                !rating.Set(UnitUtils.ConvertToInternalUnits(r.BreakerA, UnitTypeId.Amperes)))
                                ratingFail++;
                        }
                        catch (Exception) { ratingFail++; }
                    }
                }
                t.Commit();
            }

            var report = new TaskDialog("Dimensionar")
            {
                MainInstruction = $"{written} circuito(s) dimensionado(s) e gravado(s).",
                MainContent = "Resultados nos parâmetros RE_Seção, RE_Seção PE, RE_Disjuntor, RE_Queda de tensão, RE_Fiação e RE_Dimensionamento de cada circuito." +
                              (ratingFail > 0 ? $"\n\n{ratingFail} circuito(s) não aceitaram a alteração do disjuntor nativo do Revit (circuito travado ou valor fora da lista do Revit)." : ""),
            };
            report.Show();
            return Result.Succeeded;
        }

        private static CircuitInput ToInput(ElectricalSystem c)
        {
            List<Element> devices = c.Elements.Cast<Element>().ToList();
            bool lighting = devices.Count > 0 && devices.All(d =>
                d.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                d.Category?.BuiltInCategory == BuiltInCategory.OST_LightingDevices);

            double? real = SharedParameters.GetNumber(c, SharedParameters.RealLength);
            double? realLength = real.HasValue && real.Value > 0 ? real : null;
            double? g = SharedParameters.GetNumber(c, SharedParameters.RealGrouping);
            int? realGrouping = g.HasValue && g.Value >= 1 ? (int)Math.Round(g.Value) : (int?)null;
            double? f = SharedParameters.GetNumber(c, SharedParameters.RealFca);
            double? realFca = realGrouping.HasValue && f.HasValue && f.Value > 0 && f.Value <= 1 ? f : null;
            Element fed = devices.FirstOrDefault(CircuitDeviceFilter.IsPanel);

            return new CircuitInput
            {
                Id = c.Id,
                Panel = string.IsNullOrWhiteSpace(c.PanelName) ? "(sem quadro)" : c.PanelName,
                Number = c.CircuitNumber,
                LoadName = c.LoadName,
                VoltageV = Safe(() => UnitUtils.ConvertFromInternalUnits(c.Voltage, UnitTypeId.Volts)),
                Poles = Math.Max(1, Safe(() => c.PolesNumber)),
                ApparentLoadVA = Safe(() => UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes)),
                CurrentA = Safe(() => UnitUtils.ConvertFromInternalUnits(c.ApparentCurrent, UnitTypeId.Amperes)),
                LengthM = realLength ?? Safe(() => UnitUtils.ConvertFromInternalUnits(c.Length, UnitTypeId.Meters)),
                RealLength = realLength.HasValue,
                GroupingOverride = realGrouping,
                FcaOverride = realFca,
                FeedsPanel = fed?.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? fed?.Name,
                IsLighting = lighting,
                IgnoreGrouping = SharedParameters.GetFlag(c, SharedParameters.IgnoreGrouping),
            };
        }

        private static T Safe<T>(Func<T> f)
        {
            try { return f(); } catch (Exception) { return default; }
        }

        private static int FirstNumber(string s) =>
            int.TryParse(new string((s ?? "").TakeWhile(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue;
    }
}
