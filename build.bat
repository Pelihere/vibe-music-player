@echo off
rem Builds a single portable 64-bit exe: publish\PortableMusicPlayer.exe
rem Requires the .NET SDK 8 or later (not just the runtime): https://aka.ms/dotnet-download

dotnet --list-sdks 2>nul | findstr /r "^[0-9]" >nul
if %errorlevel% neq 0 (
  echo No .NET SDK found. Install the .NET SDK 8 or later from https://aka.ms/dotnet-download
  echo then open a NEW terminal and run build.bat again.
  pause
  exit /b 1
)

dotnet publish -c Release -o publish
if %errorlevel% neq 0 (
  echo Build failed.
  pause
  exit /b 1
)
if not exist "publish\PortableMusicPlayer.exe" (
  echo Build finished but publish\PortableMusicPlayer.exe was not created.
  pause
  exit /b 1
)
echo.
echo Done: publish\PortableMusicPlayer.exe
pause
