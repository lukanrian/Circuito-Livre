using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Depois de copiar/espelhar dispositivos, recria nas cópias os circuitos (força e telecom) que existiam
    /// entre os originais. Circuitos só são recriados quando todos os seus dispositivos foram copiados.
    /// </summary>
    public static class CircuitCloner
    {
        public class Outcome
        {
            public int Created;
            public List<string> Notes = new List<string>();
        }

        /// <summary>Associa cada original à cópia mais próxima da posição transformada (mesmo tipo de família).</summary>
        public static Dictionary<ElementId, ElementId> MapCopies(Document doc, ICollection<ElementId> originals, ICollection<ElementId> copies, Transform xf)
        {
            var map = new Dictionary<ElementId, ElementId>();
            List<Element> copyElements = copies.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            var used = new HashSet<ElementId>();

            foreach (ElementId oid in originals)
            {
                Element o = doc.GetElement(oid);
                XYZ p = RevitHelpers.PointOf(o);
                if (o == null || p == null) continue;
                XYZ expected = xf.OfPoint(p);
                ElementId typeId = o.GetTypeId();

                Element best = copyElements
                    .Where(c => !used.Contains(c.Id) && c.GetTypeId() == typeId && c.GetType() == o.GetType())
                    .Select(c => (c, d: RevitHelpers.PointOf(c)?.DistanceTo(expected) ?? double.MaxValue))
                    .OrderBy(x => x.d)
                    .Select(x => x.c)
                    .FirstOrDefault();
                if (best == null) continue;
                used.Add(best.Id);
                map[oid] = best.Id;
            }
            return map;
        }

        public static Outcome Recreate(Document doc, ICollection<ElementId> originals, Dictionary<ElementId, ElementId> map)
        {
            var outcome = new Outcome();
            var originalSet = new HashSet<ElementId>(originals);

            var circuits = new Dictionary<ElementId, ElectricalSystem>();
            foreach (ElementId id in originals)
                foreach (ElectricalSystem c in RevitHelpers.CircuitsOf(doc.GetElement(id)))
                    circuits[c.Id] = c;

            foreach (ElectricalSystem c in circuits.Values)
            {
                List<ElementId> devices = c.Elements.Cast<Element>().Select(e => e.Id).ToList();
                if (!devices.All(originalSet.Contains))
                {
                    outcome.Notes.Add($"Circuito {c.PanelName}-{c.CircuitNumber}: nem todos os dispositivos foram selecionados — não recriado.");
                    continue;
                }
                List<ElementId> copied = devices.Where(map.ContainsKey).Select(d => map[d]).ToList();
                if (copied.Count != devices.Count)
                {
                    outcome.Notes.Add($"Circuito {c.PanelName}-{c.CircuitNumber}: cópias não localizadas — não recriado.");
                    continue;
                }

                try
                {
                    ElectricalSystem created = ElectricalSystem.Create(doc, copied, c.SystemType);
                    if (created == null) continue;
                    outcome.Created++;

                    if (c.BaseEquipment != null)
                    {
                        ElementId panelId = map.TryGetValue(c.BaseEquipment.Id, out ElementId copiedPanel) ? copiedPanel : c.BaseEquipment.Id;
                        if (doc.GetElement(panelId) is FamilyInstance panel)
                        {
                            try { created.SelectPanel(panel); }
                            catch (Exception ex) { outcome.Notes.Add($"Circuito {c.CircuitNumber}: recriado, mas não ligado ao quadro ({ex.Message})."); }
                        }
                    }
                    try { if (!string.IsNullOrEmpty(c.LoadName)) created.LoadName = c.LoadName; } catch (Exception) { }
                }
                catch (Exception ex)
                {
                    outcome.Notes.Add($"Circuito {c.PanelName}-{c.CircuitNumber}: {ex.Message}");
                }
            }
            return outcome;
        }
    }
}
