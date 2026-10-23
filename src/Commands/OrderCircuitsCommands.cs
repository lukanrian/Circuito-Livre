using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>Ordenar circuitos na ordem em que o usuário clicar nos dispositivos.</summary>
    [Transaction(TransactionMode.Manual)]
    public class OrderByDevicesCommand : RenumberCircuitsCommand
    {
        protected override RenumberMode? Preset => RenumberMode.ByClick;
    }

    /// <summary>Ordenar circuitos do quadro (ordem atual, descrição, carga ou posição).</summary>
    [Transaction(TransactionMode.Manual)]
    public class OrderByPanelCommand : RenumberCircuitsCommand
    {
        protected override RenumberMode? Preset => RenumberMode.Compact;
    }

    /// <summary>Ordenar circuitos na ordem em que o usuário clicar nos ambientes/espaços.</summary>
    [Transaction(TransactionMode.Manual)]
    public class OrderBySpacesCommand : RenumberCircuitsCommand
    {
        protected override RenumberMode? Preset => RenumberMode.BySpaces;
    }

    /// <summary>Centro de cargas dos dispositivos selecionados.</summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadCenterSelectionCommand : LoadCenterCommand
    {
        protected override TaskDialogResult? Preset => TaskDialogResult.CommandLink1;
    }

    /// <summary>Centro de cargas de todos os circuitos de um quadro.</summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadCenterPanelCommand : LoadCenterCommand
    {
        protected override TaskDialogResult? Preset => TaskDialogResult.CommandLink2;
    }
}
