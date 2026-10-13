@echo off
cd /d "%~dp0"
dotnet DnsServerApp.dll --portable-app %*
