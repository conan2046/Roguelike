@echo off
setlocal
REM Launch formal stage run (TbStage id=1).
REM Close any open Unity instance of this project first, or the argument will not take effect.
REM Set UNITY_EDITOR_EXE or pass the editor executable as the first argument.
if not "%~1"=="" set "UNITY_EDITOR_EXE=%~1"
if not defined UNITY_EDITOR_EXE (
    echo Set UNITY_EDITOR_EXE or pass the Unity Editor executable.
    exit /b 1
)
if not exist "%UNITY_EDITOR_EXE%" (
    echo The configured Unity Editor executable does not exist.
    exit /b 1
)
for %%I in ("%~dp0..") do set "ROUGELIKE_PROJECT_ROOT=%%~fI"
"%UNITY_EDITOR_EXE%" -projectPath "%ROUGELIKE_PROJECT_ROOT%" -stage 1
