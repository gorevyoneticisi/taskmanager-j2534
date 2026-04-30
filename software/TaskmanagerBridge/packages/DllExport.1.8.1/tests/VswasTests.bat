@echo off
:: Copyright (c) 2017  Denis Kuzmin <x-3F@outlook.com> github/3F
:: Tests. Part of https://github.com/3F/hMSBuild

setlocal enableDelayedExpansion
call a isNotEmptyOrWhitespaceOrFail %~1 || exit /B1

set /a gcount=!%~1! & set /a failedTotal=!%~2!
set "exec=%~3" & set "wdir=%~4"

set core="%wdir%%exec%"
set vswhelper="%wdir%vswhere.bat"
set vswlog="%wdir%vswas.log"

echo @echo %%*^>%vswlog%> %vswhelper%

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::
:: Tests

set "base=-nologo -prerelease -requires  Microsoft.Component.MSBuild"

call :Vrfy base "-products *" "-only-path"

call :Vrfy base "-products * -latest" "-only-path"

call :Vrfy base "-latest -products *" "-only-path"

call :Vrfy base "-version [15.0,16.0) -products * -latest" "-only-path"

call :Vrfy base "-products * -latest -requiresAny -version [15.0,16.0) -requires Microsoft.NetCore.Component.SDK" "-only-path"

call :Vrfy base "-products * -latest -requiresAny -version 16.0 -requires Microsoft.Component.MSBuild" "-only-path"

call :Vrfy base "-products * -latest -requiresAny -version [15.0,16.0) -requires Microsoft.Component.MSBuild Microsoft.NetCore.Component.SDK" "-only-path"

:::::::::::::
call :cleanup

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::

endlocal & set /a %1=%gcount% & set /a %2=%failedTotal%
if !failedTotal! EQU 0 exit /B 0
exit /B 1

:cleanup
    call a unsetFile %vswhelper%
    call a unsetFile %vswlog%
exit /B 0

:Vrfy
    :: (1) - Expected base line
    :: (2) - `vsw-as` keys
    :: [3] - left args before `vsw-as` keys
    :: [4] - right args before `vsw-as` keys
    set "__base=!%~1!" & set "vswas=%~2" & set "left=%~3" & set "right=%~4"

    call a startVFTest %core% "%left% -vsw-as `%vswas%` %right%" %vswlog% actual

        set "expected=%__base% %vswas%"

        echo "%expected%"
        echo. ==
        echo "%actual%"

        if not "%expected%"=="%actual%" (
            echo [Failed]: ~
            echo.

            call a failTest
            exit /B 1
        )

    call a completeTest
exit /B 0
