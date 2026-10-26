using System;
using Autodesk.Revit.DB;

namespace CircuitoLivre.Services
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

    /// <summary>
    /// Descarta avisos e, se houver qualquer erro, desfaz a transação (ProceedWithRollBack) sem mostrar
    /// diálogo. Usado em operações opcionais (curvas/tês/ligações) que podem falhar por geometria: a operação
    /// é descartada sozinha e o resto do modelo fica intacto.
    /// </summary>
    public class RollbackOnError : IFailuresPreprocessor
    {
        public bool HadError { get; private set; }
        public string Error { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else
                {
                    HadError = true;
                    Error ??= f.GetDescriptionText();
                }
            return HadError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        /// <summary>Executa uma operação numa transação própria; se postar erro, desfaz e retorna false.</summary>
        public static bool Try(Document doc, string name, Func<bool> op) => Try(doc, name, op, out _);

        public static bool Try(Document doc, string name, Func<bool> op, out string failure)
        {
            failure = null;
            using var t = new Transaction(doc, name);
            t.Start();
            var handler = new RollbackOnError();
            FailureHandlingOptions o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(handler);
            o.SetClearAfterRollback(true);
            o.SetForcedModalHandling(true);
            t.SetFailureHandlingOptions(o);
            try
            {
                if (op())
                {
                    bool committed = t.Commit() == TransactionStatus.Committed && !handler.HadError;
                    if (!committed) failure = handler.Error ?? "O Revit não confirmou a transação.";
                    return committed;
                }
                failure = "A operação não concluiu a conexão.";
            }
            catch (Exception ex) { failure = handler.Error ?? ex.Message; }
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            return false;
        }
    }
}
