@echo off
rem Command line of clodlogs. A batch file waits for the desktop executable, so the shell
rem prompt returns only after the command has finished and the exit code is passed on.
"%~dp0Clodlogs.Desktop.exe" %*
exit /b %ERRORLEVEL%
