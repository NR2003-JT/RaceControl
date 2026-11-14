@echo off
rem Builds RaceControl.exe (and the offline sim) with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set SRC=src\GameMemory.cs src\Addr.cs src\RaceEngine.cs src\RaceEngine.Wave.cs src\RaceEngine.Lineup.cs src\RaceEngine.Cautions.cs src\RaceEngine.Pits.cs src\RaceEngine.Live.cs src\RaceEngine.PaceCar.cs src\RaceEngine.LowLine.cs src\RaceEngine.PlayerCall.cs src\RaceEngine.Start.cs
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:x86 /optimize+ /out:bin\RaceControl.exe /win32manifest:src\app.manifest /win32icon:src\RaceControl.ico /resource:src\logo.png,RaceControl.logo.png /resource:src\wheel_nascar.png,RaceControl.wheel_nascar.png /resource:src\wheel_indy.png,RaceControl.wheel_indy.png ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll %SRC% src\Hub.cs src\Qualifying.cs src\MainForm.cs src\Presets.cs src\LiveServer.cs src\LivePage.cs src\ConeChooser.cs src\ConeArt.cs src\ConeBoard.cs src\QualOverlay.cs src\PlayerBanner.cs src\Theme.cs src\TrackMap.cs src\TrackMapForm.cs src\Telemetry.cs src\TeleStrip.cs src\TelemetryWindow.cs src\WheelAnim.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\RaceControlSim.exe %SRC% test\SimMain.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\CautionSim.exe %SRC% test\CautionSim.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\StageSim.exe %SRC% test\StageSim.cs || exit /b 1
echo built bin\RaceControl.exe
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\TrackMapSim.exe /r:System.Web.Extensions.dll src\GameMemory.cs src\Addr.cs src\TrackMap.cs test\TrackMapSim.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\BoardRender.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll %SRC% src\ConeChooser.cs src\ConeArt.cs src\ConeBoard.cs test\BoardRender.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\QualSim.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll %SRC% src\Hub.cs src\Qualifying.cs src\QualOverlay.cs src\ConeChooser.cs src\ConeArt.cs src\ConeBoard.cs src\Telemetry.cs test\QualSim.cs || exit /b 1
"%CSC%" /nologo /target:exe /platform:x86 /out:bin\TeleLive.exe /resource:src\wheel_nascar.png,RaceControl.wheel_nascar.png /resource:src\wheel_indy.png,RaceControl.wheel_indy.png /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll %SRC% src\Hub.cs src\Qualifying.cs src\QualOverlay.cs src\ConeChooser.cs src\ConeArt.cs src\ConeBoard.cs src\Telemetry.cs src\TeleStrip.cs src\TelemetryWindow.cs test\TeleLive.cs || exit /b 1
