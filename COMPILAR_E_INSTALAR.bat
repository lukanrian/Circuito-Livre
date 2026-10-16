@echo off
chcp 65001 >nul
echo Compilando e instalando o RafaelElectrical...
echo (feche o Revit antes, para a DLL nao ficar bloqueada)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Install
echo.
pause
