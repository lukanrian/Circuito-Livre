using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RafaelElectrical.Commands;

namespace RafaelElectrical
{
    /// <summary>
    /// Ponto de entrada do plugin: cria a aba e os botões na ribbon do Revit.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "RafaelElectrical";

        public Result OnStartup(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* aba já existe */ }

            // Configuração
            RibbonPanel config = application.CreateRibbonPanel(TabName, "Configuração (beta)");
            AddButton(config, "RE_Settings", "Configurações", typeof(SettingsCommand), "settings",
                "Temperatura, seções mínima/máxima, cores dos cabos, fatores de agrupamento, taxas de ocupação, método do disjuntor, " +
                "queda de tensão total, cabos de telecom, DPS no diagrama e exportar/importar (.json).");

            // Coordenação
            RibbonPanel coord = application.CreateRibbonPanel(TabName, "Coordenação");
            AddButton(coord, "RE_CopyLevels", "Copiar\nNíveis", typeof(CopyLevelsCommand), "levels",
                "Cria os níveis do projeto a partir de um vínculo da arquitetura (RVT ou IFC vinculado) ou de um arquivo .ifc.");
            AddPulldown(coord, "RE_Links", "Vínculos", "links",
                ("RE_NameLinks", "Nomear Vínculos", typeof(NameLinksCommand), "Renomeia os vínculos com um padrão (disciplina detectada pelo nome do arquivo)."),
                ("RE_CopyLinks", "Copiar Vínculos entre Pavimentos", typeof(CopyLinksToLevelsCommand), "Copia um vínculo (ex.: pavimento tipo) para outros níveis."),
                ("RE_LinkedViews", "Configurar Vistas Vinculadas", typeof(ConfigureLinkedViewsCommand), "Exibe o vínculo pela planta do mesmo nível e em meio-tom."));

            // Lançamento
            RibbonPanel launch = application.CreateRibbonPanel(TabName, "Lançamento");
            AddButton(launch, "RE_LaunchPoints", "Lançar\nPontos", typeof(LaunchPointsCommand), "launch",
                "Lança luminárias, tomadas, interruptores e pontos de telecom com as suas famílias, já com altura e potência.");
            AddPulldown(launch, "RE_Modify", "Modificar com\nCircuitos", "modify",
                ("RE_CopyCirc", "Copiar", typeof(CopyWithCircuitsCommand), "Copia dispositivos recriando os circuitos nas cópias."),
                ("RE_CopyLevelsCirc", "Copiar para Níveis", typeof(CopyToLevelsCommand), "Copia dispositivos para outros níveis recriando os circuitos."),
                ("RE_Rotate", "Rotacionar", typeof(RotateWithCircuitsCommand), "Gira os elementos por centro + ângulo inicial e final."),
                ("RE_MirrorPts", "Espelhar por Pontos", typeof(MirrorByPointsCommand), "Espelha (cópia) por um eixo de dois pontos, recriando os circuitos."),
                ("RE_MirrorLine", "Espelhar por Linha", typeof(MirrorByLineCommand), "Espelha (cópia) por uma linha existente, recriando os circuitos."));

            // Circuitos
            RibbonPanel panel = application.CreateRibbonPanel(TabName, "Circuitos");
            AddButton(panel, "RE_CreateCircuits", "Criar\nCircuitos", typeof(CreateCircuitsCommand), "create",
                "Selecione dispositivos (força, dados, telefone, comunicação, segurança...) e um quadro para criar os circuitos.");
            AddButton(panel, "RE_RenumberCircuits", "Renumerar\nCircuitos", typeof(RenumberCircuitsCommand), "renumber",
                "Reorganiza os circuitos de um quadro de força ou telecom: ordem atual, descrição, carga, ordem de clique, ambientes ou posição na planta.");
            AddButton(panel, "RE_BalancePhases", "Balancear\nFases", typeof(BalancePhasesCommand), "balance",
                "Redistribui os circuitos monofásicos de um quadro trifásico entre as fases A, B e C para reduzir o desequilíbrio. Mostra antes/depois e pede confirmação.");
            AddButton(panel, "RE_SwitchReturns", "Retornos", typeof(SwitchReturnsCommand), "returns",
                "Clique no interruptor e nas luminárias que ele comanda: o plugin dá a próxima letra de retorno do circuito (a, b, c...) e grava em RE_Retorno.");
            AddButton(panel, "RE_Identify", "Preencher\nIdentificação", typeof(IdentifyCommand), "identify",
                "Preenche a identificação (ex.: QD1-05) e o nível nos circuitos de força/telecom e nos dispositivos ligados a eles.");
            AddButton(panel, "RE_LoadReport", "Relatório\nde Cargas", typeof(LoadReportCommand), "report",
                "Lista quadros, circuitos e cargas do modelo, com exportação para CSV (Excel).");

            // Dimensionamento
            RibbonPanel sizing = application.CreateRibbonPanel(TabName, "Dimensionamento");
            AddButton(sizing, "RE_Dimension", "Dimensionar\nInstalação", typeof(DimensionCommand), "dimension",
                "Dimensiona condutores e disjuntores dos circuitos de força pela NBR 5410 (quadros selecionados ou projeto inteiro) e exporta o relatório.");
            AddButton(sizing, "RE_LoadCenter", "Centro de\nCargas", typeof(LoadCenterCommand), "center",
                "Calcula o centro de cargas (ponderado pela potência) dos dispositivos selecionados ou de um quadro e marca na planta.");
            AddButton(sizing, "RE_ToggleGrouping", "Desconsiderar\nAgrupamento", typeof(ToggleGroupingCommand), "grouping",
                "Marca circuitos que não entram no fator de agrupamento (ex.: eletroduto exclusivo).");

            // Infraestrutura
            RibbonPanel infra = application.CreateRibbonPanel(TabName, "Infraestrutura");
            AddButton(infra, "RE_BuildInfra", "Construir\nInfraestrutura", typeof(BuildInfrastructureCommand), "build",
                "Constrói eletroduto ou eletrocalha ligando, em sequência, os elementos clicados (quadro primeiro).");
            AddButton(infra, "RE_Route", "Atualizar\nQuadros", typeof(RouteCircuitsCommand), "route",
                "Roteia os circuitos pela infraestrutura: comprimento real, agrupamento real e cabos em cada trecho (RE_Circuitos / RE_Cabos).");
            AddButton(infra, "RE_SizeInfra", "Dimensionar\nInfraestrutura", typeof(SizeInfrastructureCommand), "sizeinfra",
                "Dimensiona os eletrodutos pela taxa de ocupação e cria a vista 3D colorida de verificação.");
            AddButton(infra, "RE_Supports", "Adicionar\nSuportes", typeof(SupportsCommand), "supports",
                "Insere uma família de suporte ao longo dos eletrodutos/eletrocalhas selecionados.");

            // Documentação
            RibbonPanel docs = application.CreateRibbonPanel(TabName, "Documentação");
            AddButton(docs, "RE_Tags", "Criar\nIdentificadores", typeof(CreateTagsCommand), "tags",
                "Cria identificadores na vista ativa: elementos selecionados, todos os dispositivos ou a infraestrutura (fiação).");
            AddButton(docs, "RE_SingleLine", "Diagrama\nUnifilar", typeof(SingleLineDiagramCommand), "diagram",
                "Gera o diagrama unifilar de cada quadro numa vista de desenho.");
            AddPulldown(docs, "RE_Reports", "Relatórios", "reports",
                ("RE_WiringReport", "Relatório de Fiação", typeof(WiringReportCommand), "Quantitativo de cabos por quadro e seção (CSV)."),
                ("RE_WiringSchedule", "Criar Tabelas de Fiação", typeof(WiringScheduleCommand), "Cria a tabela nativa de circuitos com fiação, por quadro ou por nível."),
                ("RE_Memorial", "Memorial Descritivo e de Cálculo", typeof(MemorialCommand), "Gera o memorial em Word (.docx) com capa, sumário e cálculo dos quadros."));
            AddButton(docs, "RE_Sheets", "Compor\nPranchas", typeof(ComposeSheetsCommand), "sheets",
                "Cria uma prancha por vista selecionada, com a folha e a numeração escolhidas.");

            // Ferramentas
            RibbonPanel tools = application.CreateRibbonPanel(TabName, "Ferramentas");
            AddButton(tools, "RE_InspectParameters", "Inspecionar\nParâmetros", typeof(ParameterInspectorCommand), "inspect",
                "Lista os parâmetros dos elementos elétricos do modelo (inclusive os criados por outros plugins) e exporta para CSV.");
            AddButton(tools, "RE_ReportBug", "Reportar\nErro", typeof(ReportBugCommand), "bug",
                "Abre o GitHub com um formulário já preenchido (versões do Revit e do plugin) para reportar um erro ou sugerir uma função.");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private static void AddButton(RibbonPanel panel, string name, string text, Type command, string icon, string tooltip)
        {
            var data = new PushButtonData(name, text, command.Assembly.Location, command.FullName)
            {
                ToolTip = tooltip,
                LargeImage = LoadImage($"{icon}32.png"),
                Image = LoadImage($"{icon}16.png"),
            };
            panel.AddItem(data);
        }

        private static void AddPulldown(RibbonPanel panel, string name, string text, string icon,
            params (string Name, string Text, Type Command, string Tooltip)[] items)
        {
            var data = new PulldownButtonData(name, text)
            {
                LargeImage = LoadImage($"{icon}32.png"),
                Image = LoadImage($"{icon}16.png"),
            };
            if (panel.AddItem(data) is not PulldownButton pulldown) return;
            foreach (var item in items)
            {
                pulldown.AddPushButton(new PushButtonData(item.Name, item.Text, item.Command.Assembly.Location, item.Command.FullName)
                {
                    ToolTip = item.Tooltip,
                    LargeImage = LoadImage($"{icon}32.png"),
                    Image = LoadImage($"{icon}16.png"),
                });
            }
        }

        private static BitmapImage LoadImage(string fileName)
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"RafaelElectrical.Resources.{fileName}");
            if (stream == null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
