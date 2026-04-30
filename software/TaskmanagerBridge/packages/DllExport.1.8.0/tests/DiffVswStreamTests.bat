@echo off
:: Copyright (c) 2017  Denis Kuzmin <x-3F@outlook.com> github/3F
:: Tests. Part of https://github.com/3F/hMSBuild

setlocal enableDelayedExpansion
call a isNotEmptyOrWhitespaceOrFail %~1 || exit /B1

set /a gcount=!%~1! & set /a failedTotal=!%~2!
set "pA=%~3" & set "wdir=%~4" & set "pB=%~5"

set pA="%pA%" & set pB="%pB%"
set "emptyProj=%cd%\empty.proj"

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::
:: Tests

call :init %pA%


    call a abStreamTest "-only-path" %pA% %pB% || goto x
    call a abStreamTest "-only-path -no-vswhere" %pA% %pB% || goto x
    call a abStreamTest "-only-path -no-vs" %pA% %pB% || goto x
    call a abStreamTest "-only-path -no-netfx" %pA% %pB% || goto x
    call a abStreamTest "-only-path -notamd64" %pA% %pB% || goto x

    call a abStreamTest "/v:m" %pA% %pB% || goto x
    call a abStreamTest "-no-vswhere /v:m" %pA% %pB% || goto x
    call a abStreamTest "-no-vs /v:m" %pA% %pB% || goto x
    call a abStreamTest "-no-netfx /v:m" %pA% %pB% || goto x
    call a abStreamTest "-notamd64 /v:m" %pA% %pB% || goto x

    call a abStreamTest "-only-path -vswhere local" %pA% %pB% || goto x
    call a abStreamTest "-only-path -vswhere 2.8.4" %pA% %pB% || goto x
    call a abStreamTest "-only-path -vswhere latest" %pA% %pB% || goto x
    call a abStreamTest "-only-path -eng" %pA% %pB% || goto x
    call a abStreamTest "-GetNuTool -unpack" %pA% %pB% || goto x

    call a abStreamTest "-only-path -priority Microsoft.NetCore.Component.SDK" %pA% %pB% || goto x
    call a abStreamTest "-only-path -priority NoComponent.NoSDK0" %pA% %pB% || goto x
    call a abStreamTest "-priority Microsoft.NetCore.Component.SDK /v:m" %pA% %pB% || goto x
    call a abStreamTest "-priority NoComponent.NoSDK0 /v:m" %pA% %pB% || goto x

    :: TODO: fix -no-cache diff comparison
    @REM call a abStreamTest "-only-path -no-cache" %pA% %pB% || goto x
    @REM call a abStreamTest "-only-path -vswhere 2.8.4 -no-cache" %pA% %pB% || goto x
    @REM call a abStreamTest "-only-path -vswhere latest -no-cache" %pA% %pB% || goto x

    call a abStreamTest "-only-path -vswhere local -no-cache" %pA% %pB% || goto x
    call a abStreamTest "-no-vswhere -no-vs -only-path" %pA% %pB% || goto x
    call a abStreamTest "-no-vswhere -no-vs -notamd64 -only-path" %pA% %pB% || goto x
    call a abStreamTest "-no-netfx -notamd64 -only-path" %pA% %pB% || goto x
    call a abStreamTest "-no-vs -notamd64 -only-path" %pA% %pB% || goto x
    call a abStreamTest "-notamd64 -vswhere local -only-path" %pA% %pB% || goto x
    call a abStreamTest "-notamd64 -vswhere 2.8.4 -only-path" %pA% %pB% || goto x
    call a abStreamTest "-notamd64 -vswhere latest -only-path" %pA% %pB% || goto x
    
    :: exit code > 0
    call a abStreamTest "-no-vswhere -no-vs -no-netfx -only-path" %pA% %pB% 2 || goto x

    :: -1 to avoid checking an exit code due to different env.  /Y-59
    call a abStreamTest "-no-vswhere -no-netfx -only-path" %pA% %pB% -1 || goto x
    call a abStreamTest "-no-vswhere -no-netfx -notamd64 -only-path" %pA% %pB% -1 || goto x

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::
::
:x
call :cleanup

endlocal & set /a %1=%gcount% & set /a %2=%failedTotal%
if !failedTotal! EQU 0 exit /B 0
exit /B 1

:init
    :: to cache specified versions
    call %~1 -only-path -vswhere 2.8.4 >nul
    call %~1 -only-path -vswhere latest >nul

    ::create empty .proj to have a Build succeeded.
    echo ^<?xml version="1.0" encoding="utf-8"?^> > %emptyProj%
    echo ^<Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"^> >> %emptyProj%
    echo ^<Target Name="Build" /^>^</Project^> >> %emptyProj%
exit /B 0

:cleanup
    set "TIME="
    call a unsetFile %emptyProj%
    call a unsetFile gnt.core
exit /B 0
