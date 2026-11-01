@echo off
rem Builds TaskbarSplit.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /out:TaskbarSplit.exe ^
  /lib:"%FW%\WPF" /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll TaskbarSplit.cs
