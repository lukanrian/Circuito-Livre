# RafaelElectrical — plugin elétrico open source para Revit

Plugin gratuito e de código aberto para **projetos elétricos e de telecom no Autodesk Revit 2025, 2026 e 2027**,
com dimensionamento pela **ABNT NBR 5410**. Feito em C# (.NET 8 / .NET 10, WPF), sem dependências externas.

> ⚠️ **Aviso de responsabilidade técnica**
> Este software é uma ferramenta de apoio ao projeto. Os resultados (seções, disjuntores, quedas de tensão,
> fatores, ocupação de eletrodutos etc.) **devem ser conferidos por profissional legalmente habilitado**, que assina
> o projeto e emite a ART/RRT. As tabelas da NBR 5410 foram digitadas a partir da norma e precisam ser conferidas com
> a edição vigente. O software é distribuído **sem nenhuma garantia** (veja a [licença](LICENSE)).

> 🚧 **Em desenvolvimento.** Várias funções ainda não foram testadas em projeto real. Relate o que encontrar nas [issues](../../issues).

## Funções

| Painel | Comandos |
|---|---|
| **Configuração** | Configurações: temperatura, seções mínima/máxima, cores dos cabos, fatores de agrupamento (Tabela 42) por tipo de infraestrutura, taxas de ocupação, método do disjuntor, queda de tensão total 5%/7%, neutro reduzido, cabos de telecom, DPS no unifilar, exportar/importar `.json` |
| **Coordenação** | Copiar Níveis (de vínculo RVT/IFC ou de arquivo `.ifc`); Vínculos: nomear, copiar entre pavimentos, configurar vistas vinculadas |
| **Lançamento** | Lançar Pontos (luminárias, tomadas, interruptores, telecom) com altura e potência; Copiar / Copiar para Níveis / Rotacionar / Espelhar levando os circuitos |
| **Circuitos** | Criar Circuitos (força e telecom), Renumerar (várias ordens), Balancear Fases (A/B/C, com prévia antes/depois), Retornos de interruptores (letras a, b, c… em `RE_Retorno`), Preencher Identificação, Relatório de Cargas |
| **Dimensionamento** | Dimensionar Instalação (NBR 5410: Ib ≤ In ≤ Iz, FCT, FCA, seção mínima, queda de tensão parcial e total), Centro de Cargas, Desconsiderar Agrupamento |
| **Infraestrutura** | Construir eletrodutos/eletrocalhas, Atualizar Quadros (roteamento pela infraestrutura: comprimento e agrupamento reais), Dimensionar Infraestrutura (taxa de ocupação + vista 3D colorida), Adicionar Suportes |
| **Documentação** | Criar Identificadores, Diagrama Unifilar, Relatório de Fiação (CSV), Tabelas de Fiação, Memorial Descritivo e de Cálculo (`.docx`), Compor Pranchas |
| **Ferramentas** | Inspecionar Parâmetros |

O plugin grava seus resultados em parâmetros compartilhados próprios, com prefixo `RE_`
(ex.: `RE_Seção (mm²)`, `RE_Disjuntor (A)`, `RE_Queda total (%)`, `RE_Fiação`).

### Fluxo sugerido
Configurações → Copiar Níveis → Lançar Pontos → Criar Circuitos → Renumerar → Construir Infraestrutura →
Atualizar Quadros → Dimensionar Instalação → Dimensionar Infraestrutura → Atualizar Quadros →
Preencher Identificação → Identificadores → Unifilar / Tabelas / Relatórios / Memorial → Compor Pranchas.

## Instalar (versão compilada)
1. Baixe o `RafaelElectrical.bundle.zip` em **[Releases](../../releases)**.
2. Extraia a pasta `RafaelElectrical.bundle` em `%APPDATA%\Autodesk\ApplicationPlugins`.
3. Abra o Revit: aparece a aba **RafaelElectrical**.

Para desinstalar, apague essa pasta.

## Compilar a partir do código
Pré-requisitos (Windows):
- Revit 2025, 2026 e/ou 2027 instalado (as DLLs da API são lidas de `C:\Program Files\Autodesk\Revit 20XX` e **não** fazem parte deste repositório);
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (Revit 2025/2026) e .NET 10 SDK (Revit 2027).

Passos:
1. Feche o Revit.
2. Rode `COMPILAR_E_INSTALAR.bat` (compila só para as versões instaladas e instala o bundle), ou
   `powershell -ExecutionPolicy Bypass -File build.ps1` para só gerar `dist\RafaelElectrical.bundle` e o `.zip`.

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
