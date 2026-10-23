using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace CircuitoLivre.Services
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
    /// Parâmetros compartilhados próprios do Circuito Livre. São criados num arquivo de parâmetros
    /// do próprio plugin (%AppData%\CircuitoLivre) e vinculados ao projeto quando necessário.
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
        public static readonly ParamDef Identification = new("CL_Identificação", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a01", SpecTypeId.String.Text,
            "Identificação do circuito/dispositivo (quadro-circuito), preenchida pelo Circuito Livre.", CircuitsAndDevices);
        public static readonly ParamDef LevelDef = new("CL_Nível", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a02", SpecTypeId.String.Text,
            "Nível do circuito/dispositivo, preenchido pelo Circuito Livre.", CircuitsAndDevices);

        public static readonly ParamDef Return = new("CL_Retorno", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a03", SpecTypeId.String.Text,
            "Letra(s) de retorno do interruptor e das luminárias comandadas (ex.: a, b).",
            new[] { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices });

        /// <summary>Categorias de pontos que entram na legenda.</summary>
        public static readonly BuiltInCategory[] LegendCategories =
        {
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

        public static readonly ParamDef Legend = new("CL_Legenda", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a04", SpecTypeId.String.Text,
            "Número do tipo na legenda de pontos (para mostrar no identificador).", LegendCategories);

        public static readonly ParamDef PanelDemand = new("CL_Demanda (VA)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a06", SpecTypeId.Number,
            "Demanda do quadro calculada pelo Dimensionar Projeto (VA).", new[] { BuiltInCategory.OST_ElectricalEquipment });
        public static readonly ParamDef PanelIcc = new("CL_Icc (kA)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a07", SpecTypeId.Number,
            "Corrente de curto-circuito estimada no quadro (kA).", new[] { BuiltInCategory.OST_ElectricalEquipment });
        public static readonly ParamDef PanelEntrance = new("CL_Padrão de entrada", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a08", SpecTypeId.String.Text,
            "Categoria de atendimento / padrão de entrada (quadro geral).", new[] { BuiltInCategory.OST_ElectricalEquipment });

        public static readonly ParamDef Origin = new("CL_Origem", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a05", SpecTypeId.String.Text,
            "Equipamento de origem no vínculo (preenchido pelo Importar Cargas).",
            new[] { BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalEquipment });

        // ---- dimensionamento (circuitos) ----
        public static readonly ParamDef Section = new("CL_Seção (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a10", SpecTypeId.Number,
            "Seção do condutor de fase dimensionada (mm²).", CircuitsOnly);
        public static readonly ParamDef ProtectionSection = new("CL_Seção PE (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a11", SpecTypeId.Number,
            "Seção do condutor de proteção (mm²).", CircuitsOnly);
        public static readonly ParamDef Breaker = new("CL_Disjuntor (A)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a12", SpecTypeId.Number,
            "Corrente nominal do disjuntor (A).", CircuitsOnly);
        public static readonly ParamDef Current = new("CL_Corrente de projeto (A)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a13", SpecTypeId.Number,
            "Corrente de projeto Ib (A).", CircuitsOnly);
        public static readonly ParamDef VoltageDrop = new("CL_Queda de tensão (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a14", SpecTypeId.Number,
            "Queda de tensão calculada no circuito (%).", CircuitsOnly);
        public static readonly ParamDef Wiring = new("CL_Fiação", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a15", SpecTypeId.String.Text,
            "Descrição da fiação (ex.: F+N+PE #2,5 mm²).", CircuitsOnly);
        public static readonly ParamDef SizingStatus = new("CL_Dimensionamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a16", SpecTypeId.String.Text,
            "Situação e critério do dimensionamento.", CircuitsOnly);
        public static readonly ParamDef NeutralSection = new("CL_Seção neutro (mm²)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a18", SpecTypeId.Number,
            "Seção do condutor neutro (mm²).", CircuitsOnly);
        public static readonly ParamDef TotalDrop = new("CL_Queda total (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a19", SpecTypeId.Number,
            "Queda de tensão acumulada desde a origem (alimentadores + circuito) (%).", CircuitsOnly);
        public static readonly ParamDef IgnoreGrouping = new("CL_Desconsiderar agrupamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a17", SpecTypeId.Boolean.YesNo,
            "Marcado: o circuito não entra no fator de agrupamento.", CircuitsOnly);

        // ---- roteamento / infraestrutura ----
        private static readonly BuiltInCategory[] Infrastructure =
        {
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        };

        public static readonly ParamDef RouteCircuits = new("CL_Circuitos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a20", SpecTypeId.String.Text,
            "Circuitos que passam por este trecho (preenchido pelo roteamento).", Infrastructure);
        public static readonly ParamDef RouteCount = new("CL_Nº de circuitos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a21", SpecTypeId.Number,
            "Quantidade de circuitos no trecho.", Infrastructure);
        public static readonly ParamDef RouteCables = new("CL_Cabos", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a22", SpecTypeId.String.Text,
            "Cabos no trecho por circuito (para identificadores de fiação).", Infrastructure);
        public static readonly ParamDef RouteOccupancy = new("CL_Taxa de ocupação (%)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a23", SpecTypeId.Number,
            "Taxa de ocupação do eletroduto (%).", Infrastructure);
        public static readonly ParamDef LevelPass = new("CL_Passagem", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a27", SpecTypeId.String.Text,
            "SOBE, DESCE ou PASSA: trecho vertical que vai para outro nível (preenchido por Identificadores de Infra de Nível).", Infrastructure);
        public static readonly ParamDef RealLength = new("CL_Comprimento real (m)", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a24", SpecTypeId.Number,
            "Comprimento do circuito pelo caminho na infraestrutura (m).", CircuitsOnly);
        public static readonly ParamDef RealGrouping = new("CL_Agrupamento", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a25", SpecTypeId.Number,
            "Maior quantidade de circuitos agrupados no caminho do circuito.", CircuitsOnly);

        public static readonly ParamDef RealFca = new("CL_FCA", "4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1a26", SpecTypeId.Number,
            "Fator de agrupamento do pior trecho do caminho (pelo tipo de infraestrutura).", CircuitsOnly);

        public static void EnsureRouting(Document doc) =>
            Ensure(doc, RouteCircuits, RouteCount, RouteCables, RouteOccupancy, RealLength, RealGrouping, RealFca,
                NeutralSection, TotalDrop, Section, ProtectionSection, Breaker, Current, VoltageDrop, Wiring, SizingStatus, IgnoreGrouping);

        public static readonly ParamDef PanelIccStatus = new("CL_Verificação de curto", "9ea9fb6b-f73a-4cdf-8abd-d8a2c58b10f5", SpecTypeId.String.Text,
            "Indica se RE_Icc foi estimado; zero com situação não verificada não representa corrente de curto nula.", new[] { BuiltInCategory.OST_ElectricalEquipment });

        public static bool HasValidSizing(Element e)
        {
            string status = e?.get_Parameter(SizingStatus.Guid)?.AsString();
            return status != null && status.StartsWith("OK", StringComparison.Ordinal) && !status.Contains("sem comprimento");
        }

        private static void RequireUsableSizing(Element e, ParamDef d)
        {
            if (d != Section && d != ProtectionSection && d != NeutralSection && d != Breaker &&
                d != VoltageDrop && d != TotalDrop && d != Wiring) return;
            string status = e?.get_Parameter(SizingStatus.Guid)?.AsString();
            if (!string.IsNullOrWhiteSpace(status) && !status.StartsWith("OK", StringComparison.Ordinal))
                throw new InvalidOperationException($"Circuito {e.Id}: dimensionamento reprovado. Execute Dimensionar antes de usar os resultados.");
        }

        public static bool WriteSizingResult(Element e, SizingResult r)
        {
            bool ok = SetText(e, SizingStatus, r.Ok ? $"{r.Status} — {r.Criterion}" : r.Status);
            ok &= SetNumber(e, Current, double.IsFinite(r.CurrentA) ? Math.Round(r.CurrentA, 2) : 0);
            ok &= SetNumber(e, Section, r.Ok ? r.SectionMm2 : 0);
            ok &= SetNumber(e, ProtectionSection, r.Ok ? r.ProtectionMm2 : 0);
            ok &= SetNumber(e, NeutralSection, r.Ok ? r.NeutralMm2 : 0);
            ok &= SetNumber(e, Breaker, r.Ok ? r.BreakerA : 0);
            ok &= SetNumber(e, VoltageDrop, r.Ok ? r.VoltageDropPercent : 0);
            ok &= SetNumber(e, TotalDrop, r.Ok ? r.TotalDropPercent : 0);
            ok &= SetText(e, Wiring, r.Ok ? r.Wiring : "");
            return ok;
        }

        public static double? GetNumber(Element e, ParamDef d)
        {
            RequireUsableSizing(e, d);
            Parameter p = e?.get_Parameter(d.Guid);
            return p != null && p.HasValue ? p.AsDouble() : (double?)null;
        }

        public static string GetText(Element e, ParamDef d)
        {
            RequireUsableSizing(e, d);
            return e?.get_Parameter(d.Guid)?.AsString();
        }

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
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CircuitoLivre");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "CircuitoLivre_Parametros.txt");
            if (!File.Exists(file)) File.WriteAllText(file, string.Empty);

            try
            {
                app.SharedParametersFilename = file;
                DefinitionFile definitionFile = app.OpenSharedParameterFile()
                    ?? throw new InvalidOperationException("Não foi possível abrir o arquivo de parâmetros do plugin.");

                DefinitionGroup group = definitionFile.Groups.get_Item("CircuitoLivre")
                                        ?? definitionFile.Groups.Create("CircuitoLivre");

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
            try { return p.AsString() == (value ?? string.Empty) || p.Set(value ?? string.Empty); } catch (Exception) { return false; }
        }

        public static bool SetNumber(Element e, ParamDef d, double value)
        {
            Parameter p = e?.get_Parameter(d.Guid);
            if (p == null || p.IsReadOnly) return false;
            try { return double.IsFinite(value) && ((p.HasValue && p.AsDouble() == value) || p.Set(value)); } catch (Exception) { return false; }
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
