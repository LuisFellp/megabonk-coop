@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Megabonk Coop - Desinstalar

echo ============================================================
echo   MEGABONK COOP - desinstalar (volta o jogo ao vanilla)
echo   Remove o BepInEx e TODOS os mods dele. Saves nao sao tocados.
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

if defined MEGABONK_DIR goto skiprun
tasklist /FI "IMAGENAME eq Megabonk.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "Megabonk.exe" >nul
if not errorlevel 1 (
  echo ERRO: o Megabonk esta aberto. Feche o jogo e rode de novo.
  pause
  exit /b 1
)
:skiprun

if not defined MEGABONK_DIR (
  choice /C SN /M "Remover o mod e o BepInEx de %GAME% "
  if errorlevel 2 (
    echo Cancelado.
    pause
    exit /b 0
  )
)

if exist "%GAME%\BepInEx" rd /s /q "%GAME%\BepInEx"
if exist "%GAME%\dotnet" rd /s /q "%GAME%\dotnet"
for %%F in (winhttp.dll doorstop_config.ini .doorstop_version changelog.txt) do (
  if exist "%GAME%\%%F" (
    attrib -h -s -r "%GAME%\%%F" >nul 2>&1
    del /f /q "%GAME%\%%F" >nul 2>&1
  )
)
rem LEIAME deixado pela v1 quando o zip era extraido direto na pasta do jogo
if exist "%GAME%\LEIAME.txt" (
  findstr /C:"MEGABONK COOP" "%GAME%\LEIAME.txt" >nul 2>&1 && del /f /q "%GAME%\LEIAME.txt" >nul 2>&1
)
echo.
echo PRONTO! O jogo esta vanilla de novo.
echo Se quiser ter certeza: Steam ^> Megabonk ^> Propriedades ^> Arquivos instalados ^> Verificar integridade.
pause
endlocal
