@echo off
:: Copyright (c) 2017  Denis Kuzmin <x-3F@outlook.com> github/3F
:: Part of https://github.com/3F/hMSBuild

if "%~1"=="" echo Empty function name & exit /B 1
if exist GetNuTool\a.bat ( set "base0=GetNuTool\a" ) else if exist ..\GetNuTool\a.bat ( set "base0=..\GetNuTool\a" ) else if exist ..\GetNuTool\tests\a.bat ( set "base0=..\GetNuTool\tests\a" ) else (
    echo GetNuTool's functions are not found & exit /B 1
)
if not defined G_LevelChild set /a G_LevelChild=1
    if "%~1"=="tryThisOrBase" ( call %base0% shiftArgs 4,99 shArgs %* ) else set shArgs=%*

    :: TODO: (performance) reduce the number of interruptions
    call :!shArgs! 2>%~nx0.err
    call %base0% tryThisOrBase !ERRORLEVEL! %~nx0.err !shArgs!
exit /B !ERRORLEVEL!

:: = = = = = = = = = = = = = = =

:rsrvHMS1
exit /B 0

:rsrvHMS2
exit /B 1
