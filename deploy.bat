@echo off
setlocal enabledelayedexpansion

REM Deploy script for Citadel deployment system
REM All configuration is read from .env in the same directory as this script.
REM Usage: deploy.bat [source-path]

REM Get script directory
set "SCRIPT_DIR=%~dp0"

REM Load .env
set "ENV_FILE=%SCRIPT_DIR%.env"
if not exist "!ENV_FILE!" (
    echo Error: .env file not found at !ENV_FILE!
    exit /b 1
)

REM Parse .env key=value pairs (skip comment lines and blank lines)
for /f "usebackq tokens=1,* delims==" %%a in ("!ENV_FILE!") do (
    set "_key=%%a"
    if not "!_key:~0,1!"=="#" if not "!_key!"=="" (
        set "%%a=%%b"
    )
)

REM Validate required variables
for %%v in (AUTH_TOKEN DEPLOY_URL PROFILE) do (
    if "!%%v!"=="" (
        echo Error: %%v not set in .env
        exit /b 1
    )
)

REM SOURCE: CLI arg overrides .env SOURCE; fallback to current directory
if not "%~1"=="" set "SOURCE=%~1"
if "!SOURCE!"=="" set "SOURCE=."

REM Validate source exists
if not exist "!SOURCE!" (
    echo Error: source path does not exist: !SOURCE!
    exit /b 1
)

REM Create temp zip filename
for /f %%a in ('powershell -Command "[System.IO.Path]::GetTempPath() + 'deploy_' + [System.IO.Path]::GetRandomFileName().Split('.')[0] + '.zip'"') do set "TEMP_ZIP=%%a"

REM Zip the source
if "!SOURCE:~-4!"==".zip" (
    echo Copying zip file: !SOURCE!
    copy "!SOURCE!" "!TEMP_ZIP!" >nul
) else (
    echo Zipping: !SOURCE!
    powershell -Command "Compress-Archive -Path '!SOURCE!\*' -DestinationPath '!TEMP_ZIP!' -Force"
)

if not exist "!TEMP_ZIP!" (
    echo Error: Failed to create zip file
    exit /b 1
)

REM Get zip size
for /f %%a in ('powershell -Command "'{0:N2}' -f ((Get-Item '!TEMP_ZIP!').length / 1MB) + ' MB'"') do set "ZIP_SIZE=%%a"
echo Created zip: !ZIP_SIZE!

REM Deploy - write body to temp file, capture HTTP status code
set "BODY_FILE=%TEMP%\citadel_body_%RANDOM%.txt"
echo Deploying to !DEPLOY_URL! (profile: !PROFILE!)
for /f %%a in ('curl -s -o "!BODY_FILE!" -w "%%{http_code}" -X POST ^
    -H "Authorization: Bearer !AUTH_TOKEN!" ^
    -H "X-Profile: !PROFILE!" ^
    -F "file=@!TEMP_ZIP!" ^
    "!DEPLOY_URL!"') do set "HTTP_CODE=%%a"

set /p BODY=<"!BODY_FILE!"
del /q "!BODY_FILE!" 2>nul

REM Clean up temp zip
if exist "!TEMP_ZIP!" del /q "!TEMP_ZIP!"

echo.
if "!HTTP_CODE!"=="200" (
    echo Deploy successful!
    echo Response: !BODY!
    exit /b 0
) else (
    echo Deploy failed with HTTP !HTTP_CODE!
    echo Response: !BODY!
    exit /b 1
)
