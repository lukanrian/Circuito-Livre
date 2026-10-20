# Regressões do dimensionamento

Compile primeiro para Revit 2025 e execute:

```powershell
dotnet build src/RafaelElectrical.csproj -c Release -p:RevitVersion=2025
dotnet run --project tests/SizingRegression -c Release
```

Sem dependências NuGet novas. Exige SDK .NET 8 e APIs do Revit 2025 instaladas.
Última execução: 53 verificações aprovadas em 19/10/2026.
Cobre validação, domínio térmico, queda acumulada, seleção parcial, ciclos,
alimentação ambígua e estimativa resistiva de curto com tensão variável.
A gravação de parâmetros e a interface precisam de validação dentro do Revit.

As tabelas existentes de Nbr5410 foram mantidas. As correções rejeitam dados fora
do domínio e preservam precisão nas comparações; não acrescentam tabelas normativas.
