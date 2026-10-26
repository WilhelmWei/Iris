@echo off
rem AI PC Checkup - remote collector. Read-only: no network, installs nothing, changes nothing.
rem https://github.com/WilhelmWei/Iris
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0remote.ps1"
