using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>Identificadores de fiação nos eletrodutos/eletrocalhas da vista ativa.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateWiringTagsCommand : CreateTagsCommand
    {
        protected override TaskDialogResult? Preset => TaskDialogResult.CommandLink3;
    }

    /// <summary>
    /// Central de documentação: roda em sequência o roteamento dos quadros (fiação e quantitativos nos trechos),
    /// os identificadores de fiação na vista ativa e o diagrama unifilar.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CentralDocumentationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var form = new FormWindow("Central de documentação", "Escolha as etapas. Cada uma abre a própria janela, na ordem abaixo.");
            var route = form.Check("1. Atualizar quadros: rotear os circuitos e gravar a fiação e o quantitativo nos trechos (RE_Cabos)", true);
            var tags = form.Check("2. Criar identificadores de fiação na vista ativa (textos do plugin, com visibilidade e legenda)", true);
            var familyTags = form.Check("2b. Usar famílias de identificador de eletroduto (RE_Cabos) em vez dos textos", false);
            var diagram = form.Check("3. Gerar o diagrama unifilar (e multifilar/DR, conforme as Configurações) dos quadros", false);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            Result last = Result.Cancelled;
            if (route.IsChecked == true)
            {
                last = new RouteCircuitsCommand().Execute(data, ref message, elements);
                if (last == Result.Failed) return last;
            }
            if (tags.IsChecked == true)
                last = familyTags.IsChecked == true
                    ? new CreateWiringTagsCommand().Execute(data, ref message, elements)
                    : new WiringTagsCommand().Execute(data, ref message, elements);
            if (diagram.IsChecked == true) last = new SingleLineDiagramCommand().Execute(data, ref message, elements);
            return last == Result.Failed ? Result.Failed : Result.Succeeded;
        }
    }
}
