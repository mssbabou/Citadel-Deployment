@echo off
chcp 65001 >nul
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

REM CLI arg overrides SOURCE from .env
if not "%~1"=="" set "SOURCE=%~1"

REM Validate required variables
for %%v in (AUTH_TOKEN DEPLOY_URL PROFILE SOURCE) do (
    if "!%%v!"=="" (
        echo Error: %%v not set in .env
        exit /b 1
    )
)

REM Strip trailing backslash if present
if "!SOURCE:~-1!"=="\" set "SOURCE=!SOURCE:~0,-1!"

REM Validate source exists
if not exist "!SOURCE!" (
    echo Error: source path does not exist: !SOURCE!
    exit /b 1
)

REM TEMP_DIR: use .env value if set, otherwise use system %TEMP%
if "!TEMP_DIR!"=="" set "TEMP_DIR=%TEMP%"

REM Create temp zip filename
for /f %%a in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString(\"N\").Substring(0,8)"') do set "_rand=%%a"
set "TEMP_ZIP=!TEMP_DIR!\deploy_!_rand!.zip"

REM Zip the source
if "!SOURCE:~-4!"==".zip" (
    echo 📦 Copying zip file: !SOURCE!
    copy "!SOURCE!" "!TEMP_ZIP!" >nul
) else (
    if exist "!SOURCE!\" (
        echo 📦 Zipping directory: !SOURCE!
    ) else (
        echo 📦 Zipping file: !SOURCE!
    )
    powershell -NoProfile -Command "Compress-Archive -Path '!SOURCE!' -DestinationPath '!TEMP_ZIP!' -Force"
)

if not exist "!TEMP_ZIP!" (
    echo Error: Failed to create zip file
    exit /b 1
)

REM Get zip size
for /f %%a in ('powershell -NoProfile -Command "'{0:N2} MB' -f ((Get-Item '!TEMP_ZIP!').Length / 1MB)"') do set "ZIP_SIZE=%%a"
echo ✓ Created zip: !ZIP_SIZE!

REM Deploy — write streaming response to temp file, then display
set "RESP_FILE=%TEMP%\citadel_resp_%RANDOM%.txt"
echo 🚀 Deploying to !DEPLOY_URL! (profile: !PROFILE!)
curl -s --no-buffer -X POST ^
    -H "Authorization: Bearer !AUTH_TOKEN!" ^
    -H "X-Profile: !PROFILE!" ^
    -F "file=@!TEMP_ZIP!" ^
    "!DEPLOY_URL!" > "!RESP_FILE!"

type "!RESP_FILE!"
echo.

REM Clean up temp zip
if exist "!TEMP_ZIP!" del /q "!TEMP_ZIP!"

REM Check last line of response for OK
set "LAST_LINE="
for /f "tokens=*" %%l in ("!RESP_FILE!") do set "LAST_LINE=%%l"
del /q "!RESP_FILE!" 2>nul

if "!LAST_LINE!"=="OK" (
    echo ✓ Done
    exit /b 0
) else (
    echo ✗ Deploy failed
    exit /b 1
)
