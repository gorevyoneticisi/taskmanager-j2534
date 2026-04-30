@echo off
:: Copyright (c) 2017  Denis Kuzmin <x-3F@outlook.com> github/3F
:: Tests. Part of https://github.com/3F/hMSBuild

setlocal enableDelayedExpansion

call a isNotEmptyOrWhitespaceOrFail %~1 || exit /B1

set /a gcount=!%~1! & set /a failedTotal=!%~2!
set "exec=%~3" & set "wdir=%~4"

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::
:: Tests

    :: NOTE: :startTest will use ` as "
    :: It helps to use double quotes inside double quotes " ... `args` ... "

:::::::::::::::::
    call :cleanup

    ::_______ ------ ______________________________________

        call a startTest "-help" || goto x
            call a msgOrFailAt 0 "" || goto x

            if not defined appversionHms call a failTest "Empty *appversionHms" & goto x
            if not "%appversionHms%"=="off" (
                call a msgOrFailAt 1 "hMSBuild %appversionHms%" || goto x
            )
            call a msgOrFailAt 2 "github/3F" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-version" || goto x
            if not defined appversionHms call a failTest "Empty *appversionHms" & goto x
            if not "%appversionHms%"=="off" (
                call a msgOrFailAt 1 "%appversionHms%" || goto x
            )
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-?" || goto x
            if not "%appversionHms%"=="off" (
                call a msgOrFailAt 1 "hMSBuild %appversionHms%" || goto x
            )
            call a msgOrFailAt 2 "github/3F" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug ~c RCI" 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            call a msgOrFailAt !n! "/p:Configuration" || goto x
            call a msgOrFailAt !n! "`RCI`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug ~p x86" 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            call a msgOrFailAt !n! "/p:Platform" || goto x
            call a msgOrFailAt !n! "`x86`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug ~c `Public Release` ~p `Any CPU`" 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            call a msgOrFailAt !n! "/p:Configuration" || goto x
            call a msgOrFailAt !n! "`Public Release` /p:Platform" || goto x
            call a msgOrFailAt !n! "`Any CPU`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug ~x ~c name" 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            set /a maxcpu=NUMBER_OF_PROCESSORS - 1
            call a msgOrFailAt !n! "/v:m /m:!maxcpu!" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug /p:Platform=`Any CPU` " 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            call a msgOrFailAt !n! "/p:Platform" || goto x
            call a msgOrFailAt !n! "`Any CPU`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug -only-path /p:Platform=`Any CPU` " || goto x
            call a msgOrFailAt 1 "non-handled key:  /p:Platform" || goto x
            call a msgOrFailAt 2 "non-handled key: " || goto x
            call a msgOrFailAt 3 "non-handled key:  `Any CPU`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug /p:Configuration=`Public Release` " 1 || goto x
            call a findInStreamOrFail "Arguments:" 4,n || goto x
            call a msgOrFailAt !n! "/p:Configuration" || goto x
            call a msgOrFailAt !n! "`Public Release`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        call a startTest "-debug /p:Configuration=`Public Release` /p:Platform=`Any CPU`" 1 || goto x
            call a msgOrFailAt 1 "non-handled key:  /p:Configuration" || goto x
            call a msgOrFailAt 2 "non-handled key: " || goto x
            call a msgOrFailAt 3 "non-handled key:  `Public Release`" || goto x
            call a msgOrFailAt 4 "non-handled key:  /p:Platform" || goto x
            call a msgOrFailAt 5 "non-handled key: " || goto x
            call a msgOrFailAt 6 "non-handled key:  `Any CPU`" || goto x
            call a findInStreamOrFail "Arguments:" 7,n || goto x
            call a msgOrFailAt !n! "/p:Configuration" || goto x
            call a msgOrFailAt !n! "`Public Release` /p:Platform" || goto x
            call a msgOrFailAt !n! "`Any CPU`" || goto x
        call a completeTest
    ::_____________________________________________________


    ::_______ ------ ______________________________________

        REM: calling built-in GetNuTool using keys with empty "", e.g. proxycfg="" etc.
        call a startTest "-debug -GetNuTool Conari /p:proxycfg=``" 0 || goto x
            call a findInStreamOrFail "Conari " 4,n || goto x
            call a msgOrFailAt !n! "packages\Conari" || goto x
        call a completeTest
    ::_____________________________________________________


:::::::::::::
call :cleanup

:::::::::::::::::: :::::::::::::: :::::::::::::::::::::::::
::
:x
endlocal & set /a %1=%gcount% & set /a %2=%failedTotal%
if !failedTotal! EQU 0 exit /B 0
exit /B 1

:cleanup

exit /B 0