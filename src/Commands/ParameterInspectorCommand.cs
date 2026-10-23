using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Lista os parâmetros presentes nos elementos elétricos do modelo aberto (circuitos, quadros,
    /// dispositivos, luminárias, eletrodutos, fiação...) e exporta para CSV.
    /// Serve para descobrir quais parâmetros um projeto feito em outro software já traz,
    /// para o CircuitoLivre poder ler e respeitar esses dados.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ParameterInspectorCommand : IExternalCommand
    {
        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_ElectricalCircuit,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_SecurityDevices,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_Wire,
        };

        private class ParamStat
        {
            public string Category, Level, Name, Origin, Guid, DataType, Sample;
            public int Count, WithValue;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            var dialog = new TaskDialog("Inspecionar parâmetros")
            {
                MainInstruction = "Quais parâmetros listar?",
                MainContent = "Analisa circuitos, quadros, dispositivos, luminárias, eletrodutos, eletrocalhas e fiação do modelo aberto.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Somente personalizados",
                "Parâmetros compartilhados, de projeto e de família (ignora os nativos do Revit).");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Todos os parâmetros");

            bool onlyCustom;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: onlyCustom = true; break;
                case TaskDialogResult.CommandLink2: onlyCustom = false; break;
                default: return Result.Cancelled;
            }

            IList<Element> instances = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(Categories))
                .WhereElementIsNotElementType()
                .ToElements();

            if (instances.Count == 0)
            {
                TaskDialog.Show("Inspecionar parâmetros", "Nenhum elemento elétrico encontrado neste modelo.");
                return Result.Cancelled;
            }

            var stats = new Dictionary<string, ParamStat>();
            var seenTypes = new HashSet<ElementId>();

            foreach (Element e in instances)
            {
                string category = e.Category?.Name ?? "(sem categoria)";
                Collect(stats, category, "Instância", e, onlyCustom);

                ElementId typeId = e.GetTypeId();
                if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId))
                {
                    Element type = doc.GetElement(typeId);
                    if (type != null) Collect(stats, category, "Tipo", type, onlyCustom);
                }
            }

            var save = new SaveFileDialog
            {
                Title = "Salvar lista de parâmetros",
                Filter = "CSV (Excel) (*.csv)|*.csv",
                FileName = $"Parametros_{Sanitize(doc.Title)}.csv",
            };
            if (save.ShowDialog() != true) return Result.Cancelled;

            var sb = new StringBuilder();
            sb.AppendLine("Categoria;Nível;Parâmetro;Origem;GUID;Tipo de dado;Elementos;Com valor;Exemplo de valor");
            foreach (ParamStat s in stats.Values
                         .OrderBy(s => s.Category, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(s => s.Level)
                         .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.AppendLine(string.Join(";", Esc(s.Category), s.Level, Esc(s.Name), s.Origin, s.Guid,
                    Esc(s.DataType), s.Count, s.WithValue, Esc(s.Sample)));
            }

            try
            {
                File.WriteAllText(save.FileName, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Inspecionar parâmetros", $"Não foi possível salvar o arquivo:\n{ex.Message}");
                return Result.Failed;
            }

            TaskDialog.Show("Inspecionar parâmetros",
                $"{stats.Count} parâmetro(s) encontrados em {instances.Count} elemento(s).\n\nArquivo salvo em:\n{save.FileName}");
            return Result.Succeeded;
        }

        private static void Collect(Dictionary<string, ParamStat> stats, string category, string level, Element e, bool onlyCustom)
        {
            foreach (Parameter p in e.Parameters)
            {
                Definition def = p.Definition;
                if (def == null) continue;

                bool builtIn = def is InternalDefinition internalDef && internalDef.BuiltInParameter != BuiltInParameter.INVALID;
                if (onlyCustom && builtIn) continue;

                string origin = builtIn ? "Nativo" : p.IsShared ? "Compartilhado" : "Projeto/Família";
                string guid = p.IsShared ? SafeGuid(p) : "";
                string key = $"{category}|{level}|{def.Name}|{guid}";

                if (!stats.TryGetValue(key, out ParamStat s))
                {
                    s = new ParamStat
                    {
                        Category = category,
                        Level = level,
                        Name = def.Name,
                        Origin = origin,
                        Guid = guid,
                        DataType = SafeDataType(def),
                    };
                    stats[key] = s;
                }

                s.Count++;
                if (p.HasValue)
                {
                    s.WithValue++;
                    if (string.IsNullOrEmpty(s.Sample))
                    {
                        try { s.Sample = p.AsValueString() ?? p.AsString(); } catch (Exception) { }
                    }
                }
            }
        }

        private static string SafeGuid(Parameter p)
        {
            try { return p.GUID.ToString(); } catch (Exception) { return ""; }
        }

        private static string SafeDataType(Definition def)
        {
            try { return def.GetDataType()?.TypeId ?? ""; } catch (Exception) { return ""; }
        }

        private static string Esc(string value)
        {
            value ??= "";
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
