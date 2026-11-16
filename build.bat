@echo off
setlocal
echo ========================================================
echo   Building Keyboard Debouncer (Windows Standalone)
echo ========================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
)

if not exist "%CSC%" (
    echo [ERROR] .NET Framework C# Compiler (csc.exe) not found.
    pause
    exit /b 1
)

echo Using compiler: %CSC%
echo Compiling KeyboardDebouncer.cs ...

"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /out:KeyboardDebouncer.exe /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll KeyboardDebouncer.cs

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Compilation failed!
    pause
    exit /b %errorlevel%
)

echo.
echo [SUCCESS] KeyboardDebouncer.exe successfully compiled!
echo.
pause
