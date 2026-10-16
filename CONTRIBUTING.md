# Como contribuir

Obrigado por ajudar a melhorar o RafaelElectrical!

## Relatar erro ou sugerir função
Abra uma [issue](../../issues/new/choose) usando um dos modelos. Para erros, informe:
- versão do Revit e do plugin;
- o comando usado e o passo a passo;
- o que aconteceu e o que era esperado (para cálculos, os dados do circuito e o resultado correto com a referência da NBR 5410);
- se possível, um projeto de teste pequeno (sem dados de clientes).

## Enviar código
1. Faça um **fork** e crie um branch a partir de `main` (`feature/balanceamento-fases`, `fix/queda-tensao`...).
2. Compile com `build.ps1` e teste no Revit em um **projeto de teste**.
3. Abra um **pull request** explicando o que mudou e como testou.

### Padrões
- C# com nomes de classes/métodos em inglês e textos de interface, comentários e mensagens em **português**.
- Comandos em `src/Commands`, regras de cálculo em `src/Services` (sem dependência da interface), janelas em `src/Views`.
- Sem dependências externas (NuGet) sem discutir antes numa issue — o plugin precisa continuar fácil de compilar.
- Parâmetros novos: definir em `SharedParameters.cs` com prefixo `RE_` e GUID novo.
- Não use código, ícones, famílias, textos ou tabelas copiados de softwares proprietários. Normas: cite a tabela/item da NBR.
- Mudanças em cálculo devem indicar a referência normativa no comentário e no PR.

### Revisão
O mantenedor revisa todo PR antes do merge. Alterações em dimensionamento só entram depois de conferidas.

Ao contribuir, você concorda que sua contribuição seja distribuída sob a licença [GPL-3.0](LICENSE).
