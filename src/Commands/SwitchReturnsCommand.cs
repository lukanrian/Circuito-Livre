using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Services;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Retornos de interruptores: clique no interruptor e nas luminárias que ele comanda; o plugin dá a próxima
    /// letra livre do circuito (a, b, c...) e grava em RE_Retorno (e no "ID do interruptor" nativo, quando editável).
    /// Repete até ESC. Interruptor de várias seções recebe várias letras (ex.: "a, b").
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SwitchReturnsCommand : IExternalCommand
    {
        private class CategoryFilter : ISelectionFilter
        {
            private readonly BuiltInCategory _cat;
            public CategoryFilter(BuiltInCategory cat) => _cat = cat;
            public bool AllowElement(Element e) => e.Category?.BuiltInCategory == _cat;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            using (var t = new Transaction(doc, "Parâmetro de retorno"))
            {
                t.Start();
                try { SharedParameters.Ensure(doc, SharedParameters.Return); }
                catch (Exception ex)
                {
                    t.RollBack();
                    TaskDialog.Show("Retornos", $"Não foi possível criar o parâmetro RE_Retorno:\n{ex.Message}");
                    return Result.Failed;
                }
                t.Commit();
            }

            int groups = 0;
            var log = new List<string>();
            while (true)
            {
                Element sw;
                List<Element> fixtures;
                try
                {
                    sw = doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element,
                        new CategoryFilter(BuiltInCategory.OST_LightingDevices), "Retornos: clique no INTERRUPTOR (ESC termina)"));
                    fixtures = uidoc.Selection.PickObjects(ObjectType.Element,
                            new CategoryFilter(BuiltInCategory.OST_LightingFixtures),
                            "Retornos: selecione as LUMINÁRIAS comandadas e clique em Concluir")
                        .Select(r => doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                if (fixtures.Count == 0) continue;

                ElectricalSystem circuit = RevitHelpers.CircuitsOf(fixtures[0]).FirstOrDefault(c => c.SystemType == ElectricalSystemType.PowerCircuit)
                                           ?? RevitHelpers.CircuitsOf(sw).FirstOrDefault(c => c.SystemType == ElectricalSystemType.PowerCircuit);
                string letter = NextLetter(doc, circuit, fixtures);

                using (var t = new Transaction(doc, "Retorno " + letter))
                {
                    t.Start();
                    string current = SharedParameters.GetText(sw, SharedParameters.Return) ?? "";
                    var letters = current.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (!letters.Contains(letter)) letters.Add(letter);
                    SharedParameters.SetText(sw, SharedParameters.Return, string.Join(", ", letters));
                    TrySetSwitchId(sw, string.Join(", ", letters));
                    foreach (Element f in fixtures)
                    {
                        SharedParameters.SetText(f, SharedParameters.Return, letter);
                        TrySetSwitchId(f, letter);
                    }
                    t.Commit();
                }
                groups++;
                log.Add($"{(circuit != null ? "circuito " + circuit.CircuitNumber : "sem circuito")}: retorno \"{letter}\" — {fixtures.Count} luminária(s)");
            }

            if (groups > 0)
                TaskDialog.Show("Retornos", $"{groups} retorno(s) definido(s):\n\n" + RevitHelpers.Join(log) +
                    "\n\nUse RE_Retorno nas etiquetas das luminárias e interruptores.");
            return Result.Succeeded;
        }

        /// <summary>Próxima letra livre entre as luminárias/interruptores do mesmo circuito.</summary>
        private static string NextLetter(Document doc, ElectricalSystem circuit, List<Element> picked)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<Element> scope = circuit != null
                ? circuit.Elements.Cast<Element>()
                : picked;
            foreach (Element e in scope)
            {
                if (picked.Any(p => p.Id == e.Id)) continue;
                string v = SharedParameters.GetText(doc.GetElement(e.Id), SharedParameters.Return);
                if (string.IsNullOrWhiteSpace(v)) continue;
                foreach (string part in v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) used.Add(part.Trim());
            }
            for (int i = 0; i < 26 * 27; i++)
            {
                string s = i < 26 ? ((char)('a' + i)).ToString() : $"{(char)('a' + i / 26 - 1)}{(char)('a' + i % 26)}";
                if (!used.Contains(s)) return s;
            }
            return "?";
        }

        private static void TrySetSwitchId(Element e, string value)
        {
            if (!Enum.TryParse("RBS_ELEC_SWITCH_ID_PARAM", out BuiltInParameter bip)) return;
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
            }
            catch (Exception) { }
        }
    }
}
