@echo off
setlocal
rem Offline compiler invocation only; no installer, driver, device or network call.
if "%~1"=="" exit /b 2
if "%~2"=="" exit /b 2
call "%~1\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
set "HX_NATIVE_SRC=%~dp0"
pushd "%~2"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /fp:strict /GS /sdl /guard:cf /Brepro /LD /Fo:hvx_hybrid_core.obj "%HX_NATIVE_SRC%hvx_hybrid_core.cpp" /link /OUT:HvxHybridCore.dll /IMPLIB:HvxHybridCore.lib /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf /INCREMENTAL:NO /Brepro
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /fp:strict /GS /sdl /guard:cf /Brepro /Fo:native_smoke.obj "%HX_NATIVE_SRC%native_smoke.cpp" HvxHybridCore.lib /link /OUT:native_smoke.exe /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf /INCREMENTAL:NO /Brepro
if errorlevel 1 exit /b %errorlevel%
dumpbin /nologo /exports HvxHybridCore.dll
if errorlevel 1 exit /b %errorlevel%
native_smoke.exe
set "HX_NATIVE_EXIT=%errorlevel%"
popd
exit /b %HX_NATIVE_EXIT%
