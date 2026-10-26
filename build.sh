#!/bin/sh
# Build NexLimit on Linux: C# app (Mono mcs against .NET Framework 4.7.2), Windows resources, NSIS installer.
# Needs: mono-devel, binutils-mingw-w64 (windres), nsis. dist/ must hold WinDivert.dll + WinDivert64.sys (./get-windivert.sh).
set -e
cd "$(dirname "$0")"
[ -f dist/WinDivert.dll ] || sh get-windivert.sh
x86_64-w64-mingw32-windres --preprocessor=cpp res/app.rc -O res -o res/app.res
mcs -sdk:4.7.2 -langversion:Experimental -target:winexe -platform:x64 -unsafe -optimize+ -win32res:res/app.res \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -out:dist/NexLimit.exe src/*.cs
cp res/NexLimit.exe.config dist/
mkdir -p dist/licenses && cp licenses/* dist/licenses/
mcs -langversion:Experimental -unsafe -r:System.Windows.Forms.dll -r:System.Drawing.dll -out:test/tests.exe \
    test/Tests.cs src/*.cs -main:NexLimit.Tests
mono test/tests.exe
( cd installer && mkdir -p out && makensis -V2 installer.nsi )
ls -la dist installer/out
