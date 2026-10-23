using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CircuitoLivre.Commands;

namespace CircuitoLivre.Services
{
    /// <summary>Opções da identificação automática, guardadas no projeto (ProjectStorage).</summary>
    public class AutoIdOptions
    {
        public bool Enabled { get; set; }
        public string Pattern { get; set; } = "{QUADRO}-{CIRCUITO2}";
        public bool IncludeDevices { get; set; } = true;
        public bool FillLevel { get; set; } = true;

        public static AutoIdOptions Read(Document doc)
        {
            string json = ProjectStorage.Get(doc, ProjectStorage.AutoIdField);
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<AutoIdOptions>(json); }
            catch (Exception) { return null; }
        }

        /// <summary>Precisa estar dentro de uma transação.</summary>
        public void Write(Document doc) => ProjectStorage.Set(doc, ProjectStorage.AutoIdField, JsonSerializer.Serialize(this));
    }

    /// <summary>
    /// Mantém RE_Identificação (e RE_Nível) atualizados quando um circuito é criado ou alterado
    /// (número, quadro, dispositivos) ou quando um quadro é renomeado. Só age nos projetos em que a opção
    /// foi ligada no comando Preencher Identificação. Registrado como opcional: o arquivo abre sem avisos
    /// em computadores sem o plugin.
    /// </summary>
    public class AutoIdentificationUpdater : IUpdater
    {
        private static readonly Guid UpdaterGuid = new Guid("4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1c01");
        private readonly UpdaterId _id;

        public AutoIdentificationUpdater(AddInId addInId)
        {
            _id = new UpdaterId(addInId, UpdaterGuid);
        }

        public static void Register(AddInId addInId)
        {
            var updater = new AutoIdentificationUpdater(addInId);
            if (UpdaterRegistry.IsUpdaterRegistered(updater.GetUpdaterId())) return;
            UpdaterRegistry.RegisterUpdater(updater, true);

            UpdaterId id = updater.GetUpdaterId();
            var circuits = new ElementClassFilter(typeof(ElectricalSystem));
            UpdaterRegistry.AddTrigger(id, circuits, Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, circuits, Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, new ElementCategoryFilter(BuiltInCategory.OST_ElectricalEquipment),
                Element.GetChangeTypeParameter(new ElementId(BuiltInParameter.RBS_ELEC_PANEL_NAME)));
        }

        public static void Unregister(AddInId addInId)
        {
            var id = new UpdaterId(addInId, UpdaterGuid);
            if (UpdaterRegistry.IsUpdaterRegistered(id)) UpdaterRegistry.UnregisterUpdater(id);
        }

        public void Execute(UpdaterData data)
        {
            try
            {
                Document doc = data.GetDocument();
                AutoIdOptions options = AutoIdOptions.Read(doc);
                if (options == null || !options.Enabled || string.IsNullOrWhiteSpace(options.Pattern)) return;
                if (SharedParameterElement.Lookup(doc, SharedParameters.IdentificationGuid) == null) return;

                var circuits = new Dictionary<ElementId, ElectricalSystem>();
                List<ElementId> changed = data.GetModifiedElementIds().Concat(data.GetAddedElementIds()).ToList();
                var panels = new HashSet<ElementId>();
                foreach (ElementId id in changed)
                {
                    Element e = doc.GetElement(id);
                    if (e is ElectricalSystem c) circuits[c.Id] = c;
                    else if (e is FamilyInstance) panels.Add(id);
                }
                if (panels.Count > 0)
                {
                    foreach (ElectricalSystem c in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
                        if (c.BaseEquipment != null && panels.Contains(c.BaseEquipment.Id)) circuits[c.Id] = c;
                }

                foreach (ElectricalSystem c in circuits.Values) Apply(doc, c, options);
            }
            catch (Exception)
            {
                // nunca interromper a edição do usuário por causa da identificação automática
            }
        }

        /// <summary>Preenche a identificação de um circuito (e dos dispositivos, se marcado). Só grava o que mudou.</summary>
        public static void Apply(Document doc, ElectricalSystem c, AutoIdOptions o)
        {
            if (c?.BaseEquipment == null) return;
            List<Element> devices = c.Elements.Cast<Element>().Select(d => doc.GetElement(d.Id)).Where(d => d != null).ToList();
            string level = IdentifyCommand.LevelName(doc, devices.FirstOrDefault()) ?? IdentifyCommand.LevelName(doc, c.BaseEquipment);

            SetIfChanged(c, SharedParameters.IdentificationGuid,
                IdentificationFormatter.Format(o.Pattern, c.PanelName, c.CircuitNumber, c.LoadName, level));
            if (o.FillLevel) SetIfChanged(c, SharedParameters.LevelGuid, level);
            if (!o.IncludeDevices) return;

            foreach (Element d in devices)
            {
                string deviceLevel = IdentifyCommand.LevelName(doc, d) ?? level;
                SetIfChanged(d, SharedParameters.IdentificationGuid,
                    IdentificationFormatter.Format(o.Pattern, c.PanelName, c.CircuitNumber, c.LoadName, deviceLevel));
                if (o.FillLevel) SetIfChanged(d, SharedParameters.LevelGuid, deviceLevel);
            }
        }

        private static void SetIfChanged(Element e, Guid guid, string value)
        {
            Parameter p = e?.get_Parameter(guid);
            if (p == null || p.IsReadOnly) return;
            value ??= string.Empty;
            if ((p.AsString() ?? string.Empty) == value) return;
            try { p.Set(value); } catch (Exception) { }
        }

        public UpdaterId GetUpdaterId() => _id;
        public ChangePriority GetChangePriority() => ChangePriority.MEPSystems;
        public string GetUpdaterName() => "Circuito Livre — Identificação automática";
        public string GetAdditionalInformation() =>
            "Atualiza RE_Identificação e RE_Nível dos circuitos e dispositivos quando os circuitos mudam.";
    }
}
