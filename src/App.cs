using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RafaelElectrical.Commands;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical
{
    /// <summary>
    /// Ponto de entrada do plugin: cria a aba e os botões na ribbon do Revit.
    /// A ordem dos painéis segue o fluxo de um projeto elétrico: configurar → identificar → coordenar com a arquitetura →
    /// modelar e documentar → utilitários elétricos → circuitos → relatórios → pranchas → modificar → sobre.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "RafaelElectrical";

        private record Item(string Name, string Text, Type Command, string Icon, string Tooltip);

        public Result OnStartup(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* aba já existe */ }

            // Configuração
            RibbonPanel config = Panel(application, "Configuração");
            AddButton(config, new Item("RE_Settings", "Configurações", typeof(SettingsCommand), "settings",
                "Geral, fiação, agrupamento, ocupação, dimensionamento (DR e classes de circuito), conduítes, bandejas, telecom, " +
                "identificadores, cota do conduíte, concessionária/demanda, documentação e exportar/importar ou guardar no projeto."));

            // Identificadores
            RibbonPanel tags = Panel(application, "Identificadores");
            AddSplit(tags, "RE_TagsSplit", "Criar Identificadores\npor Seleção",
                new Item("RE_Tags", "Criar Identificadores\npor Seleção", typeof(CreateTagsCommand), "tags",
                    "Cria identificadores na vista ativa com o tipo configurado para cada categoria (Configurações → Identificadores)."),
                new Item("RE_TagDim", "Criar Identificadores de Dimensão", typeof(TagDimensionCommand), "tagdim",
                    "Marca na vista os eletrodutos/eletrocalhas com tamanho diferente do padrão de cada tipo."),
                new Item("RE_TagLevel", "Identificadores de Infra. de Nível", typeof(TagLevelInfraCommand), "taglevel",
                    "Trechos verticais que sobem, descem ou passam pelo pavimento recebem SOBE / DESCE / PASSA."));

            // Coordenação
            RibbonPanel coord = Panel(application, "Coordenação");
            AddSplit(coord, "RE_LinksSplit", "Nomear Vínculos",
                new Item("RE_NameLinks", "Nomear Vínculos", typeof(NameLinksCommand), "links",
                    "Renomeia os vínculos com um padrão (disciplina detectada pelo nome do arquivo)."),
                new Item("RE_CopyLinks", "Copiar Vínculos entre Pavimentos", typeof(CopyLinksToLevelsCommand), "links",
                    "Copia um vínculo (ex.: pavimento tipo) para outros níveis."),
                new Item("RE_LinkedViews", "Configurar Vistas Vinculadas", typeof(ConfigureLinkedViewsCommand), "links",
                    "Exibe o vínculo pela planta do mesmo nível e em meio-tom."),
                new Item("RE_ImportLoads", "Importar Cargas", typeof(ImportLoadsCommand), "importloads",
                    "Cria pontos de carga a partir de equipamentos ou quadros de um modelo vinculado, com a potência deles."),
                new Item("RE_ImportDiagrams", "Importar Diagramas", typeof(ImportDiagramsCommand), "diagram",
                    "Copia as vistas de desenho (diagramas) de um modelo vinculado para este projeto."),
                new Item("RE_ImportTags", "Importar Identificadores", typeof(ImportTagsCommand), "tags",
                    "Recria na vista ativa os identificadores de uma planta do modelo vinculado, apontando para os elementos do vínculo."),
                new Item("RE_CopyLevels", "Copiar Níveis", typeof(CopyLevelsCommand), "levels",
                    "Cria os níveis do projeto a partir de um vínculo da arquitetura (RVT ou IFC) ou de um arquivo .ifc, com as vistas."));

            // Modelagem e Documentação
            RibbonPanel model = Panel(application, "Modelagem e Documentação");
            AddSplit(model, "RE_AddSplit", "Adicionar\nLuminárias",
                new Item("RE_LightGrid", "Adicionar\nLuminárias", typeof(LightingGridCommand), "lamp",
                    "Dois cliques formam um retângulo dividido em linhas × colunas; uma luminária no centro de cada célula, com rotação opcional."),
                new Item("RE_OutletsByRoom", "Adicionar Tomadas", typeof(OutletsByRoomCommand), "outlet",
                    "Distribui as tomadas no perímetro dos ambientes (modelo ou vínculo): pela NBR 5410, por espaçamento ou por quantidade."),
                new Item("RE_SwitchByDoor", "Adicionar Interruptores", typeof(SwitchByDoorCommand), "switch",
                    "Clique na porta e no lado: o interruptor fica na parede, ao lado do batente, na altura escolhida."),
                new Item("RE_AddDims", "Adicionar Cotas", typeof(AddDimensionsCommand), "cota",
                    "Cota os dispositivos: por seleção (cota linear única) ou por ambiente (até a parede perpendicular)."),
                new Item("RE_LaunchPoints", "Lançar Pontos (livre)", typeof(LaunchPointsCommand), "launch",
                    "Lança qualquer ponto clicando na planta, já com altura e potência."));
            AddSplit(model, "RE_BuildSplit", "Construir\nConduítes",
                new Item("RE_BuildInfra", "Construir\nConduítes", typeof(BuildInfrastructureCommand), "conduit",
                    "Constrói eletroduto ou eletrocalha por cliques sequenciais, seleção por proximidade ou pontos de um espaço MEP e quadro."),
                new Item("RE_BuildTrays", "Construir Bandejas de Cabos", typeof(BuildTraysCommand), "tray",
                    "Liga por eletrocalha os conectores de bandeja alinhados e de frente um para o outro (seleção por retângulo)."),
                new Item("RE_Detour", "Desvio de Infraestrutura", typeof(InfraDetourCommand), "detour",
                    "Clique no início e no fim do trecho: sobe, desce ou desvia para o lado (90° ou 45°)."),
                new Item("RE_Supports", "Adicionar Suportes", typeof(SupportsCommand), "supports",
                    "Insere uma família de suporte ao longo dos eletrodutos/eletrocalhas selecionados."),
                new Item("RE_Unions", "Adicionar Uniões", typeof(SplitWithUnionsCommand), "supports",
                    "Divide os trechos horizontais no comprimento da barra e insere uniões."),
                new Item("RE_MoveRun", "Deslocar Trecho", typeof(AdjustRouteCommand), "route",
                    "Clique num trecho reto e num ponto: o trecho é deslocado até passar pelo ponto."));
            AddButton(model, new Item("RE_Central", "Central de\nDocumentação", typeof(CentralDocumentationCommand), "central",
                "Em sequência: atualizar quadros, identificadores de fiação na vista e diagramas (unifilar, multifilar, DR, proteção)."));
            AddSplit(model, "RE_AdjustTagsSplit", "Ajustar\nIdentificadores",
                new Item("RE_WiringTags", "Criar Identificadores\nde Fiação", typeof(WiringTagsCommand), "adjusttags",
                    "Cria/recria os identificadores de fiação da vista ativa (textos do plugin, com visibilidade e legenda)."),
                new Item("RE_AdjustTags", "Ajustar Identificadores", typeof(AdjustWiringTagsCommand), "adjusttags",
                    "Separa os identificadores de fiação sobrepostos na vista ativa."),
                new Item("RE_VisPreset", "Visibilidade de Identif. Pré-Definida", typeof(WiringVisibilityPresetCommand), "visibility",
                    "Para cada vista: todos, iluminação, iluminação e misto, força, força e misto, alimentador ou não alimentador."),
                new Item("RE_VisCustom", "Visibilidade de Identif. Personalizada", typeof(WiringVisibilityCustomCommand), "visibility",
                    "Escolha circuito a circuito o que aparece nos identificadores da vista ativa."),
                new Item("RE_TagsFromView", "Criar Identificadores Baseado na Vista", typeof(WiringTagsFromViewCommand), "tagsview",
                    "A vista ativa recebe a configuração de outra vista do mesmo nível."));
            AddButton(model, new Item("RE_OptimizeRoute", "Ajustar\nRota", typeof(OptimizeRouteCommand), "route",
                "Procura rotas alternativas para os circuitos dos quadros escolhidos para reduzir agrupamento e ocupação; mostra antes/depois."));
            AddSplit(model, "RE_LegendSplit", "Substituir\npor Legenda",
                new Item("RE_LegendSub", "Substituir\npor Legenda", typeof(WiringLegendSubstituteCommand), "legend",
                    "Os trechos clicados mostram um número/letra no lugar da fiação (mesma fiação = mesmo número)."),
                new Item("RE_LegendView", "Criar Vista de Legenda", typeof(WiringLegendViewCommand), "legendview",
                    "Vista de desenho com o significado de cada legenda de fiação."),
                new Item("RE_LegendResetW", "Reiniciar Legendas", typeof(WiringLegendResetCommand), "legendreset",
                    "Apaga a numeração das legendas de fiação."),
                new Item("RE_LegendAuto", "Auto Substituir por Legenda", typeof(WiringLegendAutoCommand), "legendauto",
                    "Trechos com N circuitos ou mais passam a mostrar legenda."),
                new Item("RE_LegendCreate", "Legenda de Pontos", typeof(LegendCommand), "legend",
                    "Numera cada tipo de ponto (RE_Legenda) e monta a vista \"RE - Legenda de pontos\"."),
                new Item("RE_LegendReset", "Reiniciar Legenda de Pontos", typeof(LegendResetCommand), "legendreset",
                    "Apaga a numeração da legenda de pontos e numera de novo a partir de 1."));
            AddSplit(model, "RE_PathSplit", "Caminho do\nCircuito",
                new Item("RE_HighlightPath", "Caminho do\nCircuito", typeof(HighlightCircuitPathCommand), "path",
                    "Clique num dispositivo, circuito ou quadro: os trechos por onde o circuito passa ficam coloridos na vista ativa."),
                new Item("RE_ClearPath", "Limpar Destaque", typeof(ClearCircuitPathCommand), "path",
                    "Remove da vista ativa as cores aplicadas pelo Caminho do Circuito."));
            AddButton(model, new Item("RE_SizeInfra", "Dimensionar\nInfraestrutura", typeof(SizeInfrastructureCommand), "sizeinfra",
                "Dimensiona os eletrodutos pela taxa de ocupação e cria a vista 3D colorida de verificação."));

            // Utilitários Elétricos
            RibbonPanel electric = Panel(application, "Utilitários Elétricos");
            AddSplit(electric, "RE_CenterSplit", "Centro de Cargas\nSeleção",
                new Item("RE_CenterSel", "Centro de Cargas\nSeleção", typeof(LoadCenterSelectionCommand), "center",
                    "Centro de cargas (ponderado pela potência) dos dispositivos selecionados, marcado na planta."),
                new Item("RE_CenterPanel", "Centro de Cargas Quadro", typeof(LoadCenterPanelCommand), "centerpanel",
                    "Centro de cargas de todos os dispositivos alimentados por um quadro."));
            AddSplit(electric, "RE_DimensionSplit", "Dimensionar\nInstalação",
                new Item("RE_Dimension", "Dimensionar\nInstalação", typeof(DimensionCommand), "dimension",
                    "Dimensiona condutores e disjuntores pela NBR 5410 (quadros selecionados ou projeto inteiro) e exporta o relatório."),
                new Item("RE_DimProject", "Dimensionar Projeto", typeof(DimensionProjectCommand), "dimproject",
                    "Hierarquia de quadros: demanda, corrente do alimentador, curto-circuito estimado e padrão de entrada."),
                new Item("RE_ToggleGrouping", "Descons. Agrupamento", typeof(ToggleGroupingCommand), "grouping",
                    "Marca circuitos que não entram no fator de agrupamento (ex.: eletroduto exclusivo)."),
                new Item("RE_BalancePhases", "Balancear Fases", typeof(BalancePhasesCommand), "balance",
                    "Redistribui os circuitos monofásicos entre as fases A, B e C. Mostra antes/depois e pede confirmação."),
                new Item("RE_SwitchReturns", "Retornos", typeof(SwitchReturnsCommand), "returns",
                    "Clique no interruptor e nas luminárias que ele comanda: próxima letra de retorno do circuito (a, b, c...)."));
            AddButton(electric, new Item("RE_Route", "Atualizar\nQuadros", typeof(RouteCircuitsCommand), "update",
                "Roteia os circuitos pela infraestrutura (usa as rotas do Ajustar Rota): comprimento e agrupamento reais e cabos em cada trecho."));

            // Circuitos (força e telecom)
            RibbonPanel circuits = Panel(application, "Utilitários de Circuitos");
            AddButton(circuits, new Item("RE_CreateCircuits", "Criar\nCircuitos", typeof(CreateCircuitsCommand), "create",
                "Selecione dispositivos (força, dados, telefone, comunicação, segurança...) e um quadro para criar os circuitos."));
            AddSplit(circuits, "RE_OrderSplit", "Ordenar Circuitos\nPor Dispositivos",
                new Item("RE_OrderDevices", "Ordenar Circuitos\nPor Dispositivos", typeof(OrderByDevicesCommand), "orderdev",
                    "Escolha o quadro e clique nos dispositivos na ordem desejada dos circuitos."),
                new Item("RE_OrderPanel", "Ordenar Circuitos Por Quadro", typeof(OrderByPanelCommand), "orderpanel",
                    "Reorganiza os circuitos do quadro: ordem atual, descrição, carga ou posição na planta."),
                new Item("RE_OrderSpaces", "Ordenar Circuitos Por Espaços", typeof(OrderBySpacesCommand), "orderspace",
                    "Escolha o quadro e clique nos ambientes/espaços na ordem desejada."));
            AddButton(circuits, new Item("RE_Identify", "Propriedades\nde Circuitos", typeof(IdentifyCommand), "props",
                "Preenche a identificação (ex.: QD1-05) e o nível nos circuitos e dispositivos, com opção de manter atualizado."));

            // Relatórios
            RibbonPanel reports = Panel(application, "Relatórios");
            AddSplit(reports, "RE_ReportsSplit", "Relatório\nde Fiação",
                new Item("RE_WiringReport", "Relatório\nde Fiação", typeof(WiringReportCommand), "reports",
                    "Quantitativo de cabos por quadro e seção (Excel ou CSV)."),
                new Item("RE_LoadReport", "Relatório de Cargas", typeof(LoadReportCommand), "reportdim",
                    "Lista quadros, circuitos e cargas do modelo, com exportação para Excel."),
                new Item("RE_WiringSchedule", "Criar Tabelas de Fiação do Projeto", typeof(WiringScheduleCommand), "schedule",
                    "Cria a tabela nativa de circuitos com fiação, por quadro ou por nível."),
                new Item("RE_SingleLine", "Diagramas (Unifilar / Multifilar)", typeof(SingleLineDiagramCommand), "diagram",
                    "Gera o unifilar de cada quadro (com DR e DPS) e, conforme as Configurações, o multifilar e o detalhe da proteção."),
                new Item("RE_Memorial", "Memorial Descritivo e de Cálculo", typeof(MemorialCommand), "memorial",
                    "Gera o memorial em Word (.docx) com capa, sumário, demanda e cálculo dos quadros."));

            // Pranchas
            RibbonPanel sheets = Panel(application, "Pranchas");
            AddButton(sheets, new Item("RE_Sheets", "Compor\nPranchas", typeof(ComposeSheetsCommand), "sheets",
                "Cria uma prancha por vista selecionada, com a folha e a numeração escolhidas."));

            // Modificar com Circuitos
            RibbonPanel modify = Panel(application, "Modificar com Circuitos");
            AddSplit(modify, "RE_ModifySplit", "Copiar",
                new Item("RE_CopyCirc", "Copiar", typeof(CopyWithCircuitsCommand), "copy",
                    "Copia dispositivos recriando os circuitos nas cópias."),
                new Item("RE_CopyLevelsCirc", "Copiar para Níveis", typeof(CopyToLevelsCommand), "copy",
                    "Copia dispositivos para outros níveis recriando os circuitos."),
                new Item("RE_Rotate", "Rotacionar", typeof(RotateWithCircuitsCommand), "copy",
                    "Gira os elementos por centro + ângulo inicial e final."),
                new Item("RE_MirrorPts", "Espelhar por pontos", typeof(MirrorByPointsCommand), "copy",
                    "Espelha (cópia) por um eixo de dois pontos, recriando os circuitos."),
                new Item("RE_MirrorLine", "Espelhar por linha", typeof(MirrorByLineCommand), "copy",
                    "Espelha (cópia) por uma linha existente, recriando os circuitos."));

            // Sobre
            RibbonPanel about = Panel(application, "Sobre");
            AddButton(about, new Item("RE_About", "Sobre", typeof(AboutCommand), "about",
                $"RafaelElectrical {RevitHelpers.PluginVersion}: versão, licença GPL-3.0 e links do projeto."));
            AddButton(about, new Item("RE_ReportBug", "Reportar\nErro", typeof(ReportBugCommand), "bug",
                "Envia um erro ou sugestão (com imagem, se quiser) para o desenvolvedor."));
            AddButton(about, new Item("RE_InspectParameters", "Inspecionar\nParâmetros", typeof(ParameterInspectorCommand), "inspect",
                "Lista os parâmetros dos elementos elétricos do modelo (inclusive os criados por outros plugins) e exporta para CSV."));
            AddButton(about, new Item("RE_Pane", "Painel\nRafaelElectrical", typeof(ShowCommandsPaneCommand), "settings",
                "Mostra o painel lateral com todos os comandos e uma busca."));

            // Painel lateral com todos os comandos
            try
            {
                var handler = new PostCommandHandler();
                ExternalEvent ev = ExternalEvent.Create(handler);
                application.RegisterDockablePane(CommandsPane.Id, "RafaelElectrical", new CommandsPane(PaneCommands, ev, handler));
            }
            catch (Exception) { /* painel indisponível nesta sessão */ }

            // Dados guardados no projeto: configurações e identificação automática
            application.ViewActivated += OnViewActivated;
            try { AutoIdentificationUpdater.Register(application.ActiveAddInId); }
            catch (Exception) { /* sem identificação automática nesta sessão */ }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ViewActivated -= OnViewActivated;
            try { AutoIdentificationUpdater.Unregister(application.ActiveAddInId); } catch (Exception) { }
            return Result.Succeeded;
        }

        /// <summary>Ao trocar de vista/documento, passa a usar as configurações guardadas no projeto ativo (se houver).</summary>
        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                DimensioningSettings.ActiveProjectJson = ProjectStorage.Get(e.Document, ProjectStorage.SettingsField);
                DimensioningSettings.ProjectDirty = false;
            }
            catch (Exception) { DimensioningSettings.ActiveProjectJson = null; }
        }

        private static RibbonPanel Panel(UIControlledApplication application, string name) =>
            application.CreateRibbonPanel(TabName, name);

        private static PushButtonData Data(Item item) =>
            new PushButtonData(item.Name, item.Text, item.Command.Assembly.Location, item.Command.FullName)
            {
                ToolTip = item.Tooltip,
                LargeImage = LoadImage($"{item.Icon}32.png"),
                Image = LoadImage($"{item.Icon}16.png"),
            };

        /// <summary>Todos os comandos, para o painel lateral.</summary>
        public static readonly List<PaneCommand> PaneCommands = new();

        private static void Register(RibbonPanel panel, Item item, string split)
        {
            var ids = new List<string>();
            if (split == null) ids.Add($"CustomCtrl_%CustomCtrl_%{TabName}%{panel.Name}%{item.Name}");
            else
            {
                ids.Add($"CustomCtrl_%CustomCtrl_%CustomCtrl_%{TabName}%{panel.Name}%{split}%{item.Name}");
                ids.Add($"CustomCtrl_%CustomCtrl_%{TabName}%{panel.Name}%{split}%{item.Name}");
            }
            PaneCommands.Add(new PaneCommand { Panel = panel.Name, Text = item.Text, Tooltip = item.Tooltip, Icon = LoadImage($"{item.Icon}32.png"), CommandIds = ids });
        }

        private static void AddButton(RibbonPanel panel, Item item)
        {
            panel.AddItem(Data(item));
            Register(panel, item, null);
        }

        /// <summary>
        /// Botão dividido: o clique principal roda o primeiro comando; a seta abre os outros.
        /// O botão passa a mostrar o último usado.
        /// </summary>
        private static void AddSplit(RibbonPanel panel, string name, string text, params Item[] items)
        {
            if (panel.AddItem(new SplitButtonData(name, text)) is not SplitButton split) return;
            split.IsSynchronizedWithCurrentItem = true;
            foreach (Item item in items)
            {
                split.AddPushButton(Data(item));
                Register(panel, item, name);
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
