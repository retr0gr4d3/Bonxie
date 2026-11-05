@echo off
setlocal enabledelayedexpansion

echo ==========================================================
echo               Bonxie Release Packager
echo ==========================================================
echo.

:: 1. Check for Velopack CLI (vpk)
where vpk >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [1/4] Installing Velopack CLI ^(vpk^)...
    dotnet tool install -g vpk
    if %ERRORLEVEL% NEQ 0 (
        echo [ERROR] Failed to install Velopack CLI.
        pause
        exit /b 1
    )
) else (
    echo [1/4] Velopack CLI ^(vpk^) is ready.
)

:: 2. Determine Version
set "VERSION=%~1"
if "%VERSION%"=="" (
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "[xml]$x = Get-Content '%~dp0Directory.Build.props'; $x.Project.PropertyGroup.Version | Select-Object -First 1"`) do (
        set "VERSION=%%v"
    )
)

if "%VERSION%"=="" set "VERSION=2.0.0"
echo [2/4] Packaging version: %VERSION%
echo.

:: 3. Build & Assemble Project Output
echo [3/4] Running BuildProject.bat...
call "%~dp0BuildProject.bat" --no-pause
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed. Aborting release package.
    pause
    exit /b %ERRORLEVEL%
)

:: 4. Clean old releases and run vpk pack
echo.
echo [4/4] Generating Release Assets with Velopack...
if exist "%~dp0Build\Releases" (
    rmdir /s /q "%~dp0Build\Releases" >nul 2>&1
)

vpk pack -u Bonxie -v %VERSION% -p "%~dp0Build" -e Bonxie.Manager.exe --icon "%~dp0Skua.Shared.Avalonia\Assets\SkuaIcon.ico" -o "%~dp0Build\Releases" -r win-x64
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Velopack packaging failed.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ==========================================================
echo           Bonxie Release Packaging Complete!
echo ==========================================================
echo.
echo Output location: "%~dp0Build\Releases"
echo.
echo Generated Assets:
echo   [1] Bonxie-win-Setup.exe      - 1-click installer for new users
echo   [2] Bonxie-win-Portable.zip   - Zero-install portable zip
echo   [3] Bonxie-%VERSION%-full.nupkg - Background auto-update payload
echo   [4] releases.win.json           - Update manifest for GitHub Releases
echo.
echo Next steps:
echo   1. Push your code to GitHub: git push origin latestreleasereadyimo:main
echo   2. Go to https://github.com/retr0gr4d3/Bonxie/releases/new
echo   3. Create the tag (e.g. b7) and upload EVERY file from Build\Releases\
echo      The updater needs releases.win.json and the -full.nupkg, not just the zip.
echo      Bump Version in Directory.Build.props for each release or no update is offered.
echo ==========================================================
echo.
pause
