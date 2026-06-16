@echo off

cd /d "%~dp0"

dotnet publish .\kw1281test.csproj ^
  -c Debug ^
  -r win-x86 ^
  --disable-build-servers ^
  -p:PublishSingleFile=true ^
  -p:SelfContained=true ^
  -p:PlatformTarget=x86 ^
  --output .\artifacts\publish\debug\win-x86-single

dotnet publish .\kw1281test.csproj ^
  -c Debug ^
  -r win-x64 ^
  --disable-build-servers ^
  -p:PublishSingleFile=true ^
  -p:SelfContained=true ^
  -p:PlatformTarget=x64 ^
  --output .\artifacts\publish\debug\win-x64-single
