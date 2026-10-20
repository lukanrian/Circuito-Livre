using Autodesk.Revit.DB;

namespace RafaelElectrical.Services
{
    /// <summary>
    /// Descarta os avisos (não os erros) do Revit durante uma transação do plugin, para não interromper
    /// o usuário com dezenas de janelas. Conta quantos foram descartados.
    /// </summary>
    public class WarningSwallower : IFailuresPreprocessor
    {
        public int Discarded { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() != FailureSeverity.Warning) continue;
                accessor.DeleteWarning(f);
                Discarded++;
            }
            return FailureProcessingResult.Continue;
        }

        /// <summary>Liga o descarte de avisos na transação (chamar depois de Start).</summary>
        public static WarningSwallower Attach(Transaction t)
        {
            var swallower = new WarningSwallower();
            FailureHandlingOptions options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            t.SetFailureHandlingOptions(options);
            return swallower;
        }
    }
}
