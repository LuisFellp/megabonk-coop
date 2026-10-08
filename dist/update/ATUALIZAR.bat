@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Megabonk Coop - Atualizar v0.8.2

echo ============================================================
echo   MEGABONK COOP - atualizacao rapida (v0.8.2)
echo   So troca o arquivo do mod. Nao mexe em mais nada.
echo ============================================================
echo.

set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
if defined MEGABONK_DIR set "GAME=%MEGABONK_DIR%"
if exist "%GAME%\Megabonk.exe" goto found
echo Nao achei o jogo no caminho padrao.
echo No Steam: clique direito em Megabonk ^> Gerenciar ^> Explorar arquivos locais,
echo copie o caminho da barra de endereco e cole aqui.
set /p GAME=Caminho:
:found
set "GAME=%GAME:"=%"
if not exist "%GAME%\Megabonk.exe" (
  echo ERRO: nao achei Megabonk.exe em "%GAME%"
  pause
  exit /b 1
)
if not exist "%GAME%\BepInEx\plugins\MegabonkCoop" (
  echo ERRO: o mod ainda nao esta instalado. Use o INSTALAR.bat do pacote completo primeiro.
  pause
  exit /b 1
)
if defined MEGABONK_DIR goto skiprun
tasklist /FI "IMAGENAME eq Megabonk.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "Megabonk.exe" >nul
if not errorlevel 1 (
  echo ERRO: o Megabonk esta aberto. Feche o jogo e rode de novo.
  pause
  exit /b 1
)
:skiprun
copy /Y "%~dp0MegabonkCoop.dll" "%GAME%\BepInEx\plugins\MegabonkCoop\MegabonkCoop.dll" >nul
if errorlevel 1 (
  echo ERRO ao copiar. Feche o jogo e tente de novo.
  pause
  exit /b 1
)
echo PRONTO! Mod atualizado para a v0.8.2. Abra o jogo pelo Steam.
pause
endlocal
