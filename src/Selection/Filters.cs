using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI.Selection;

namespace RafaelElectrical.Selection
{
    /// <summary>
    /// Permite selecionar dispositivos que tenham conector elétrico livre (sem circuito) de qualquer sistema:
    /// força, dados, telefone, comunicação, segurança, alarme etc.
    /// </summary>
    public class CircuitDeviceFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => GetFreeSystemTypes(elem).Count > 0;

        public bool AllowReference(Reference reference, XYZ position) => false;

        /// <summary>Tipos de sistema dos conectores elétricos ainda sem circuito.</summary>
        public static HashSet<ElectricalSystemType> GetFreeSystemTypes(Element elem)
        {
            var set = new HashSet<ElectricalSystemType>();
            if (elem is not FamilyInstance fi || IsPanel(fi)) return set;

            ConnectorManager cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return set;

            foreach (Connector c in cm.UnusedConnectors)
            {
                if (c.Domain != Domain.DomainElectrical) continue;
                try
                {
                    ElectricalSystemType t = Normalize(c.ElectricalSystemType);
                    if (t != ElectricalSystemType.UndefinedSystemType) set.Add(t);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            return set;
        }

        /// <summary>Força equilibrada/desequilibrada viram "circuito de força".</summary>
        public static ElectricalSystemType Normalize(ElectricalSystemType t) =>
            t == ElectricalSystemType.PowerBalanced || t == ElectricalSystemType.PowerUnBalanced
                ? ElectricalSystemType.PowerCircuit
                : t;

        public static string DisplayName(ElectricalSystemType t) => t switch
        {
            ElectricalSystemType.PowerCircuit => "Força",
            ElectricalSystemType.Data => "Dados",
            ElectricalSystemType.Telephone => "Telefone",
            ElectricalSystemType.Communication => "Comunicação",
            ElectricalSystemType.Security => "Segurança",
            ElectricalSystemType.FireAlarm => "Alarme de incêndio",
            ElectricalSystemType.NurseCall => "Chamada de enfermagem",
            ElectricalSystemType.Controls => "Controle",
            _ => t.ToString(),
        };

        /// <summary>Quadro/painel: família cujo modelo MEP é equipamento elétrico (força ou telecom).</summary>
        public static bool IsPanel(Element elem) =>
            elem is FamilyInstance fi &&
            (fi.MEPModel is ElectricalEquipment || elem.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment);
    }

    /// <summary>Permite selecionar apenas quadros/painéis (força ou telecom).</summary>
    public class PanelFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => CircuitDeviceFilter.IsPanel(elem);

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Permite selecionar apenas os elementos de uma lista (ex.: dispositivos de um quadro).</summary>
    public class IdSetFilter : ISelectionFilter
    {
        private readonly HashSet<ElementId> _ids;
        public IdSetFilter(HashSet<ElementId> ids) => _ids = ids;

        public bool AllowElement(Element elem) => _ids.Contains(elem.Id);

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Espaços (MEP) ou Ambientes do próprio projeto.</summary>
    public class SpatialPickFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            BuiltInCategory? c = elem.Category?.BuiltInCategory;
            return c == BuiltInCategory.OST_MEPSpaces || c == BuiltInCategory.OST_Rooms;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Ambientes dentro de vínculos (arquitetura).</summary>
    public class LinkedRoomFilter : ISelectionFilter
    {
        private readonly Document _doc;
        public LinkedRoomFilter(Document doc) => _doc = doc;

        public bool AllowElement(Element elem) => elem is RevitLinkInstance;

        public bool AllowReference(Reference reference, XYZ position)
        {
            if (_doc.GetElement(reference) is not RevitLinkInstance link) return false;
            Element linked = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
            return linked?.Category?.BuiltInCategory == BuiltInCategory.OST_Rooms;
        }
    }
}
