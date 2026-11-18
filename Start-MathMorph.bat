@echo off
rem Starts the MathMorph GUI from this folder (no install needed).
set "MATHMORPH_HOME=%~dp0"
if "%MATHMORPH_HOME:~-1%"=="\" set "MATHMORPH_HOME=%MATHMORPH_HOME:~0,-1%"
if not exist "%MATHMORPH_HOME%\input" mkdir "%MATHMORPH_HOME%\input"
if not exist "%MATHMORPH_HOME%\output" mkdir "%MATHMORPH_HOME%\output"
start "" "%MATHMORPH_HOME%\gui_bin\MathMorph.Gui.exe"
