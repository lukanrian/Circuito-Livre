using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

namespace RafaelElectrical.Services
{
    /// <summary>Utilitários comuns aos comandos.</summary>
    public static class RevitHelpers
    {
        /// <summary>Versão exibida do plugin (ex.: "0.1.0-beta").</summary>
        public static string PluginVersion
        {
            get
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string info = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm))?.InformationalVersion;
                if (string.IsNullOrEmpty(info)) return asm.GetName().Version?.ToString() ?? "?";
                int plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info;
            }
        }

        public static double M(double meters) => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        public static double ToM(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);

        public static XYZ PointOf(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        public static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();

        public static IntPtr Owner(ExternalCommandData data) => data.Application.MainWindowHandle;

        public static void ShowOwned(System.Windows.Window w, ExternalCommandData data) =>
            new System.Windows.Interop.WindowInteropHelper(w).Owner = data.Application.MainWindowHandle;

        /// <summary>Circuitos (de qualquer sistema) aos quais o elemento está ligado como carga.</summary>
        public static List<ElectricalSystem> CircuitsOf(Element e)
        {
            if (e is not FamilyInstance fi || fi.MEPModel == null) return new List<ElectricalSystem>();
            try
            {
                return fi.MEPModel.GetElectricalSystems()
                    .Where(s => s.BaseEquipment == null || s.BaseEquipment.Id != fi.Id)
                    .ToList();
            }
            catch (Exception) { return new List<ElectricalSystem>(); }
        }

        /// <summary>Planta de piso (não modelo) de um nível, preferindo o mesmo modelo de vista da vista de referência.</summary>
        public static ViewPlan PlanOf(Document doc, ElementId levelId, View reference)
        {
            List<ViewPlan> plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == levelId)
                .ToList();
            if (reference != null)
            {
                ViewPlan same = plans.FirstOrDefault(v => v.ViewTemplateId == reference.ViewTemplateId && v.ViewTemplateId != ElementId.InvalidElementId);
                if (same != null) return same;
            }
            return plans.FirstOrDefault();
        }

        public static string Join(IEnumerable<string> items, int max = 40)
        {
            List<string> list = items.ToList();
            return string.Join("\n", list.Take(max)) + (list.Count > max ? $"\n… e mais {list.Count - max}" : "");
        }
    }
}
