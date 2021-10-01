if "%1"=="/console" goto console
if "%1"=="/service" goto service
goto exit
:console
ping 127.0.0.1 -n 2 > nul
OruxPalsServer.exe
goto exit
:service
ping 127.0.0.1 -n 2 > nul
OruxPalsServer.exe /start
goto exit
pause
:exit