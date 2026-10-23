using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Dados do Circuito Livre gravados dentro do próprio projeto (.rvt), por Extensible Storage,
    /// num único DataStorage. Assim as opções vão junto com o arquivo para qualquer computador.
    /// O esquema é fixo pelo GUID: para novos dados, usar o campo "Extra" (JSON).
    /// </summary>
    public static class ProjectStorage
    {
        private static readonly Guid SchemaGuid = new Guid("4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1b01");
        private const string SchemaName = "CircuitoLivreProjeto";

        public const string SettingsField = "Configuracoes";
        public const string AutoIdField = "IdentificacaoAutomatica";
        public const string ExtraField = "Extra";

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetDocumentation("Configurações do plugin Circuito Livre guardadas no projeto.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(SettingsField, typeof(string));
            builder.AddSimpleField(AutoIdField, typeof(string));
            builder.AddSimpleField(ExtraField, typeof(string));
            return builder.Finish();
        }

        private static DataStorage Find(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(ds => ds.GetEntity(schema).IsValid());

        /// <summary>Lê um campo (null se o projeto não tiver o dado).</summary>
        public static string Get(Document doc, string field)
        {
            if (doc == null || doc.IsFamilyDocument) return null;
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;
                DataStorage ds = Find(doc, schema);
                if (ds == null) return null;
                string value = ds.GetEntity(schema).Get<string>(field);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Grava um campo (null/vazio apaga). Precisa estar dentro de uma transação.</summary>
        public static void Set(Document doc, string field, string value)
        {
            Schema schema = GetSchema();
            DataStorage ds = Find(doc, schema);
            if (ds == null)
            {
                if (string.IsNullOrEmpty(value)) return;
                ds = DataStorage.Create(doc);
                try { ds.Name = "CircuitoLivre"; } catch (Exception) { /* nome é opcional */ }
            }
            Entity entity = ds.GetEntity(schema);
            if (!entity.IsValid()) entity = new Entity(schema);
            entity.Set(field, value ?? string.Empty);
            ds.SetEntity(entity);
        }

        /// <summary>Lê uma chave do campo "Extra" (objeto JSON com várias chaves). Null se não existir.</summary>
        public static string GetExtra(Document doc, string key)
        {
            string json = Get(doc, ExtraField);
            if (json == null) return null;
            try { return System.Text.Json.Nodes.JsonNode.Parse(json)?[key]?.ToJsonString(); }
            catch (Exception) { return null; }
        }

        /// <summary>Grava uma chave do campo "Extra" (valorJson = JSON do valor; null apaga). Precisa de transação.</summary>
        public static void SetExtra(Document doc, string key, string valueJson)
        {
            System.Text.Json.Nodes.JsonObject obj;
            try { obj = System.Text.Json.Nodes.JsonNode.Parse(Get(doc, ExtraField) ?? "{}") as System.Text.Json.Nodes.JsonObject; }
            catch (Exception) { obj = null; }
            obj ??= new System.Text.Json.Nodes.JsonObject();
            obj.Remove(key);
            if (valueJson != null) obj[key] = System.Text.Json.Nodes.JsonNode.Parse(valueJson);
            Set(doc, ExtraField, obj.ToJsonString());
        }

        /// <summary>
        /// Se as configurações do projeto foram alteradas por uma janela (ex.: Dimensionar), grava no projeto.
        /// Precisa estar dentro de uma transação.
        /// </summary>
        public static void FlushSettings(Document doc)
        {
            if (!DimensioningSettings.ProjectDirty || DimensioningSettings.ActiveProjectJson == null) return;
            Set(doc, SettingsField, DimensioningSettings.ActiveProjectJson);
            DimensioningSettings.ProjectDirty = false;
        }
    }
}
