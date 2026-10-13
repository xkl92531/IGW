@echo off
setlocal

rem Build IGW: Rust native component, Vue UI, Runtime (with built-in log storage), Inno Setup.
rem Usage: build.bat [--no-bump]
set "ROOT=%~dp0.."
set "DIST=%ROOT%\dist\IGW"
set "ISCC=%LOCALAPPDATA%\Programs\InnoSetupBuild\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "CARGO=%USERPROFILE%\.cargo\bin\cargo.exe"
if not exist "%CARGO%" set "CARGO=cargo"
set "PNPM_CMD=pnpm"
set "CODEX_DEPS=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies"
if exist "%CODEX_DEPS%\bin\fallback\pnpm.cmd" set "PNPM_CMD=%CODEX_DEPS%\bin\fallback\pnpm.cmd"
if exist "%CODEX_DEPS%\node\bin\node.exe" set "PATH=%CODEX_DEPS%\node\bin;%PATH%"

echo [0/4] Building S7 Plus native component...
pushd "%ROOT%\native\igw-s7plus" || goto :error
"%CARGO%" build --release
if errorlevel 1 goto :native_gnu
popd
goto :native_built

rem Without MSVC C/C++ headers or libraries, fall back to the GNU (MinGW) toolchain; the shipped DLL only
rem depends on Windows system libraries either way. GNU ld cannot handle non-ASCII paths, so build under
rem LOCALAPPDATA and copy the DLL back into target\release.
:native_gnu
echo MSVC build failed; retrying with the stable-x86_64-pc-windows-gnu toolchain...
set "GNU_TARGET=%LOCALAPPDATA%\igw-s7plus-target"
set "CARGO_TARGET_DIR=%GNU_TARGET%"
"%CARGO%" +stable-x86_64-pc-windows-gnu build --release
set "GNU_EXIT=%ERRORLEVEL%"
set "CARGO_TARGET_DIR="
if not "%GNU_EXIT%"=="0" goto :native_error
if not exist "%ROOT%\native\igw-s7plus\target\release" mkdir "%ROOT%\native\igw-s7plus\target\release"
copy /y "%GNU_TARGET%\release\igw_s7plus.dll" "%ROOT%\native\igw-s7plus\target\release\igw_s7plus.dll" >nul || goto :native_error
popd
:native_built
if not exist "%ROOT%\native\igw-s7plus\target\release\igw_s7plus.dll" goto :missing_native

rem Resolve the version before publishing: assembly versions identify the build in field logs and diagnostics.
rem A failed build keeps the bumped version; rerun with --no-bump to reuse it.
if /i "%~1"=="--no-bump" goto :read_version
for /f "delims=" %%v in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump_version.ps1"') do set "APPVER=%%v"
goto :version_ready

:read_version
set /p APPVER=<"%~dp0version.txt"

:version_ready
if not defined APPVER goto :missing_version
echo Packaging version %APPVER%

echo [1/4] Building embedded Web UI...
pushd "%ROOT%\src\IGW.WebUi" || goto :error
set "CI=true"
call "%PNPM_CMD%" install --frozen-lockfile --silent
if errorlevel 1 goto :web_error
call "%PNPM_CMD%" build
if errorlevel 1 goto :web_error
set "CI="
popd

echo [2/4] Publishing self-contained Runtime...
if exist "%DIST%" rmdir /s /q "%DIST%"
dotnet publish "%ROOT%\src\IGW.Runtime\IGW.Runtime.csproj" -c Release -p:Version=%APPVER% -r win-x64 --self-contained true -o "%DIST%" --nologo
if errorlevel 1 goto :error
if not exist "%DIST%\igw_s7plus.dll" goto :missing_published_native
call powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-questdb.ps1" -RuntimeDirectory "%DIST%" -Platform windows
if errorlevel 1 goto :error

rem 安装向导里“测试连接/建库”用的数据库设置工具(单文件，安装前即可运行)
if exist "%ROOT%\dist\IGW.DbSetup" rmdir /s /q "%ROOT%\dist\IGW.DbSetup"
dotnet publish "%ROOT%\src\IGW.DbSetup\IGW.DbSetup.csproj" -c Release -p:Version=%APPVER% -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "%ROOT%\dist\IGW.DbSetup" --nologo
if errorlevel 1 goto :error

rem 数据迁移工具(WinForms 单文件，装到安装目录 tools 下，开始菜单有快捷方式)
if exist "%ROOT%\dist\IGW.DataMover" rmdir /s /q "%ROOT%\dist\IGW.DataMover"
dotnet publish "%ROOT%\src\IGW.DataMover\IGW.DataMover.csproj" -c Release -p:Version=%APPVER% -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%ROOT%\dist\IGW.DataMover" --nologo
if errorlevel 1 goto :error

call powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-publish-layout.ps1" -RuntimeDirectory "%DIST%"
if errorlevel 1 goto :error

rem 安装脚本回归测试：不通过就不出包。例如升级时不能重设数据目录权限，否则文件多的现场会卡在 Creating directories。
for %%t in ("%~dp0*.test.cjs") do (
  node --test "%%~t"
  if errorlevel 1 goto :installer_test_error
)

echo [3/4] Compiling installer...
"%ISCC%" "%~dp0IGW.iss"
if errorlevel 1 goto :error
echo [4/4] Complete: "%~dp0out\IGW-Setup-%APPVER%.exe"
exit /b 0

:native_error
popd
goto :error

:web_error
set "CI="
popd
goto :error

:installer_test_error
echo Installer script tests failed; see the failing test above.
goto :error

:missing_version
echo Unable to determine the package version from version.txt
goto :error

:missing_native
echo Missing native\igw-s7plus\target\release\igw_s7plus.dll
goto :error

:missing_published_native
echo Missing dist\IGW\igw_s7plus.dll
goto :error

:error
echo Build failed.
exit /b 1
