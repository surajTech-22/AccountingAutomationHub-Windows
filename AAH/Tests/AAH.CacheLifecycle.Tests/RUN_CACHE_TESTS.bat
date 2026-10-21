@echo off
setlocal
cd /d "%~dp0\..\.."
dotnet run --project "Tests\AAH.CacheLifecycle.Tests\AAH.CacheLifecycle.Tests.csproj" --configuration Release
exit /b %errorlevel%
