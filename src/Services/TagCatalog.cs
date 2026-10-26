using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CircuitoLivre.Services
{
    /// <summary>Categorias de dispositivos e as categorias de identificador correspondentes.</summary>
    public static class TagCatalog
    {
        public static readonly (string Label, BuiltInCategory Device, BuiltInCategory Tag)[] Map =
        {
            ("Luminárias", BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags),
            ("Dispositivos elétricos (tomadas)", BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalFixtureTags),
            ("Dispositivos de iluminação (interruptores)", BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_LightingDeviceTags),
            ("Equipamentos elétricos (quadros)", BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalEquipmentTags),
            ("Dispositivos de dados", BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_DataDeviceTags),
            ("Dispositivos de comunicação", BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_CommunicationDeviceTags),
            ("Dispositivos de telefone", BuiltInCategory.OST_TelephoneDevices, BuiltInCategory.OST_TelephoneDeviceTags),
            ("Dispositivos de segurança", BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_SecurityDeviceTags),
            ("Alarme de incêndio", BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_FireAlarmDeviceTags),
            ("Eletrodutos", BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitTags),
            ("Eletrocalhas", BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayTags),
        };

        public static string Label(FamilySymbol s) => $"{s.FamilyName} : {s.Name}";

        public static ElementId Automatic(Document doc, BuiltInCategory tagCategory)
        {
            var used = new FilteredElementCollector(doc,doc.ActiveView.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Where(t=>t.Category?.BuiltInCategory==tagCategory).GroupBy(t=>t.GetTypeId())
                .OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.Value).Select(g=>g.Key).FirstOrDefault();
            if (used!=null) return used;
            try
            {
                var preferred=doc.GetDefaultFamilyTypeId(new ElementId(tagCategory));
                if(doc.GetElement(preferred) is FamilySymbol symbol && symbol.Category?.BuiltInCategory==tagCategory) return preferred;
            }
            catch (System.Exception) { }
            var loaded=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s=>s.Category?.BuiltInCategory==tagCategory).Take(2).ToList();
            return loaded.Count==1?loaded[0].Id:null;
        }

        public static string DeviceKey(Element e)
        {
            var symbol = (e as FamilyInstance)?.Symbol;
            return symbol == null ? null : $"{e.Category?.BuiltInCategory}|{Label(symbol)}";
        }

        public static bool Centered(Element e, DimensioningSettings cfg)
        {
            string key = DeviceKey(e);
            return key != null && cfg.TagCenterByDeviceType.TryGetValue(key, out bool centered) && centered;
        }

        /// <summary>Opções de identificador por categoria (inclui os multicategoria).</summary>
        public static Dictionary<string, List<string>> Options(Document doc)
        {
            List<FamilySymbol> tags = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null).ToList();
            var result = new Dictionary<string, List<string>>();
            foreach (var m in Map)
            {
                result[m.Label] = tags.Where(s => s.Category.BuiltInCategory == m.Tag || s.Category.BuiltInCategory == BuiltInCategory.OST_MultiCategoryTags)
                    .Select(Label).Distinct().OrderBy(x => x).ToList();
            }
            return result;
        }

        /// <summary>Tipo de identificador configurado para a categoria do elemento (ou null).</summary>
        public static ElementId Configured(Document doc, Element e, DimensioningSettings cfg)
        {
            BuiltInCategory cat = e.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
            string key = DeviceKey(e);
            foreach (var m in Map)
            {
                if (m.Device != cat) continue;
                string label = key != null && cfg.TagByDeviceType.TryGetValue(key, out string own)
                    ? own : cfg.TagByCategory.TryGetValue(m.Label, out string byCategory) ? byCategory : null;
                if (label == null) return Automatic(doc,m.Tag);
                return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => Label(s) == label && s.Category != null &&
                        (s.Category.BuiltInCategory == m.Tag || s.Category.BuiltInCategory == BuiltInCategory.OST_MultiCategoryTags))?.Id ?? Automatic(doc,m.Tag);
            }
            return null;
        }
    }
}
