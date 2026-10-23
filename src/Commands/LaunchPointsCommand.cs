using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Lança luminárias, tomadas, interruptores e pontos de telecom com as famílias carregadas no projeto,
    /// usando a inserção nativa do Revit (vários cliques, ESC termina) e ajustando altura e potência depois.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LaunchPointsCommand : IExternalCommand
    {
        private static readonly Dictionary<BuiltInCategory, LaunchCategory> CategoryMap = new()
        {
            [BuiltInCategory.OST_LightingFixtures] = LaunchCategory.Lighting,
            [BuiltInCategory.OST_ElectricalFixtures] = LaunchCategory.Outlet,
            [BuiltInCategory.OST_LightingDevices] = LaunchCategory.Switch,
            [BuiltInCategory.OST_DataDevices] = LaunchCategory.Telecom,
            [BuiltInCategory.OST_CommunicationDevices] = LaunchCategory.Telecom,
            [BuiltInCategory.OST_TelephoneDevices] = LaunchCategory.Telecom,
            [BuiltInCategory.OST_SecurityDevices] = LaunchCategory.Telecom,
            [BuiltInCategory.OST_FireAlarmDevices] = LaunchCategory.Telecom,
            [BuiltInCategory.OST_NurseCallDevices] = LaunchCategory.Telecom,
        };

        private static readonly string[] PowerParameterNames =
            { "Potência (VA)", "Potência", "Potencia", "Carga Aparente", "Carga aparente", "Apparent Load", "Potência Aparente" };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) return Result.Cancelled;

            // 1. Tipos carregados no projeto
            List<LaunchType> types = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && CategoryMap.ContainsKey(s.Category.BuiltInCategory))
                .Select(s => new LaunchType
                {
                    SymbolId = s.Id,
                    Category = CategoryMap[s.Category.BuiltInCategory],
                    Label = $"{s.FamilyName} : {s.Name}",
                })
                .OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var window = new LaunchWindow(types);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;

            var symbol = (FamilySymbol)doc.GetElement(window.Selected.SymbolId);
            if (!symbol.IsActive)
            {
                using var t = new Transaction(doc, "Ativar tipo");
                t.Start();
                symbol.Activate();
                t.Commit();
            }

            // 2. Inserção nativa, capturando os elementos criados
            var added = new List<ElementId>();
            void OnChanged(object sender, DocumentChangedEventArgs e)
            {
                if (e.GetDocument().Equals(doc)) added.AddRange(e.GetAddedElementIds());
            }

            uiapp.Application.DocumentChanged += OnChanged;
            try
            {
                uidoc.PromptForFamilyInstancePlacement(symbol);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* ESC: fim do lançamento */ }
            catch (Exception ex)
            {
                message = ex.Message;
            }
            finally
            {
                uiapp.Application.DocumentChanged -= OnChanged;
            }

            List<FamilyInstance> placed = added
                .Select(id => doc.GetElement(id))
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Id == symbol.Id)
                .ToList();

            if (placed.Count == 0) return Result.Cancelled;

            // 3. Altura e potência
            var notes = new List<string>();
            if (window.HeightM.HasValue || window.PowerVA.HasValue)
            {
                using var t = new Transaction(doc, "Ajustar pontos lançados");
                t.Start();
                int heightFail = 0, powerFail = 0;
                foreach (FamilyInstance fi in placed)
                {
                    if (window.HeightM.HasValue && !SetHeight(fi, window.HeightM.Value)) heightFail++;
                    if (window.PowerVA.HasValue && !SetPower(fi, window.PowerVA.Value)) powerFail++;
                }
                t.Commit();

                if (heightFail > 0)
                    notes.Add($"{heightFail} ponto(s) não aceitaram a altura (a família define a altura pelo hospedeiro ou não tem parâmetro de elevação).");
                if (powerFail > 0)
                    notes.Add($"{powerFail} ponto(s) não aceitaram a potência: na família ela é um parâmetro de tipo ou tem outro nome. " +
                              "Ajuste no tipo da família ou crie um parâmetro de instância \"Potência (VA)\".");
            }

            var report = new TaskDialog("Lançar pontos")
            {
                MainInstruction = $"{placed.Count} ponto(s) lançado(s): {window.Selected.Label}",
            };
            if (notes.Count > 0) report.MainContent = string.Join("\n\n", notes);
            report.Show();

            uidoc.Selection.SetElementIds(placed.Select(p => p.Id).ToList());
            return Result.Succeeded;
        }

        internal static bool SetHeight(FamilyInstance fi, double meters)
        {
            double ft = UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
            foreach (BuiltInParameter bip in new[]
                     {
                         BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                         BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
                     })
            {
                Parameter p = fi.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                try { if (p.Set(ft)) return true; } catch (Exception) { }
            }
            return false;
        }

        internal static bool SetPower(FamilyInstance fi, double va)
        {
            // Famílias com mais de uma tomada (ex.: "Tomada 1_Potência...", "Tomada 2_Potência...") ou nomes livres:
            // grava em todos os parâmetros de instância editáveis de potência aparente (VA).
            bool any = false;
            foreach (Parameter p in fi.Parameters)
            {
                if (p.IsReadOnly || p.StorageType != StorageType.Double || p.Definition == null) continue;
                string n = p.Definition.Name ?? "";
                bool powerName = n.IndexOf("Potência", StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                 n.IndexOf("Potencia", StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                 n.IndexOf("Apparent Load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 n.IndexOf("Carga Aparente", StringComparison.CurrentCultureIgnoreCase) >= 0;
                if (!powerName) continue;
                ForgeTypeId spec;
                try { spec = p.Definition.GetDataType(); } catch (Exception) { continue; }
                if (spec != SpecTypeId.ApparentPower) continue;
                try { any |= p.Set(UnitUtils.ConvertToInternalUnits(va, UnitTypeId.VoltAmperes)); } catch (Exception) { }
            }
            if (any) return true;

            foreach (string name in PowerParameterNames)
            {
                Parameter p = fi.LookupParameter(name);
                if (p == null || p.IsReadOnly) continue;
                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.Double:
                            double value = p.Definition.GetDataType() == SpecTypeId.ApparentPower
                                ? UnitUtils.ConvertToInternalUnits(va, UnitTypeId.VoltAmperes)
                                : va;
                            if (p.Set(value)) return true;
                            break;
                        case StorageType.Integer:
                            if (p.Set((int)Math.Round(va))) return true;
                            break;
                        case StorageType.String:
                            if (p.Set(va.ToString("0"))) return true;
                            break;
                    }
                }
                catch (Exception) { }
            }
            return false;
        }
    }
}
