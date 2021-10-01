This is the OruxPalsAir specially written for OruxPalsServer

It allows:
 - receive APRS AFSK 1200 AX.25 packets from Air (sound card direct input)  
 and send it via TCP to OruxPalsServer or APRS-IS;
 - receive APRS packets from OruxPalsServer or APRS-IS via TCP and send it
 to Air (AFSK 1200 AX.25, sound card direct output)  

How to launch:
    OruxPalsAir.xml - configuration
	
	Run at console:
		OruxPalsAir.exe
		
	Install as service:
		OruxPalsAir.exe /install
		
	Uninstall service:
		OruxPalsAir.exe /uninstall
		
	Start service:
		OruxPalsAir.exe /start
	
	Stop service:
		OruxPalsAir.exe /stop
		
	Restart service:
		OruxPalsAir.exe /restart
		
	Service status:
		OruxPalsAir.exe /status
	
	List audio devices:
		OruxPalsAir.exe /listaudio	
	