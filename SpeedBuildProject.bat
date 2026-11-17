@echo off
setlocal EnableExtensions

rem ==========================================================
rem LOCAL FAST BUILD
rem - Does not pull, switch branches, or modify Git state.
rem - Builds the five application/plugin projects in parallel.
rem - Uses the ignored Build folder for its temporary solution filter.
rem ==========================================================

set "BUILDPROJECT_ROOT=%~dp0"
if "%~1"=="" set "BUILDPROJECT_PAUSE=1"
set "BUILD_START_TIME=%TIME%"
set "BUILD_EXIT_CODE=1"
set "BUILDPROJECT_PUSHED=0"
set "BUILD_FILTER="
set "BUILD_PROGRESS=0"
set "BUILD_WORKERS=16"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem Get ANSI escape character for the Pac-Man progress bar.
for /f %%E in ('echo prompt $E ^| cmd') do set "ESC=%%E"

pushd "%BUILDPROJECT_ROOT%" || goto :Failure
set "BUILDPROJECT_PUSHED=1"

call :Progress 0 "Initializing local build"

echo.
echo ==========================================================
echo                    BONXIE LOCAL BUILD
echo ==========================================================
echo  CPU:       Intel Core i9-11900K
echo  Workers:   %BUILD_WORKERS% MSBuild workers
echo  Memory:    128 GB DDR4
echo  Mode:      Release / Parallel / Incremental
echo  Git:       No pull or checkout
echo ==========================================================
echo.

rem ----------------------------------------------------------
rem Validate repository and required tools
rem ----------------------------------------------------------

echo Checking repository and build tools...

git rev-parse --show-toplevel >nul 2>&1
if errorlevel 1 (
    echo This script must be run from inside the Bonxie Git repository.
    goto :Failure
)

if not exist "Skua.sln" (
    echo Skua.sln was not found. Make sure the solution is generated.
    goto :Failure
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK was not found in PATH.
    goto :Failure
)

for /f "delims=" %%B in ('git branch --show-current 2^>nul') do set "BUILDPROJECT_BRANCH=%%B"
if not defined BUILDPROJECT_BRANCH set "BUILDPROJECT_BRANCH=detached HEAD"
echo Current branch: %BUILDPROJECT_BRANCH%
echo Working tree changes will be built as-is.

call :Progress 8 "Repository and SDK validated"

rem ----------------------------------------------------------
rem Close running instances
rem ----------------------------------------------------------

echo.
echo Closing running instances...
for %%P in (Bonxie.exe Bonxie.Manager.exe Skua.exe Skua.Manager.exe) do taskkill /IM "%%P" /F >nul 2>&1

if not exist "Build" (
    mkdir "Build"
    if errorlevel 1 goto :Failure
)

call :Progress 15 "Running instances stopped"

rem ----------------------------------------------------------
rem Rebuild AS3 only when its tracked SWF is missing or changed
rem ----------------------------------------------------------

echo.
echo Checking AS3 client...
set "AS3_REBUILD=0"
set "AS3_TEMP_SWF=Skua.AS3\skua\bin\skua.build-%RANDOM%-%RANDOM%.tmp.swf"

if not exist "Skua.AS3\skua\bin\skua.swf" set "AS3_REBUILD=1"
git diff --quiet HEAD -- "Skua.AS3/skua/bin/skua.swf"
if errorlevel 1 set "AS3_REBUILD=1"

if "%AS3_REBUILD%"=="1" (
    echo AS3 changes detected. Compiling client...
    if exist "%AS3_TEMP_SWF%" del /F /Q "%AS3_TEMP_SWF%"

    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "Skua.AS3\compile-as3.ps1" -OutputPath "%AS3_TEMP_SWF%"
    if errorlevel 1 (
        if exist "%AS3_TEMP_SWF%" del /F /Q "%AS3_TEMP_SWF%"
        echo AS3 compilation failed. The existing skua.swf was preserved.
        goto :Failure
    )

    if not exist "%AS3_TEMP_SWF%" (
        echo AS3 compilation completed without producing a SWF.
        goto :Failure
    )

    move /Y "%AS3_TEMP_SWF%" "Skua.AS3\skua\bin\skua.swf" >nul
    if errorlevel 1 (
        echo Failed to replace skua.swf with the compiled output.
        goto :Failure
    )
) else (
    echo AS3 client unchanged. Skipping compilation.
)

call :Progress 23 "AS3 client ready"

rem ----------------------------------------------------------
rem Generate solution filter inside ignored Build folder
rem ----------------------------------------------------------

echo.
echo Preparing parallel project build...
set "BUILD_FILTER=Build\LocalBuild-%RANDOM%-%RANDOM%.slnf"

> "%BUILD_FILTER%" echo {
>> "%BUILD_FILTER%" echo   "solution": {
>> "%BUILD_FILTER%" echo     "path": "../Skua.sln",
>> "%BUILD_FILTER%" echo     "projects": [
>> "%BUILD_FILTER%" echo       "Skua.App.Avalonia/Skua.App.Avalonia.csproj",
>> "%BUILD_FILTER%" echo       "Skua.Manager.Avalonia/Skua.Manager.Avalonia.csproj",
>> "%BUILD_FILTER%" echo       "Skua.Plugin.DailyTracker/Skua.Plugin.DailyTracker.csproj",
>> "%BUILD_FILTER%" echo       "Skua.Plugin.ButlerV5/Skua.Plugin.ButlerV5.csproj",
>> "%BUILD_FILTER%" echo       "Skua.Plugin.Cosmetics/Skua.Plugin.Cosmetics.csproj"
>> "%BUILD_FILTER%" echo     ]
>> "%BUILD_FILTER%" echo   }
>> "%BUILD_FILTER%" echo }

if not exist "%BUILD_FILTER%" (
    echo Failed to generate the temporary solution filter.
    goto :Failure
)

call :Progress 28 "Build targets prepared"

rem ----------------------------------------------------------
rem Restore only selected projects and their dependencies
rem ----------------------------------------------------------

echo.
echo Restoring NuGet dependencies for selected projects...
dotnet restore "%BUILD_FILTER%" --verbosity quiet --nologo
if errorlevel 1 goto :Failure

call :Progress 38 "NuGet restore complete"

rem ----------------------------------------------------------
rem Parallel Release build
rem ----------------------------------------------------------

echo.
echo ==========================================================
echo                 COMPILING BONXIE PROJECTS
echo ==========================================================
echo.
echo Building five projects with %BUILD_WORKERS% MSBuild workers.
echo Project references and shared dependencies are handled by MSBuild.
echo Incremental compilation and shared compiler are enabled.
echo.

call :Progress 42 "Compiling projects in parallel"

rem The current latest Mugetsu release moved the AVX fallback asset to
rem tag v1.6.0-skua.2 while retaining the skua.1 asset filename.
rem Use latest/download for that optional asset without editing the csproj.
dotnet build "%BUILD_FILTER%" -c Release --no-restore --verbosity minimal --nologo -m:%BUILD_WORKERS% -p:BuildInParallel=true -p:UseSharedCompilation=true
if errorlevel 1 goto :Failure

call :Progress 82 "All selected projects compiled"

rem Remove the temporary solution filter before packaging.
if exist "%BUILD_FILTER%" del /F /Q "%BUILD_FILTER%" >nul 2>&1
set "BUILD_FILTER="

rem ----------------------------------------------------------
rem Package output
rem ----------------------------------------------------------

echo.
echo Packaging and organizing output...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "package-build.ps1"
if errorlevel 1 goto :Failure

if not exist "Build\Bonxie.exe" (
    echo Packaging completed without producing Build\Bonxie.exe.
    goto :Failure
)

call :Progress 95 "Packaging complete"

rem ----------------------------------------------------------
rem Launch application
rem ----------------------------------------------------------

echo.
echo Launching Bonxie...
start "" "Build\Bonxie.exe"
if errorlevel 1 goto :Failure

set "BUILD_EXIT_CODE=0"
call :Progress 98 "Bonxie launched"
goto :Finish

rem ----------------------------------------------------------
rem Pac-Man progress bar (pure batch + ANSI)
rem ----------------------------------------------------------

:Progress
set "BUILD_PROGRESS=%~1"
setlocal EnableDelayedExpansion
set "PB_WIDTH=40"
set "PB_POS=0"
set /a "PB_POS=PB_WIDTH*%~1/100"
if !PB_POS! lss 1 set "PB_POS=1"
if %~1 geq 100 set /a "PB_POS=PB_WIDTH+1"

set "PB_BAR="
for /l %%i in (1,1,%PB_WIDTH%) do (
    set /a "PB_ODD=%%i & 1"
    if %%i lss !PB_POS! (
        set "PB_BAR=!PB_BAR!-"
    ) else if %%i equ !PB_POS! (
        if !PB_ODD!==1 (set "PB_BAR=!PB_BAR!C") else (set "PB_BAR=!PB_BAR!c")
    ) else (
        if !PB_ODD!==1 (set "PB_BAR=!PB_BAR!o") else (set "PB_BAR=!PB_BAR! " )
    )
)

set "PB_PCT=   %~1"
set "PB_PCT=!PB_PCT:~-3!"
echo !ESC![36m[!ESC![33m!PB_BAR!!ESC![36m]!ESC![0m !PB_PCT!%%  !ESC![97m%~2!ESC![0m
endlocal
exit /b 0

rem ----------------------------------------------------------
rem Convert a Windows %TIME% value to centiseconds.
rem Uses fixed character positions to avoid FOR /F delimiter issues.
rem ----------------------------------------------------------

:ToCs
set "TC_TIME=%~1"
set "TC_HOUR=%TC_TIME:~0,2%"
set "TC_MINUTE=%TC_TIME:~3,2%"
set "TC_SECOND=%TC_TIME:~6,2%"
set "TC_CENTISECOND=%TC_TIME:~9,2%"
set "TC_HOUR=%TC_HOUR: =0%"
set /a "%~2=((1%TC_HOUR%-100)*360000)+((1%TC_MINUTE%-100)*6000)+((1%TC_SECOND%-100)*100)+(1%TC_CENTISECOND%-100)"
exit /b 0

rem ----------------------------------------------------------
rem Failure handling
rem ----------------------------------------------------------

:Failure
set "BUILD_EXIT_CODE=1"
echo.
echo ==========================================================
echo                       BUILD FAILED
echo ==========================================================
echo Check the output above for details.

rem ----------------------------------------------------------
rem Final summary and cleanup
rem ----------------------------------------------------------

:Finish
if defined BUILD_FILTER if exist "%BUILD_FILTER%" del /F /Q "%BUILD_FILTER%" >nul 2>&1
if defined AS3_TEMP_SWF if exist "%AS3_TEMP_SWF%" del /F /Q "%AS3_TEMP_SWF%" >nul 2>&1

if "%BUILD_EXIT_CODE%"=="0" (
    call :Progress 100 "Build completed successfully"
) else (
    call :Progress %BUILD_PROGRESS% "Build failed"
)

call :ToCs "%BUILD_START_TIME%" START_CS
call :ToCs "%TIME%" END_CS
set /a "ELAPSED_CS=END_CS-START_CS"
if %ELAPSED_CS% lss 0 set /a "ELAPSED_CS+=8640000"

set /a "BUILD_TOTAL_SECONDS=ELAPSED_CS/100"
set /a "BUILD_HOURS=BUILD_TOTAL_SECONDS/3600"
set /a "BUILD_MINUTES=(BUILD_TOTAL_SECONDS/60)%%60"
set /a "BUILD_SECONDS=BUILD_TOTAL_SECONDS%%60"
set /a "BUILD_CENTISECONDS=ELAPSED_CS%%100"

echo.
echo ==========================================================
echo                     BUILD SUMMARY
echo ==========================================================
echo.
echo Total build time: %BUILD_HOURS%h %BUILD_MINUTES%m %BUILD_SECONDS%.%BUILD_CENTISECONDS%s
echo.
if "%BUILD_EXIT_CODE%"=="0" (
    echo Status: SUCCESS
    echo Output: Build\
) else (
    echo Status: FAILED
)
echo.
echo ==========================================================

if defined BUILDPROJECT_PAUSE pause
if "%BUILDPROJECT_PUSHED%"=="1" popd
exit /b %BUILD_EXIT_CODE%
