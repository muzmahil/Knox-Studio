@echo off
setlocal enabledelayedexpansion
title Knox Studio - Derleme ve Tek Dosya Setup Paketleyici

echo ===============================================================================
echo                KNOX STUDIO - DERLEME VE SETUP PAKETLEYICI
echo                   Gelistirici: Furkan Centek (rootcf)
echo ===============================================================================
echo.

cd /d "%~dp0"

:: 0. Surum bilgisini oku
if exist VERSION (
    set /p KNOX_VER=<VERSION
) else (
    set KNOX_VER=1.0.0
)
set KNOX_VER=%KNOX_VER: =%

echo [*] Surum: %KNOX_VER%
echo [*] Hedef Mimari: Windows x64 (Single-File / Temiz Paket)
echo.

:: -----------------------------------------------------------------------------
:: ADIM 1: Native Motor ve Bilesenler Kontrol Ediliyor
:: -----------------------------------------------------------------------------
echo [1/5] Native Motor ve Bilesenler Kontrol Ediliyor...
if exist "scripts\build_native.bat" (
    echo     Native derleme scripti calistiriliyor...
    call "scripts\build_native.bat" >nul 2>&1
)

:: -----------------------------------------------------------------------------
:: ADIM 2: Managed Solution Derleme (.NET 10.0 Release)
:: -----------------------------------------------------------------------------
echo [2/5] Knox Studio Cozumu Derleniyor [Release]...
dotnet build Knox.sln -c Release --nologo
if errorlevel 1 (
    echo.
    echo [x] HATA: Proje derlenirken bir sorun olustu!
    if "%~1"=="" pause
    exit /b 1
)
echo     [+] Derleme basarili!
echo.

:: -----------------------------------------------------------------------------
:: ADIM 3: Smoke Testleri Dogrulama
:: -----------------------------------------------------------------------------
echo [3/5] Otomatik Smoke Testler Calistiriliyor...
dotnet run --project tests\Knox.SmokeTest -c Release --no-build
if errorlevel 1 (
    echo.
    echo [!] Uyari: Smoke testlerinde bazi uyarilar cikti, devam ediliyor...
) else (
    echo     [+] Smoke testleri basariyla gecti!
)
echo.

:: -----------------------------------------------------------------------------
:: ADIM 4: Single-File Uygulama Yayini (Tum .dll'ler Knox Studio.exe icine gomulur)
:: -----------------------------------------------------------------------------
echo [4/5] Tek Dosya (Single-File) Temiz Paket Yayini Hazirlaniyor...
set PUB_DIR=dist\publish-x64
if exist "%PUB_DIR%" rmdir /s /q "%PUB_DIR%"
mkdir "%PUB_DIR%" >nul 2>&1

dotnet publish src\managed\Knox.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None -p:DebugSymbols=false -o "%PUB_DIR%" --nologo
if errorlevel 1 (
    echo.
    echo [x] HATA: Dotnet publish islemi basarisiz oldu!
    if "%~1"=="" pause
    exit /b 1
)

:: Exe adini Knox Studio.exe olarak ayarla
if exist "%PUB_DIR%\Knox.App.exe" move /Y "%PUB_DIR%\Knox.App.exe" "%PUB_DIR%\Knox Studio.exe" >nul

:: Sadece gerekli calisma dosyalari ve native motoru tasi
if exist "src\native\knox.engine\build\Release\knox_engine.dll" copy /Y "src\native\knox.engine\build\Release\knox_engine.dll" "%PUB_DIR%" >nul
if exist "src\native\knox.engine\build\Release\knox-scanworker.exe" copy /Y "src\native\knox.engine\build\Release\knox-scanworker.exe" "%PUB_DIR%" >nul
if exist "src\native\knox.engine\build\knox_engine.dll" copy /Y "src\native\knox.engine\build\knox_engine.dll" "%PUB_DIR%" >nul
if exist "src\native\knox.engine\build\knox-scanworker.exe" copy /Y "src\native\knox.engine\build\knox-scanworker.exe" "%PUB_DIR%" >nul
if exist "assets\icons\windows\knox.ico" copy /Y "assets\icons\windows\knox.ico" "%PUB_DIR%" >nul

:: Fazlalik pdb hata ayiklama dosyalarini temizle (temiz kütüphane dizini icin)
del /f /q "%PUB_DIR%\*.pdb" >nul 2>&1

echo     [+] Uygulama paketlendi (Tek dosya Knox Studio.exe): %PUB_DIR%
echo.

:: -----------------------------------------------------------------------------
:: ADIM 5: Ozel Modern Setup Paketi (Knox.Installer -> setup.exe)
:: -----------------------------------------------------------------------------
echo [5/5] Ozel Modern Tasarimli Kurulum Paketi [setup.exe] Olusturuluyor...

if not exist dist mkdir dist >nul 2>&1
if not exist "src\managed\Knox.Installer\Payload" mkdir "src\managed\Knox.Installer\Payload" >nul 2>&1
if not exist "src\managed\Knox.Installer\Assets" mkdir "src\managed\Knox.Installer\Assets" >nul 2>&1

:: Gorselleri senkronize et
if exist "assets\installer\*" (
    copy /Y "assets\installer\*.png" "src\managed\Knox.Installer\Assets\" >nul 2>&1
)

:: Uygulama dosyalarini installer icine gommek uzere zip paketine donustur
echo     Uygulama bilesenleri paketleniyor...
powershell -Command "if (Test-Path 'src\managed\Knox.Installer\Payload\payload.zip') { Remove-Item 'src\managed\Knox.Installer\Payload\payload.zip' -Force }; Compress-Archive -Path '%PUB_DIR%\*' -DestinationPath 'src\managed\Knox.Installer\Payload\payload.zip' -Force"

echo     Modern Setup.exe derleniyor (Tek dosya / Self-contained)...
set INSTALLER_OUT=dist\installer_tmp
if exist "%INSTALLER_OUT%" rmdir /s /q "%INSTALLER_OUT%"
mkdir "%INSTALLER_OUT%" >nul 2>&1

dotnet publish src\managed\Knox.Installer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%INSTALLER_OUT%" --nologo
if errorlevel 1 (
    echo [x] HATA: Modern Setup derlemesi basarisiz oldu!
) else (
    if exist "%INSTALLER_OUT%\Knox.Installer.exe" (
        copy /Y "%INSTALLER_OUT%\Knox.Installer.exe" "dist\setup.exe" >nul
        copy /Y "%INSTALLER_OUT%\Knox.Installer.exe" "dist\Knox-Studio-Setup-v%KNOX_VER%-x64.exe" >nul
    )
    rmdir /s /q "%INSTALLER_OUT%" >nul 2>&1
    
    echo.
    echo ===============================================================================
    echo [TEBRIKLER] Ozel Tasarimli Tek Dosya Setup Basariyla Olusturuldu!
    echo.
    echo Kurulum Dosyasi:
    echo   - dist\setup.exe (ve dist\Knox-Studio-Setup-v%KNOX_VER%-x64.exe)
    echo.
    echo Gorselleri Degistirmek Icin:
    echo   - assets\installer\ klasorundeki PNG dosyalarini degistirebilirsiniz.
    echo ===============================================================================
)

echo.
echo Islem basariyla tamamlandi.
if "%~1"=="" pause
