# RafaelElectrical — plugin elétrico open source para Revit

Plugin gratuito e de código aberto para **projetos elétricos e de telecom no Autodesk Revit 2025, 2026 e 2027**,
com dimensionamento pela **ABNT NBR 5410**. Feito em C# (.NET 8 / .NET 10, WPF), sem dependências externas.

> ⚠️ **Aviso de responsabilidade técnica**
> Este software é uma ferramenta de apoio ao projeto. Os resultados (seções, disjuntores, quedas de tensão,
> fatores, ocupação de eletrodutos etc.) **devem ser conferidos por profissional legalmente habilitado**, que assina
> o projeto e emite a ART/RRT. As tabelas da NBR 5410 foram digitadas a partir da norma e precisam ser conferidas com
> a edição vigente. O software é distribuído **sem nenhuma garantia** (veja a [licença](LICENSE)).

> 🚧 **Versão 0.2.3 beta — em desenvolvimento.** Várias funções ainda não foram testadas em projeto real. Relate o que encontrar nas [issues](../../issues).

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

**Construir Conduítes** mantém o lançamento sequencial por cliques e oferece dois modos adicionais: selecionar vários componentes para ordená-los por proximidade a partir do primeiro quadro selecionado, ou selecionar um espaço MEP e o quadro para ligar seus pontos elétricos. Esses modos usam a mesma altura, tipo de infraestrutura e criação de conexões do comando original. A proximidade usa um percurso guloso; não calcula desvio global de obstáculos. O diâmetro do eletroduto continua a ser dimensionado pelo comando **Dimensionar Infraestrutura** depois que os circuitos e cabos estiverem roteados.

O plugin grava seus resultados em parâmetros compartilhados próprios, com prefixo `RE_`
(ex.: `RE_Seção (mm²)`, `RE_Disjuntor (A)`, `RE_Queda total (%)`, `RE_Fiação`).

### Fluxo sugerido
Configurações → Copiar Níveis → Lançar Pontos → Criar Circuitos → Ordenar Circuitos → Construir Infraestrutura →
Atualizar Quadros → Dimensionar Instalação → Dimensionar Infraestrutura → Atualizar Quadros →
Preencher Identificação → Identificadores → Unifilar / Tabelas / Relatórios / Memorial → Compor Pranchas.

## Instalar (versão compilada)
1. Baixe o `RafaelElectrical.bundle.zip` em **[Releases](../../releases)**.
2. Extraia todo o ZIP e execute `Instalar.ps1` no PowerShell, com o Revit fechado. Também é possível copiar a pasta `RafaelElectrical.bundle` para `%APPDATA%\Autodesk\ApplicationPlugins`.
3. Abra o Revit: aparece a aba **RafaelElectrical**.

Para desinstalar, apague essa pasta.

## Compilar a partir do código
Pré-requisitos (Windows):
- Para **compilar** o código: [.NET 10 SDK](https://dotnet.microsoft.com/download) e acesso ao NuGet para restaurar as referências de compilação das APIs 2025–2027. Não é necessário ter o Revit instalado para compilar, e nenhuma DLL da API vai para o pacote. Para **instalar o ZIP pronto**, não é necessário instalar o SDK.

Passos:
1. Feche o Revit.
2. Rode `COMPILAR_E_INSTALAR.bat` para compilar as três versões e instalar o bundle, ou
   `powershell -ExecutionPolicy Bypass -File build.ps1` para só gerar `dist\RafaelElectrical.bundle` e o `.zip`. O build falha se qualquer uma das três versões não puder ser compilada.

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
