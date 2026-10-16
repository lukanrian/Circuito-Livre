using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RafaelElectrical.Services
{
    /// <summary>Definição de um parâmetro compartilhado do plugin.</summary>
    public class ParamDef
    {
        public ParamDef(string name, string guid, ForgeTypeId spec, string description, BuiltInCategory[] categories)
        {
            Name = name;
            Guid = new Guid(guid);
            Spec = spec;
            Description = description;
            Categories = categories;
        }

        public string Name { get; }
        public Guid Guid { get; }
        public ForgeTypeId Spec { get; }
        public string Description { get; }
        public BuiltInCategory[] Categories { get; }
    }

    /// <summary>
    /// Parâmetros compartilhados próprios do RafaelElectrical. São criados num arquivo de parâmetros
    /// do próprio plugin (%AppData%\RafaelElectrical) e vinculados ao projeto quando necessário.
    /// O arquivo de parâmetros compartilhados configurado no Revit é restaurado ao final.
    /// </summary>
    public static class SharedParameters
    {
        private static readonly BuiltInCategory[] CircuitsAndDevices =
        {
            BuiltInCategory.OST_ElectricalCircuit,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_SecurityDevices,
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_NurseCallDevices,
        };

        private static readonly BuiltInCategory[] CircuitsOnly = { BuiltInCategory.OST_ElectricalCircuit };

        // ---- identificação ----
        public static readonly ParamDef Identification = new("RE_Identificação", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a01", SpecTypeId.String.Text,
            "Identificação do circuito/dispositivo (quadro-circuito), preenchida pelo RafaelElectrical.", CircuitsAndDevices);
        public static readonly ParamDef LevelDef = new("RE_Nível", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a02", SpecTypeId.String.Text,
            "Nível do circuito/dispositivo, preenchido pelo RafaelElectrical.", CircuitsAndDevices);

        public static readonly ParamDef Return = new("RE_Retorno", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a03", SpecTypeId.String.Text,
            "Letra(s) de retorno do interruptor e das luminárias comandadas (ex.: a, b).",
            new[] { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices });

        // ---- dimensionamento (circuitos) ----
        public static readonly ParamDef Section = new("RE_Seção (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a10", SpecTypeId.Number,
            "Seção do condutor de fase dimensionada (mm²).", CircuitsOnly);
        public static readonly ParamDef ProtectionSection = new("RE_Seção PE (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a11", SpecTypeId.Number,
            "Seção do condutor de proteção (mm²).", CircuitsOnly);
        public static readonly ParamDef Breaker = new("RE_Disjuntor (A)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a12", SpecTypeId.Number,
            "Corrente nominal do disjuntor (A).", CircuitsOnly);
        public static readonly ParamDef Current = new("RE_Corrente de projeto (A)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a13", SpecTypeId.Number,
            "Corrente de projeto Ib (A).", CircuitsOnly);
        public static readonly ParamDef VoltageDrop = new("RE_Queda de tensão (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a14", SpecTypeId.Number,
            "Queda de tensão calculada no circuito (%).", CircuitsOnly);
        public static readonly ParamDef Wiring = new("RE_Fiação", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a15", SpecTypeId.String.Text,
            "Descrição da fiação (ex.: F+N+PE #2,5 mm²).", CircuitsOnly);
        public static readonly ParamDef SizingStatus = new("RE_Dimensionamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a16", SpecTypeId.String.Text,
            "Situação e critério do dimensionamento.", CircuitsOnly);
        public static readonly ParamDef NeutralSection = new("RE_Seção neutro (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a18", SpecTypeId.Number,
            "Seção do condutor neutro (mm²).", CircuitsOnly);
        public static readonly ParamDef TotalDrop = new("RE_Queda total (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a19", SpecTypeId.Number,
            "Queda de tensão acumulada desde a origem (alimentadores + circuito) (%).", CircuitsOnly);
        public static readonly ParamDef IgnoreGrouping = new("RE_Desconsiderar agrupamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a17", SpecTypeId.Boolean.YesNo,
            "Marcado: o circuito não entra no fator de agrupamento.", CircuitsOnly);

        // ---- roteamento / infraestrutura ----
        private static readonly BuiltInCategory[] Infrastructure =
        {
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        };

        public static readonly ParamDef RouteCircuits = new("RE_Circuitos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a20", SpecTypeId.String.Text,
            "Circuitos que passam por este trecho (preenchido pelo roteamento).", Infrastructure);
        public static readonly ParamDef RouteCount = new("RE_Nº de circuitos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a21", SpecTypeId.Number,
            "Quantidade de circuitos no trecho.", Infrastructure);
        public static readonly ParamDef RouteCables = new("RE_Cabos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a22", SpecTypeId.String.Text,
            "Cabos no trecho por circuito (para identificadores de fiação).", Infrastructure);
        public static readonly ParamDef RouteOccupancy = new("RE_Taxa de ocupação (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a23", SpecTypeId.Number,
            "Taxa de ocupação do eletroduto (%).", Infrastructure);
        public static readonly ParamDef RealLength = new("RE_Comprimento real (m)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a24", SpecTypeId.Number,
            "Comprimento do circuito pelo caminho na infraestrutura (m).", CircuitsOnly);
        public static readonly ParamDef RealGrouping = new("RE_Agrupamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a25", SpecTypeId.Number,
            "Maior quantidade de circuitos agrupados no caminho do circuito.", CircuitsOnly);

        public static readonly ParamDef RealFca = new("RE_FCA", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a26", SpecTypeId.Number,
            "Fator de agrupamento do pior trecho do caminho (pelo tipo de infraestrutura).", CircuitsOnly);

        public static void EnsureRouting(Document doc) =>
            Ensure(doc, RouteCircuits, RouteCount, RouteCables, RouteOccupancy, RealLength, RealGrouping, RealFca,
                NeutralSection, TotalDrop, Section, ProtectionSection, Breaker, Current, VoltageDrop, Wiring, SizingStatus, IgnoreGrouping);

        public static double? GetNumber(Element e, ParamDef d)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            return p != null && p.HasValue ? p.AsDouble() : (double?)null;
        }

        public static string GetText(Element e, ParamDef d) => e?.get_Parameter(d.Guid)?.AsString();

        // compatibilidade com o comando de identificação
        public static Guid IdentificationGuid => Identification.Guid;
        public static Guid LevelGuid => LevelDef.Guid;
        public static string IdentificationName => Identification.Name;
        public static string LevelName => LevelDef.Name;

        public static void EnsureIdentification(Document doc) => Ensure(doc, Identification, LevelDef);

        public static void EnsureSizing(Document doc) =>
            Ensure(doc, NeutralSection, TotalDrop, Section, ProtectionSection, Breaker, Current, VoltageDrop, Wiring, SizingStatus, IgnoreGrouping);

        /// <summary>Garante que os parâmetros existem no projeto. Precisa estar dentro de uma transação.</summary>
        public static void Ensure(Document doc, params ParamDef[] defs)
        {
            ParamDef[] missing = defs.Where(d => SharedParameterElement.Lookup(doc, d.Guid) == null).ToArray();
            if (missing.Length == 0) return;

            Application app = doc.Application;
            string original = app.SharedParametersFilename;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RafaelElectrical");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "RafaelElectrical_Parametros.txt");
            if (!File.Exists(file)) File.WriteAllText(file, string.Empty);

            try
            {
                app.SharedParametersFilename = file;
                DefinitionFile definitionFile = app.OpenSharedParameterFile()
                    ?? throw new InvalidOperationException("Não foi possível abrir o arquivo de parâmetros do plugin.");

                DefinitionGroup group = definitionFile.Groups.get_Item("RafaelElectrical")
                                        ?? definitionFile.Groups.Create("RafaelElectrical");

                foreach (ParamDef d in missing)
                {
                    Definition definition = group.Definitions.get_Item(d.Name) ?? group.Definitions.Create(
                        new ExternalDefinitionCreationOptions(d.Name, d.Spec)
                        {
                            GUID = d.Guid,
                            Description = d.Description,
                            UserModifiable = true,
                            Visible = true,
                        });

                    CategorySet categories = app.Create.NewCategorySet();
                    foreach (BuiltInCategory bic in d.Categories)
                    {
                        Category c = Category.GetCategory(doc, bic);
                        if (c != null && c.AllowsBoundParameters) categories.Insert(c);
                    }

                    InstanceBinding binding = app.Create.NewInstanceBinding(categories);
                    if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData))
                        doc.ParameterBindings.ReInsert(definition, binding, GroupTypeId.IdentityData);
                }
            }
            finally
            {
                try { app.SharedParametersFilename = original ?? string.Empty; } catch (Exception) { }
            }
        }

        // ---- leitura/escrita ----

        public static bool SetText(Element e, ParamDef d, string value)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value ?? string.Empty); } catch (Exception) { return false; }
        }

        public static bool SetNumber(Element e, ParamDef d, double value)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value); } catch (Exception) { return false; }
        }

        public static bool SetFlag(Element e, ParamDef d, bool value)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value ? 1 : 0); } catch (Exception) { return false; }
        }

        public static bool GetFlag(Element e, ParamDef d)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            return p != null && p.HasValue && p.AsInteger() == 1;
        }
    }
}
