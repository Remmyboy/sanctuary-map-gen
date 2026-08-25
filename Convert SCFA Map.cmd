@echo off
rem Double-click launcher for the converter GUI.
pwsh -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Convert-ScMapGui.ps1"
