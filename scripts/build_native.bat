@echo off
setlocal enabledelayedexpansion

:: Try locating vcvarsall.bat dynamically
set "VCVARS="
for %%e in (Community Professional Enterprise BuildTools) do (
    for %%v in (18 2022 17 2019) do (
        if not defined VCVARS if exist "%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvarsall.bat"
        if not defined VCVARS if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvarsall.bat"
    )
)

if defined VCVARS (
    echo Using Visual Studio Environment: !VCVARS!
    call "!VCVARS!" x64 >nul 2>&1
)

cd /d "%~dp0..\src\native\knox.engine"
if exist build\CMakeCache.txt (
    findstr /I "nota.engine" build\CMakeCache.txt >nul 2>&1
    if not errorlevel 1 (
        echo Cleaning stale build cache...
        rmdir /s /q build
    )
)
if not exist build mkdir build
cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1

cmake --build . --config Release
if errorlevel 1 exit /b 1

if exist Release\knox_engine.dll (
    copy /Y Release\knox_engine.dll .
    copy /Y Release\knox-scanworker.exe .
    if exist "..\..\..\managed\Knox.App\bin\Release\net10.0" copy /Y Release\knox_engine.dll "..\..\..\managed\Knox.App\bin\Release\net10.0"
    if exist "..\..\..\managed\Knox.App\bin\Release\net10.0" copy /Y Release\knox-scanworker.exe "..\..\..\managed\Knox.App\bin\Release\net10.0"
    if exist "..\..\..\managed\Knox.App\bin\Debug\net10.0" copy /Y Release\knox_engine.dll "..\..\..\managed\Knox.App\bin\Debug\net10.0"
    if exist "..\..\..\managed\Knox.App\bin\Debug\net10.0" copy /Y Release\knox-scanworker.exe "..\..\..\managed\Knox.App\bin\Debug\net10.0"
    if exist "..\..\..\..\tests\Knox.SmokeTest\bin\Release\net10.0" copy /Y Release\knox_engine.dll "..\..\..\..\tests\Knox.SmokeTest\bin\Release\net10.0"
    if exist "..\..\..\..\tests\Knox.SmokeTest\bin\Debug\net10.0" copy /Y Release\knox_engine.dll "..\..\..\..\tests\Knox.SmokeTest\bin\Debug\net10.0"
)
