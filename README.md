# Circuito Livre — plugin elétrico open source para Revit

Plugin gratuito e de código aberto para **projetos elétricos e de telecom no Autodesk Revit 2025, 2026 e 2027**,
com dimensionamento pela **ABNT NBR 5410**. Feito em C# (.NET 8 / .NET 10, WPF), sem dependências externas.

> ⚠️ **Aviso de responsabilidade técnica**
> Este software é uma ferramenta de apoio ao projeto. Os resultados (seções, disjuntores, quedas de tensão,
> fatores, ocupação de eletrodutos etc.) **devem ser conferidos por profissional legalmente habilitado**, que assina
> o projeto e emite a ART/RRT. As tabelas da NBR 5410 foram digitadas a partir da norma e precisam ser conferidas com
> a edição vigente. O software é distribuído **sem nenhuma garantia** (veja a [licença](LICENSE)).

> 🚧 **Versão 0.3.15 beta — em desenvolvimento.** Várias funções ainda não foram testadas em projeto real. Relate o que encontrar nas [issues](../../issues).

## Funções

| Painel | Comandos |
|---|---|
| **Configuração** | Configurações: geral (retornos, ordem da legenda), fiação, agrupamento (Tab. 42), ocupação, dimensionamento (método do disjuntor, queda total, DR, sobrecarga, classes de circuito com curva/neutro/terra), conduítes (diâmetro padrão por tipo), bandejas, telecom, identificadores por categoria, cota do conduíte por nível, concessionária (padrão de entrada, fatores de demanda, curto-circuito), documentação (terra, tags, multifilar, proteção, DPS); exportar/importar `.json` ou guardar no projeto |
| **Identificadores** | Criar por Seleção (tipo por categoria), de Dimensão (diâmetro fora do padrão), de Infra. de Nível (SOBE / DESCE / PASSA) |
| **Coordenação** | Nomear / Copiar / Configurar Vínculos, Importar Cargas (equipamentos e quadros vinculados), Importar Diagramas, Importar Identificadores, Copiar Níveis (RVT/IFC) |
| **Modelagem e Documentação** | Adicionar Luminárias (malha), Tomadas (NBR 5410, espaçamento ou quantidade), Interruptores (pela porta), Cotas; Construir Conduítes / Bandejas, Desvio, Suportes, Uniões, Deslocar Trecho; Central de Documentação; Identificadores de fiação com Ajustar, Visibilidade pré-definida/personalizada e Baseado na Vista; Ajustar Rota (rotas alternativas para reduzir agrupamento); Substituir por Legenda, Vista de Legenda, Reiniciar, Auto; Legenda de Pontos; Caminho do Circuito; Dimensionar Infraestrutura |

| **Utilitários Elétricos** | Centro de Cargas (seleção / quadro); Dimensionar Instalação (NBR 5410), Dimensionar Projeto (demanda, curto-circuito, padrão de entrada), Descons. Agrupamento, Balancear Fases, Retornos; Atualizar Quadros |
| **Utilitários de Circuitos** | Criar Circuitos; Ordenar Circuitos por dispositivos / quadro / espaços; Propriedades de Circuitos (identificação automática) |
| **Relatórios** | Relatório de Fiação e de Cargas (Excel), Tabelas de Fiação, Diagramas (unifilar com DR/DPS, multifilar, detalhe da proteção), Memorial Descritivo e de Cálculo (`.docx`) |
| **Pranchas** | Compor Pranchas |
| **Modificar com Circuitos** | Copiar, Copiar para Níveis, Rotacionar, Espelhar por pontos ou por linha |
| **Sobre** | Sobre, Reportar Erro, Inspecionar Parâmetros, Painel lateral com todos os comandos e busca |

**Construir Conduítes** usa somente o lançamento sequencial: clique nos componentes na ordem do caminho e pressione ESC para terminar a seleção. A janela e o painel lateral usam o mesmo cálculo, com altura por nível, orientação do L, ângulos em planta e parede e modo de conexão. O lançamento procura pares de conectores livres compatíveis, começando pelos mais próximos, e tenta seis afastamentos progressivos a partir do maior valor entre 10 cm e quatro diâmetros. Em orientação Padrão, testa também a alternativa do L; uma orientação explícita é preservada. Dois componentes hospedados na mesma parede reta usam o eixo dessa parede. O percurso precisa respeitar a direção 3D dos conectores, o comprimento mínimo e a geometria das curvas, sem se cruzar. Se uma curva ou ligação falhar no Revit, todos os elementos daquela tentativa são desfeitos antes da próxima; os percursos já concluídos permanecem. O resumo informa percursos não criados, extremidades sem conector e até três motivos de falha, identificando criação dos trechos, curva ou ligação ao componente. Percursos geométricos idênticos são tentados uma única vez para cada par de conectores. Componentes intermediários precisam de conectores livres para entrada e saída; não são criadas derivações automáticas em um único conector ocupado. Nos modos Priorizar teto e Pelo piso, pontos reconhecidos em parede usam uma transição no plano horizontal escolhido, no mesmo alinhamento da saída existente, mantendo a subida ou descida no seu eixo. No lançamento de eletrodutos Pelo piso, as saídas inferiores livres têm prioridade; quando necessário, o percurso se adapta à direção de outra saída compatível, incluindo afastamento para uma saída superior antes da descida. Sem conector real livre, o percurso não é criado e o resumo identifica o componente. Essa regra não altera teto, mesma parede ou eletrocalhas. Pelo piso usa elevação própria relativa ao nível da planta (padrão -0,50 m), com descida, trecho horizontal e subida; valores negativos ficam abaixo do nível. A janela e o painel lateral mantêm os campos de elevação separados ao alternar entre teto e piso. O tipo horizontal selecionado vale para teto ou piso, e o tipo de parede vale para subidas e descidas. O reconhecimento usa hospedagem nativa ou por face e, para famílias sem hospedeiro, busca faces verticais planas na categoria Paredes até 10 cm, inclusive em vínculos carregados e transformados. A solução não procura obstáculos no modelo, não resolve faces curvas de IFC nem garante a menor rede global ou o cobrimento na parede. Quando uma caixa próxima ao teto tem desnível menor que o afastamento da saída, a transição absorve essa diferença no trecho seguinte, evitando um pequeno trecho vertical sem espaço para duas curvas; saídas reconhecidas em parede mantêm seu eixo vertical. Não altera a posição dos conectores nem incorpora código ou DLLs de terceiros. O diâmetro continua a ser calculado pelo comando **Dimensionar Infraestrutura** depois que os circuitos e cabos estiverem roteados.

O plugin grava seus resultados em parâmetros compartilhados próprios, com prefixo `CL_`
(ex.: `CL_Seção (mm²)`, `CL_Disjuntor (A)`, `CL_Queda total (%)`, `CL_Fiação`).

### Fluxo sugerido
Configurações → Copiar Níveis → Lançar Pontos → Criar Circuitos → Ordenar Circuitos → Construir Infraestrutura →
Atualizar Quadros → Dimensionar Instalação → Dimensionar Infraestrutura → Atualizar Quadros →
Preencher Identificação → Identificadores → Unifilar / Tabelas / Relatórios / Memorial → Compor Pranchas.

## Instalar (versão compilada)
1. Baixe o `CircuitoLivre.bundle.zip` em **[Releases](../../releases)**.
2. Extraia todo o ZIP e execute `Instalar.ps1` no PowerShell, com o Revit fechado. Também é possível copiar a pasta `CircuitoLivre.bundle` para `%APPDATA%\Autodesk\ApplicationPlugins`.
3. Abra o Revit: aparece a aba **Circuito Livre**.

Para desinstalar, apague essa pasta.

## Compilar a partir do código
Pré-requisitos (Windows):
- Para **compilar** o código: [.NET 10 SDK](https://dotnet.microsoft.com/download) e acesso ao NuGet para restaurar as referências de compilação das APIs 2025–2027. Não é necessário ter o Revit instalado para compilar, e nenhuma DLL da API vai para o pacote. Para **instalar o ZIP pronto**, não é necessário instalar o SDK.

Passos:
1. Feche o Revit.
2. Rode `COMPILAR_E_INSTALAR.bat` para compilar as três versões e instalar o bundle, ou
   `powershell -ExecutionPolicy Bypass -File build.ps1` para só gerar `dist\CircuitoLivre.bundle` e o `.zip`. O build falha se qualquer uma das três versões não puder ser compilada.

## Estrutura
```
src/
  App.cs                 ribbon (IExternalApplication)
  Commands/              comandos (IExternalCommand)
  Services/              NBR 5410, dimensionamento, roteamento, parâmetros, DOCX, IFC
  Selection/             filtros de seleção
  Views/                 janelas WPF
  Resources/             ícones
bundle/                  manifesto .addin
build.ps1                compilação multi-versão + bundle
```

## Como contribuir
Contribuições são muito bem-vindas — de programadores e também de projetistas que encontrem erros de cálculo ou
queiram sugerir funções. Leia o [CONTRIBUTING.md](CONTRIBUTING.md) e veja as [issues abertas](../../issues).

Funções planejadas: legendas, desvio e ajuste de rota,
diagrama multifilar com setorização por DR, exportação Excel (.xlsx), cotas automáticas, cálculo com alumínio e
cos φ / reatância.

## Licença
[GNU GPL v3.0](LICENSE) © Rafael Matias e contribuidores.
Você pode usar, estudar, modificar e redistribuir; versões modificadas distribuídas devem manter a mesma licença
e publicar o código-fonte.

Autodesk e Revit são marcas registradas da Autodesk, Inc. Este projeto é independente e não é afiliado à Autodesk
nem a qualquer fabricante de software de projeto elétrico.

### Roteamento e nomes dos circuitos na versão 0.3.13

Atualizar Quadros considera a passagem entre eletrodutos conectados à mesma caixa, prioriza ligações físicas das famílias e de seus módulos e usa as saídas reais do quadro. Quadros não são tratados como passagem entre redes. Ajustar Rota usa a mesma regra. Avisos e identificadores exibem, por exemplo, **QD · Circuito 1 — Tomadas** e **MED · Circuito 1 — Alimentação do QD**, preservando nomes, números e vínculos nativos do Revit. O ensaio em Projeto1 alcançou os três circuitos e gerou quatro identificadores; depois foi integralmente desfeito.

### Passagem de fiação na versão 0.3.14

Criar Identificadores de Fiação desenha símbolos próprios de fase, neutro, terra e retorno, com o número do circuito acima e as seções abaixo. Configurações → Documentação permite usar os números de cada quadro ou uma sequência única guardada no projeto, aplicada somente aos identificadores da planta. A numeração dos circuitos e das tabelas do Revit permanece igual. Os números únicos existentes são mantidos quando novos circuitos são criados; números de circuitos removidos não são reutilizados.

Execute Atualizar Quadros, Dimensionar Instalação e aplique os resultados antes de gerar os símbolos. Retornos dependem da associação de interruptores e luminárias em CL_Retorno. As quantidades de fase, neutro e terra vêm da fiação dimensionada do circuito; esta versão não faz um cálculo independente de condutores por terminal de interruptor. Se a fiação estiver ausente ou em formato desconhecido, o trecho usa o identificador textual. A omissão de seções, o agrupamento do terra e a substituição por legenda continuam respeitando as configurações existentes. Para mostrar a seção padrão de 2,5 mm², desative sua omissão nas configurações.

A referência foi o comportamento observado em um plugin de projeto elétrico existente: quantidades separadas de condutores, retorno associado e campos próprios para número e seção. O Circuito Livre usa implementação e símbolos próprios, sem distribuir código ou famílias de terceiros.

### Simbologia do template na versão 0.3.15

Os símbolos de fiação usam famílias de anotação do próprio projeto/template, reconhecidas pelos parâmetros N_Fase, N_Neutro, N_Terra, N_Retorno, N_Circuito, Bit_Fase e Bit_Terra. O plugin prioriza famílias compatíveis já utilizadas no projeto e verifica se conseguem representar zero retornos. Na ausência de uma compatível, carrega CL_SMB_Fiação, uma família de simbologia de fiação fornecida pelo usuário, com a mesma simbologia. A cópia corrige a distância do array de retornos para usar N_Retorno_Array: na original, informar zero retornos fazia o Revit alterar a quantidade para um. A família original e as anotações manuais permanecem preservadas.

Os identificadores de pontos priorizam as escolhas manuais, depois as tags usadas na vista ou o padrão do projeto. O diálogo de eletrodutos e eletrocalhas usa essa mesma preferência, sem escolher arbitrariamente a primeira família da lista. As fontes e os símbolos vêm dos tipos carregados no projeto, herdados do template; não há importação universal de qualquer parâmetro ou de arquivos RTE externos. Famílias de fiação com outros nomes de parâmetros exigem adaptação. Quantidades fora do limite gráfico usam texto; fontes, cores, espessuras e tamanho dos símbolos são definidos na família. O posicionamento considera anotações existentes, e a regeneração remove somente elementos rastreados pelo plugin.
