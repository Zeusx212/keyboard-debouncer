@echo off
cd /d "%~dp0"
python keyboard_debounce.py status
echo.
python keyboard_debounce.py log
pause
