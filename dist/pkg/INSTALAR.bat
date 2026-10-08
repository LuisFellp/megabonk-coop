@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Megabonk Coop - Instalacao limpa

echo ============================================================
echo   MEGABONK COOP - instalacao limpa
echo   Remove qualquer BepInEx/mod antigo e instala a versao nova.
echo   Os arquivos ORIGINAIS do jogo nao sao tocados.
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
  echo.
  echo ERRO: nao achei Megabonk.exe em "%GAME%"
  pause
  exit /b 1
)
if not exist "%~dp0files\winhttp.dll" (
  echo.
  echo ERRO: a pasta "files" nao esta ao lado deste instalador.
  echo Extraia o zip inteiro antes de rodar.
  pause
  exit /b 1
)

if defined MEGABONK_DIR goto skiprun
tasklist /FI "IMAGENAME eq Megabonk.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "Megabonk.exe" >nul
if not errorlevel 1 (
  echo.
  echo ERRO: o Megabonk esta aberto. Feche o jogo e rode de novo.
  pause
  exit /b 1
)
:skiprun

echo Jogo encontrado em: %GAME%
echo.

rem ---------- 1) backup do que existia (so plugins e config, pequeno)
set "STAMP=agora"
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%i"
set "BK=%~dp0backup_antigo_%STAMP%"
if exist "%GAME%\BepInEx\plugins" (
  robocopy "%GAME%\BepInEx\plugins" "%BK%\plugins" /E /NFL /NDL /NJH /NJS /NP >nul
  echo [1/4] Backup dos plugins antigos em: %BK%\plugins
)
if exist "%GAME%\BepInEx\config" (
  robocopy "%GAME%\BepInEx\config" "%BK%\config" /E /NFL /NDL /NJH /NJS /NP >nul
  echo [1/4] Backup das configs antigas em:  %BK%\config
)
if not exist "%GAME%\BepInEx" echo [1/4] Nada para fazer backup (jogo estava limpo).

rem ---------- 2) volta o jogo ao estado vanilla (so remove o que o BepInEx cria)
echo [2/4] Removendo BepInEx antigo...
rem apaga so o que e do BepInEx/mods. MANTEM interop, cache e unity-libs: sao gerados do proprio jogo
rem (o BepInEx confere o hash e regenera se o jogo mudou) e evitam a 1a abertura demorada.
for %%D in (core plugins config patchers) do if exist "%GAME%\BepInEx\%%D" rd /s /q "%GAME%\BepInEx\%%D"
for %%F in (LogOutput.log ErrorLog.log) do if exist "%GAME%\BepInEx\%%F" del /f /q "%GAME%\BepInEx\%%F" >nul 2>&1
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
if exist "%GAME%\BepInEx\plugins" (
  echo ERRO: nao consegui apagar a pasta BepInEx\plugins. Feche o jogo/Steam e tente de novo.
  pause
  exit /b 1
)

rem ---------- 3) instala a versao nova
echo [3/4] Instalando versao nova...
xcopy "%~dp0files\*" "%GAME%\" /E /Y /I /Q >nul
if errorlevel 1 (
  echo ERRO ao copiar os arquivos.
  pause
  exit /b 1
)

rem ---------- 4) confere
set "OK=1"
if not exist "%GAME%\winhttp.dll" set "OK=0"
if not exist "%GAME%\doorstop_config.ini" set "OK=0"
if not exist "%GAME%\BepInEx\core\BepInEx.Unity.IL2CPP.dll" set "OK=0"
if not exist "%GAME%\BepInEx\plugins\MegabonkCoop\MegabonkCoop.dll" set "OK=0"
if "%OK%"=="0" (
  echo ERRO: a instalacao ficou incompleta. Rode de novo.
  pause
  exit /b 1
)
echo [4/4] Conferido: tudo no lugar.
echo.

rem ---------- IP opcional
set "IP="
set /p IP=Digite o IP do Radmin do HOST (ou so ENTER se VOCE e o host / prefere usar o painel):
if defined IP powershell -NoProfile -Command "$f='%GAME%\BepInEx\config\luis.megabonkcoop.cfg'; (Get-Content $f) -replace 'JoinIP = .*','JoinIP = %IP%' | Set-Content -Encoding UTF8 $f"

echo.
echo ============================================================
echo   PRONTO! Abra o Megabonk pelo Steam.
echo   Se for a primeira vez com o BepInEx, pode demorar 1-2 minutos carregando. Nao feche.
echo   No menu principal aparece o painel COOP embaixo no centro.
echo ============================================================
pause
endlocal
