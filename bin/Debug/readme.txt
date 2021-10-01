This is the OruxPalsServer specially written for OruxMaps Android Application 
(6.5.5+ for AIS and 7.0.0rc9+ for APRS). The server can receive position from
OruxMaps application by GPSGate (HTTP GET) protocol, MapMyTracks protocol or
or APRS protocol. Server stores received positions and send it to all clients 
connected by AIS or APRS. So you can watch on the map user position in real 
time as vessels (by AIS) or as aprs icons (by APRS) with names.

Also server can receive data via Big Brother GPS protocol, OsmAnd protocol and 
Traccar protocol. You can user OwnTracks Client to send data to server and 
receive friends locations via OwnTracks protocol. 

Server also can filter sending data to each client with specified user filters:
- range filter for static objects (me/10/50);
- name filters to pass or block incoming positions from users or static objects 
  (+sw/ +ew/ +fn/ -sw/ -ew/ -fn/).

Server also can grab FlightRadar24 data and send it to user wich specify the filter:
  (FR24)


WEB ONLINE:


	To view server info in browser:
		http://127.0.0.1:12015/oruxpals/info
	
	To view online map:
		http://127.0.0.1:12015/oruxpals/view
	
	To online generate user hashsum (xml tag <adminName/>):
		http://127.0.0.1:12015/oruxpals/$admin


		
CONFIGURE ORUXMAPS:		

	To connect for upload positions (without speed & course) use MapMyTracks:
		URL: http://www.mypals.com:12015/oruxpals/m/
		USER: user - must be from 3 to 9 symbols of any [A-Z0-9]
		PASSWORD:  calculating by hashsum for user by console or browser
		
	To connect for upload positions (with speed & course) use GPSGate:
		URL: http://www.mypals.com:12015/oruxpals/@user/
			 ! user - must be from 3 to 9 symbols of any [A-Z0-9]
		IMEI:  user's password, calculating by hashsum for user by console or browser
				
	To connect for view positions using AIS:
		AIS URL:  127.0.0.1:12015
		
	To connect for view and upload position using APRS:
		APRS URL: 127.0.0.1:12015

		
CONFIGURE APRS Client (APRSDroid / OruxMaps)   

	To connect for view & upload data use APRS Client:
		URL: 127.0.0.1:12015    
		
	filter supported (in APRS auth string):	
		; Standart filters not used http://www.aprs-is.net/javAPRSFilter.aspx and passed ;
		Static objects range/limit filter (filter is not apply for users positions):
			me/10/30 - maximum 30 static objects from me in 10 km range
			me/10 - static objects from me in 10 km range			
			me/0 - no static objects
			me/-1 - no static, no everytime objects
		; user can use filter me/range/limit, if he doesn't want to use specified range/limit by xml config file
		; if user doesn't use me/range/limit filter, static objects will display within range/limit from xml config file
		Name (Group) filter:
			+sw/A/B/C - pass users/objects pos with name starts with A or B or C
			+ew/A/B/C - pass users/objects pos with name ends with A or B or C
			+fn/ULKA/RUZA - pass users/objects pos with name ULKA or RUZA
			-sw/A/B/C - block users/objects pos with name starts with A or B or C		
			-ew/A/B/C - block users/objects pos with name ends with A or B or C		
			-fn/ULKA/RUZA -  block users/objects pos with name ULKA or RUZA
		No APRS-IS or AIR APRS Data:
			-fn/AIR - block all incoming to user APRS-IS or APRS-on-AIR DATA
		; pass filters are first processed, then block.
		; by default pass all; but if you use any + filters, default is block.
		You can use Name filter to create separeted groups that receive positions only from itself group users, ex:
			for users: USERAG1, USER2G1,USERBG1,USER2G1 set filter: +ew/G1
			for users: G2ANNA,G2ALEX,G2VICTOR set filter: +sw/G2
		FlightRadar24 show AirPlanes with specified Filter:
			FR24/ZoneWHSize/Interval/minspeed/FILTER default FR24/20/15/0;
			FR24/ZoneWHSize/Interval/minspeed/FILTER/FILTER
			FR24/ZoneWHSize/Interval/minspeed/FILTER/FILTER/FILTER/ ... /FILTER
				; ZoneWHSize can be: F20 (Zone with 20 degrees width & height) N - to north only; E - to east only; S - to south only; W - to west only;
                ; NE - 0°..90°; SE - 90°..180°; SW - 180°..270°; NW - 270°..360°; EN - 0°..90°; ES - 90°..180°; WS - 180°..270°; WN - 270°..360°; 
                ; ZoneWHSize can be: R10-20 -- 10 - zone 10 degrees width and 20 degrees height
                ; ZoneWHSize can be: S10-20 -- zone 10 degrees width & 20 degrees to south
                ; ZoneWHSize can be: NW5-15 -- zone 5 degrees to west and 15 degrees to north
			FR24/15/10/0 - show airplanes in zone 15 degrees height & width, update every 10 seconds with speed 0 knots or more
			FR24/17/7/50/VKO - show airplanes in zone 17 degrees height & width, update every 7 seconds with speed 50 knots or more contains text `VKO`
			FR24/17/7/50/SU6138 - show airplanes in zone 17 degrees height & width, update every 7 seconds with speed 50 knots or more with flight number `SU6138`
			FR24/1 - show airplanes in zone 1 height & width
			FR24/F1 - show airplanes in zone 1 height & width
			FR24/N10/7/30 - show airplanes in zone 10 degrees width and 10 degrees height to north, update every 7 seconds with speed 30 knots or more
		NarodMon.ru weather Sensors with values on the map:
			+nm/range_in_km/max_objects default +nm/10/50
				; range_in_km - raidus to search weather in km
				; max_objects - limit of max objects
			+nm/20/50
		; Grab URL can be set in `FlightRadarGW.xml`, minumum update interval is 5 seconds
	filter examples:
		me/10/30 +sw/M4/MOBL +fn/M4OLGA
		me/5/15 -sw/M4ASZ/MSKAZS -fn/BOAT
		me/5/15 -sw/M4ASZ/MSKAZS -fn/BOAT FR24/10/15/45
		+ew/G1 +sw/G2
		-ew/G1 -sw/G3
		me/-1 -fn/AIR +nm
		
	SUPPORTED COMMANDS FROM APRSDroid to Server (APRS packet type Message):
		msg to ORXPLS-GW: forward   - get forward state for user (<u forward="???"/> tag)
		msg to ORXPLS-GW: forward A  - set forward state to A (send users data to global APRS)
		msg to ORXPLS-GW: forward 0  - set forward state to 0 (zero, no forward)
		msg to ORXPLS-GW: forward ALH  - set forward state to ALH
		msg to ORXPLS-GW: forward ALHDO  - set forward state to ALHDO
		msg to ORXPLS-GW: kill UserName  - delete from server info about user
		msg to ORXPLS-GW: admin NewUserName   - get APRS password & OruxPalsServer password for NewUserName (admin from xml tag <adminName/>)
		msg to ORXPLS-ST: global status here  - send status message to global APRS `:>`
		msg to ORXPLS-CM: ?  - get comment for position report forwarding data to APRS-IS when client connected not via APRS 
		msg to ORXPLS-CM: comment for position report here  - set comment for position report forwarding data to APRS-IS when client connected not via APRS 
		// when client connected via APRS client all data from him will directly send to global APRS-IS (if forwarding setting allows to user forward him data)

		
CONFIGURE OTHER CLIENTS:		
				
	To connect for upload positions use GPSGate Tracker:
		URL: 127.0.0.1:12015
		PHONE: user's phone number (defined by xml tag <u phone="+???"/>)
	
	To connect for upload positions use Big Brother GPS
		URL: http://127.0.0.1:12015/oruxpals/bb/user_password
		
	To connect for upload positions use Owntracks (HTTP Mode) send itself & receive friends locations
		URL: http://127.0.0.1:12015/oruxpals/ot/user_password
		URL: http://127.0.0.1:12015/oruxpals/ot/  -  Set Identification User and Password
		
	To connect for upload positions use Traccar Client
		URL: http://127.0.0.1:12015/oruxpals/oa/  - user_password as device id (&id=user_password) required 
		
	To connect for upload positions use OsmAnd
		URL: http://127.0.0.1:12015/oruxpals/oa/?id=user_password&lat={0}&lon={1}&speed={5}&bearing={6}

 
AUTHORIZATION: 
 
	user - must be from 3 to 9 symbols of any [A-Z0-9]
	password - calculating by hashsum for user by console or browser
	! for APRS Clients password if default for they Callsign


How to launch:
    OruxPalsServer.xml - configuration
	
	Run at console:
		OruxPalsServer.exe
		
	Install as service:
		OruxPalsServer.exe /install
		
	Uninstall service:
		OruxPalsServer.exe /uninstall
		
	Start service:
		OruxPalsServer.exe /start
	
	Stop service:
		OruxPalsServer.exe /stop
		
	Restart service:
		OruxPalsServer.exe /restart
		
	Service status:
		OruxPalsServer.exe /status
	
	Generate passcode:
		OruxPalsServer.exe userName

	Import kml file to SQLite `StaticObjects.db`
		OruxPalsServer.exe /kml2sql
		OruxPalsServer.exe /kml2sql <file>

SQLite DB:
  To manage Data you can user SQLiteStudio or SQLiteBrowser.
  Static objects are in Table `OBJECTS`
  To clean all data from SQLite just copy `Empty.db` to `StaticObjects.db`
	
	