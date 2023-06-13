#define _DEBUGORUX

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Web;
using System.Xml;
using System.Xml.Serialization;

namespace OruxPals
{
    public class OruxPalsServer
    {
        public static string serviceName { get { return "OruxPalsServer"; } }
        public string ServerName = "OruxPalsServer";
        public static string softver { get { return "OruxPalsServer v0.81b fr24/BB/TC/WS Multiport"; } }
        public static string softshort { get { return "OruxPalsServer v0.81b"; } }
        public string Web = "http://127.0.0.1:{port}/{path}";

        private static bool _NoSendToFRS = true; // GPSGate Tracker didn't support $FRPOS

        private OruxPalsServerConfig.RegUser[] regUsers;
        private Hashtable clientList = new Hashtable();
        private int threadsStarted = 0;
        private Thread listenThread = null;
        private Thread aprsThread = null;
        private Thread aisThread = null;
        private Thread httpThread = null;
        private Thread frsThread = null;
        private TcpListener mainListener = null;
        private TcpListener aprsListener = null;
        private TcpListener aisListener = null;
        private TcpListener httpListener = null;
        private TcpListener frsListener = null;
        private bool isRunning = false;        
        private ulong clientCounter = 0;
        private ulong mmtactCounter = 0;
        private Buddies BUDS = null;
        private DateTime started;

        private APRSISConfig aprscfg;
        private APRSISGateWay aprsgw;

        private OruxPalsServerConfig.FwdSvc[] forwardServices;
        
        private IPAddress ListenIP = IPAddress.Any;
        private int ListenPort = 12015;
        private int OnlyAPRSPort = 0;
        private int OnlyAISPort = 0;
        private int OnlyHTTPPort = 0;
        private int OnlyFRSPort = 0;
        private List<string> BlackListIP = new List<string>();
        private List<string> LocalNetwork = new List<string>();
        private ushort MaxClientAlive = 60;
        private byte maxHours = 48;
        private ushort greenMinutes = 60;
        private int KMLObjectsRadius = 5;
        private int KMLObjectsLimit = 50;
        private string urlPath = "/oruxpals/";
        private string adminName = "admin";
        private string adminPass = "oruxpalsadmin";
        private bool disableAIS = false;
        private bool sendBack = false;
        private bool callsignToUser = true;
        private string infoIP = "127.0.0.1";
        private List<string> banlist = new List<string>();

        public static void InitCPU()
        {
            string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (IntPtr.Size == 8) // or: if(Environment.Is64BitProcess) // .NET 4.0
            {
                File.Copy(Path.Combine(path, "x64") + @"\System.Data.SQLite.dll", path + @"\System.Data.SQLite.dll", true);
                File.Copy(Path.Combine(path, "x64") + @"\SQLite.Interop.dll", path + @"\SQLite.Interop.dll", true);
            }
            else
            {
                File.Copy(Path.Combine(path, "x86") + @"\System.Data.SQLite.dll", path + @"\System.Data.SQLite.dll", true);
                File.Copy(Path.Combine(path, "x86") + @"\SQLite.Interop.dll", path + @"\SQLite.Interop.dll", true);
            };
        }

        public OruxPalsServer() 
        {
            //InitCPU();
                       
            OruxPalsServerConfig config = OruxPalsServerConfig.LoadFile("OruxPalsServer.xml");
            ListenPort = config.ListenPort;
            OnlyAPRSPort = config.OnlyAPRSPort;
            OnlyAISPort = config.OnlyAISPort;
            OnlyHTTPPort = config.OnlyHTTPPort;
            OnlyFRSPort = config.OnlyFRSPort;
            if (config.BlackListIP != null) { foreach (string ip in config.BlackListIP) if (!String.IsNullOrEmpty(ip)) BlackListIP.Add(ip); };
            if (config.LocalNetwork != null) { foreach (string ln in config.LocalNetwork) if (!String.IsNullOrEmpty(ln)) LocalNetwork.Add(ln); };
            MaxClientAlive = config.maxClientAlive;
            maxHours = config.maxHours;
            greenMinutes = config.greenMinutes;
            KMLObjectsRadius = config.KMLObjectsRadius;
            KMLObjectsLimit = config.KMLObjectsLimit;
            if (config.urlPath.Length != 8) throw new Exception("urlPath must be 8 symbols length");
            adminName = config.adminName;
            adminPass = config.adminPass;
            disableAIS = config.disableAIS;
            sendBack = config.sendBack == "yes";
            callsignToUser = config.callsignToUser == "yes";
            infoIP = config.infoIP;
            urlPath = "/"+config.urlPath.ToLower()+"/";
            if (config.users != null) regUsers = config.users.users;
            aprscfg = config.aprsis;
            forwardServices = config.forwardServices.services;
            ServerName = config.ServerName;
            banlist.AddRange(config.banlist.ToUpper().Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }        

        public bool Running { get { return isRunning; } }
        public IPAddress ServerIP { get { return ListenIP; } }
        public int ServerPort { get { return ListenPort; } set { ListenPort = value; } }
        public int ServerAPRSPort { get { return OnlyAPRSPort; } set { OnlyAPRSPort = value; } }
        public int ServerAISPort { get { return OnlyAISPort; } set { OnlyAISPort = value; } }
        public int ServerHTTPPort { get { return OnlyHTTPPort; } set { OnlyHTTPPort = value; } }
        public int ServerFRSPort { get { return OnlyFRSPort; } set { OnlyFRSPort = value; } }

        public void Dispose() { Stop(); }
        ~OruxPalsServer() { Dispose(); }

        public void Start()
        {
            if (isRunning) return;
            started = DateTime.UtcNow;
            WriteLineToConsole(String.Format("Starting {0} at {1}:{2}\r\n", softver, infoIP, ListenPort));
            if (OnlyAPRSPort > 0) WriteLineToConsole(String.Format(" - Only {0} at {1}:{2} ", "APRS", infoIP, OnlyAPRSPort));
            if (OnlyAISPort > 0) WriteLineToConsole(String.Format(" - Only {0} at {1}:{2} ", "AIS", infoIP, OnlyAISPort));
            if (OnlyHTTPPort > 0) WriteLineToConsole(String.Format(" - Only {0} at {1}:{2} ", "HTTP", infoIP, OnlyHTTPPort));
            if (OnlyFRSPort > 0) WriteLineToConsole(String.Format(" - Only {0} at {1}:{2} ", "FRS", infoIP, OnlyFRSPort));
            WriteLineToConsole("");
            BUDS = new Buddies(maxHours, greenMinutes, KMLObjectsRadius, KMLObjectsLimit);
            BUDS.Init(new Buddies.CheckRUser(CheckRegisteredUser));
            BUDS.onBroadcastAIS = new Buddies.BroadcastMethod(BroadcastAIS);
            BUDS.onBroadcastAPRS = new Buddies.BroadcastMethod(BroadcastAPRS);
            BUDS.onBroadcastFRS = new Buddies.BroadcastMethod(BroadcastFRS);
            BUDS.onBroadcastWeb = new Buddies.BroadcastMethod(BroadcastWeb);

            isRunning = true;
            threadsStarted = 0;
            listenThread = new Thread(MainThreadALL);
            listenThread.Start();
            System.Threading.Thread.Sleep(100);
            if (OnlyAPRSPort > 0)
            {
                aprsThread = new Thread(MainThreadAPRS);
                aprsThread.Start();
            };
            if (OnlyAISPort > 0)
            {
                aisThread = new Thread(MainThreadAIS);
                aisThread.Start();
            };
            if (OnlyHTTPPort > 0)
            {
                httpThread = new Thread(MainThreadHTTP);
                httpThread.Start();
            };
            if (OnlyFRSPort > 0)
            {
                frsThread = new Thread(MainThreadFRS);
                frsThread.Start();
            };

            if ((aprscfg != null) && (aprscfg.user != null) && (aprscfg.user != String.Empty) && (aprscfg.url != null) && (aprscfg.url != String.Empty) &&
                ((aprscfg.global2ais == "yes") || (aprscfg.global2aprs == "yes") || (aprscfg.global2frs == "yes") || (aprscfg.aprs2global == "yes") || (aprscfg.any2global == "yes")))
            {
                aprsgw = new APRSISGateWay(aprscfg);
                aprsgw.onPacket = new APRSISGateWay.onAPRSGWPacket(OnGlobalAPRSData);
                aprsgw.Start();
            };

            //Start by default
            //if (FlightRadar != null)
            //    if (FlightRadar.Interval > 0)
            //        FlightRadar.Start();
        }

        private void IfStartedAll()
        {
            int MustBee = 1 + (OnlyAPRSPort > 0 ? 1 : 0) + (OnlyAISPort > 0 ? 1 : 0) + (OnlyHTTPPort > 0 ? 1 : 0) + (OnlyFRSPort > 0 ? 1 : 0);
            if (threadsStarted == MustBee)
                WriteLineToConsole("\r\nStarted all done -- switched to working Mode");
        }

        private void MainThreadALL()
        {
            mainListener = new TcpListener(this.ListenIP, this.ListenPort);
            mainListener.Start();
            threadsStarted++;
            WriteLineToConsole(String.Format("MAIN at: http://{2}:{0}{1}info", ListenPort, urlPath, infoIP));
            WriteLineToConsole(String.Format("Info at: http://{2}:{0}{1}info",ListenPort, urlPath, infoIP));
            WriteLineToConsole(String.Format("FR24 at: http://{2}:{0}{1}fr24", ListenPort, urlPath, infoIP));
            WriteLineToConsole(String.Format("Admin at: http://{2}:{0}{1}$master", ListenPort, urlPath, infoIP));
            IfStartedAll();
            (new Thread(PingNearestThread)).Start(); // ping clients thread
            while (isRunning)
            {
                try
                {
                    TcpClient client = mainListener.AcceptTcpClient();
                    GetClient(client, this.ListenPort);
                }
                catch { };                
                Thread.Sleep(10);
            };
        }

        private void MainThreadAPRS()
        {
            aprsListener = new TcpListener(this.ListenIP, this.OnlyAPRSPort);
            aprsListener.Start();
            threadsStarted++;
            WriteLineToConsole(String.Format(" - APRS at: aprs://{2}:{0}", OnlyAPRSPort, urlPath, infoIP));
            IfStartedAll();
            while (isRunning)
            {
                try
                {
                    TcpClient client = aprsListener.AcceptTcpClient();
                    GetClient(client, this.OnlyAPRSPort);
                }
                catch { };
                Thread.Sleep(10);
            };
        }

        private void MainThreadAIS()
        {
            aisListener = new TcpListener(this.ListenIP, this.OnlyAISPort);
            aisListener.Start();
            threadsStarted++;
            WriteLineToConsole(String.Format(" - AIS at: ais://{2}:{0}", OnlyAISPort, urlPath, infoIP));
            IfStartedAll();
            while (isRunning)
            {
                try
                {
                    TcpClient client = aisListener.AcceptTcpClient();
                    GetClient(client, this.OnlyAISPort);
                }
                catch { };
                Thread.Sleep(10);
            };
        }

        private void MainThreadHTTP()
        {
            httpListener = new TcpListener(this.ListenIP, this.OnlyHTTPPort);
            httpListener.Start();
            threadsStarted++;
            WriteLineToConsole(String.Format(" - Info at: http://{2}:{0}{1}info", OnlyHTTPPort, urlPath, infoIP));
            WriteLineToConsole(String.Format(" - FR24 at: http://{2}:{0}{1}fr24", OnlyHTTPPort, urlPath, infoIP));
            WriteLineToConsole(String.Format(" - Admin at: http://{2}:{0}{1}$master", OnlyHTTPPort, urlPath, infoIP));
            IfStartedAll();
            while (isRunning)
            {
                try
                {
                    TcpClient client = httpListener.AcceptTcpClient();
                    GetClient(client, this.OnlyHTTPPort);
                }
                catch { };
                Thread.Sleep(10);
            };
        }

        private void MainThreadFRS()
        {
            frsListener = new TcpListener(this.ListenIP, this.OnlyFRSPort);
            frsListener.Start();
            threadsStarted++;
            WriteLineToConsole(String.Format(" - FRS at: frs://{2}:{0}{1}", OnlyFRSPort, urlPath, infoIP));
            while (isRunning)
            {
                try
                {
                    TcpClient client = frsListener.AcceptTcpClient();
                    GetClient(client, this.OnlyFRSPort);
                }
                catch { };
                Thread.Sleep(10);
            };
        }

        private void PingNearestThread()
        {
            ushort pingInterval = 0;
            ushort nrstInterval = 0;
            while (isRunning)
            {
                if (pingInterval++ == 300) // 30 sec
                {
                    pingInterval = 0;
                    try { PingAlive(); }
                    catch { };
                };
                if (nrstInterval++ == 450) // 45 sec
                {
                    nrstInterval = 0;
                    try { SendNearest(); }
                    catch { };
                };
                Thread.Sleep(100);
            };
        }

        public void Stop()
        {
            if (!isRunning) return;

            WriteToConsole("Stopping... ");
            isRunning = false;

            if (aprsgw != null) aprsgw.Stop();
            aprsgw = null;

            if (BUDS != null) BUDS.SaveToTempFile();
            BUDS.Dispose();
            BUDS = null;

            if (mainListener != null) mainListener.Stop();
            mainListener = null;
            if (listenThread != null) listenThread.Join();
            listenThread = null; 

            if (aprsListener != null) aprsListener.Stop();
            aprsListener = null;
            if (aprsThread != null) aprsThread.Join();
            aprsThread = null;

            if (aisListener != null) aisListener.Stop();
            aisListener = null;
            if (aisThread != null) aisThread.Join();
            aprsThread = null;

            if (httpListener != null) httpListener.Stop();
            httpListener = null;
            if (httpThread != null) httpThread.Join();
            httpThread = null;

            if (frsListener != null) frsListener.Stop();
            frsListener = null;
            if (frsThread != null) frsThread.Join();
            frsThread = null;

            threadsStarted = 0;
        }

        private void PingAlive()
        {
            string pingmsg = "# " + softshort + "\r\n";
            byte[] pingdata = Encoding.ASCII.GetBytes(pingmsg);
            BroadcastAPRS(pingdata);
            Send2Air(null, pingdata);

            SafetyRelatedBroadcastMessage sbm = new SafetyRelatedBroadcastMessage("PING, " + OruxPalsServer.softshort.ToUpper());
            string txt = sbm.ToPacketFrame() + "\r\n";
            byte[] ret = Encoding.ASCII.GetBytes(sbm.ToPacketFrame() + "\r\n");
            BroadcastAIS(ret);

            PingFRS();
        }

        private void GetClient(TcpClient Client, int Port)
        {
            // check Black List
            string ip = ((IPEndPoint)Client.Client.RemoteEndPoint).Address.ToString();
            if (BlackListIP.Contains(ip)) return;

            ClientData cd = new ClientData(new Thread(ClientThread), Client, ++clientCounter);
            cd.ServerPort = Port;
            lock(clientList) clientList.Add(cd.id, cd);
            cd.thread.Start(cd);            
        }

        private void ClientThread(object param)
        {
            ClientData cd = (ClientData)param;

            // predefined APRS by OnlyAPRSPort
            if (cd.ServerPort == OnlyAPRSPort)
            {
                cd.state = 106;
                string welcome = "# " + softshort;
                byte[] ret = Encoding.ASCII.GetBytes(welcome + "\r\n");
                try { cd.stream.Write(ret, 0, ret.Length); cd.stream.Flush(); }
                catch { };
            };
            // predefined AIS by OnlyAISPort
            if (cd.ServerPort == OnlyAISPort)
            {
                SafetyRelatedBroadcastMessage sbm = new SafetyRelatedBroadcastMessage("WELCOME, AIS, " + OruxPalsServer.softshort.ToUpper());
                byte[] ret = Encoding.ASCII.GetBytes(sbm.ToPacketFrame() + "\r\n");
                cd.stream.Flush();
                cd.stream.Write(ret, 0, ret.Length);
                cd.state = 1;
                OnAISClient(cd);
            };
            // predefined HTTP by OnlyHTTPPort
            if (cd.ServerPort == OnlyHTTPPort) cd.state = 100;
            // predefined FRS by OnlyFRSPort
            if (cd.ServerPort == OnlyFRSPort) cd.state = 105;

            string rxText = "";
            byte[] rxBuffer = new byte[4096];
            int rxCount = 0;
            List<byte> rx8Buffer = new List<byte>();
            int rxAvailable = 0;
            int waitNDCounter = 55; // 5.5 sec, after 3 seconds - welcome

            while (Running && cd.thread.IsAlive && IsConnected(cd.client))
            {
                // if AIS, APRS or WebSocket
                if (((cd.state == 1) || (cd.state == 6) || (cd.state == 8) || (cd.state == 9)) && (DateTime.UtcNow.Subtract(cd.connected).TotalMinutes >= MaxClientAlive)) break;                

                // not yet defined in 10 seconds ??? - good bye
                if (((cd.state == 0) || (cd.state >= 100)) && (DateTime.UtcNow.Subtract(cd.connected).TotalSeconds > 10)) break;
                
                // AIS Client, APRS Read Only, Traccar Manager -- No Need to Receive Any Data
                if ((cd.state == 1) || (cd.state == 6) || (cd.state == 9)) { Thread.Sleep(1000); continue; };                

                // Detect AIS client on Main Port
                if ((!disableAIS) && (cd.state == 0))
                {
                    // After 2 seconds write AIS Welcome message
                    // OruxMaps from version OruxMaps7.0.0rc7.apk supports APRS, but identity packet sends 
                    //   only when any data received from server // client save 3.5 sec to identify
                    if (waitNDCounter == 25)
                    {
                        SafetyRelatedBroadcastMessage sbm = new SafetyRelatedBroadcastMessage("WELCOME, AIS/APRS, " + OruxPalsServer.softshort.ToUpper());
                        byte[] ret = Encoding.ASCII.GetBytes(sbm.ToPacketFrame() + "\r\n");
                        cd.stream.Write(ret, 0, ret.Length);
                    };

                    // After 5.5 seconds if no data
                    if (waitNDCounter-- == 0)
                    {
                        cd.state = 1;
                        OnAISClient(cd);
                    };
                };

                // Incoming Packet Available
                try { rxAvailable = cd.client.Client.Available; } catch { break; };

                // Read Incoming Data
                while (rxAvailable > 0)
                {
                    try { rxAvailable -= (rxCount = cd.stream.Read(rxBuffer, 0, rxBuffer.Length > rxAvailable ? rxAvailable : rxBuffer.Length)); }
                    catch { break; };
                    if (rxCount > 0) rxText += Encoding.ASCII.GetString(rxBuffer, 0, rxCount);
                    if ((rxCount > 0) && (cd.state == 8))
                    {
                        byte[] b2a = new byte[rxCount];
                        Array.Copy(rxBuffer, b2a, rxCount);
                        rx8Buffer.AddRange(b2a);
                    };
                };

                // PARSE INCOMING DATA //
                try
                {
                    // predefined APRS
                    if ((cd.state == 106) && (rxText.Length >= 4) && (rxText.IndexOf("\n") > 0) && (rxText.IndexOf("user") == 0))
                    {
                        if (OnAPRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                            rxText = "";
                        else
                            break;
                    };

                    // predefined FRS
                    if ((cd.state == 100) && (rxText.Length >= 4) && (rxText.IndexOf("\n") > 0) && (rxText.IndexOf("$FRPAIR") == 0))
                    {
                        if (OnFRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                            rxText = "";
                        else
                            break;
                    };

                    // predefined HTTP
                    if ((cd.state == 100) && (rxText.Length >= 4) && (rxText.IndexOf("\n") > 0))
                    {
                        if (rxText.IndexOf("GET") == 0) // GPSGate, Browser, WebSocket, OsmAnd, Traccar
                        {
                            OnGet(cd, rxText);
                            rxText = "";
                        }
                        else if (rxText.IndexOf("POST") == 0) // MapMyTracks, BigBrotherGPS, OsmAnd, Traccar, OwnTracks
                        {
                            OnPost(cd, rxText);
                            rxText = "";
                        }
                        else
                            break;
                    };

                    // Identificate Browser, GPSGate, MapMyTracks (HTTP), APRS, FRS Client or WebSocket //
                    if ((cd.state == 0) && (rxText.Length >= 4) && (rxText.IndexOf("\n") > 0))
                    {
                        if (rxText.IndexOf("GET") == 0) // GPSGate, Browser, WebSocket, OsmAnd, Traccar
                        {
                            OnGet(cd, rxText);
                            rxText = "";
                        }
                        else if (rxText.IndexOf("POST") == 0) // MapMyTracks, BigBrotherGPS, OsmAnd, Traccar, OwnTracks
                        {
                            OnPost(cd, rxText);
                            rxText = "";
                        }
                        else if (rxText.IndexOf("user") == 0) // APRS
                        {
                            if (OnAPRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else if (rxText.IndexOf("$FRPAIR") == 0) // FRS
                        {
                            if (OnFRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else
                            break;
                    };

                    // On Web Socket Data
                    if((cd.state == 8) && (rx8Buffer.Count > 0))
                    {
                        rxText = GetStringFromWebSocketFrame(rx8Buffer.ToArray(), rx8Buffer.Count);
                        if(rxText != "")
                            OnWebSocketMessage(cd, rxText);
                        rx8Buffer.Clear();
                        rxText = "";
                    };

                    // Receive incoming data from identificated clients only
                    if ((cd.state > 0) && (cd.state < 100) && (rxText.Length > 0) && (rxText.IndexOf("\n") > 0))
                    {
                        // Verified APRS Client //
                        if (cd.state == 4)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnAPRSData(cd, line);
                        };

                        // FRS Client //
                        if (cd.state == 5)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnFRSData(cd, line);
                        };

                        // AFSKMODEM
                        if (cd.state == 7)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnAir(cd, line);
                        };                 

                        rxText = "";
                    };
                }
                catch (Exception ex) { Console.WriteLine(ex.ToString()); };

                Thread.Sleep(100);
            };

            lock (clientList) clientList.Remove(cd.id);
            cd.client.Close();
            try { cd.thread.Abort(); } catch { };
        }

        private void OnAISClient(ClientData cd)
        {            
            if (BUDS == null) return;

            Buddie[] bup = BUDS.Current;
            List<byte[]> blist = new List<byte[]>();
            foreach (Buddie b in bup) 
                if((b.source != 5) && (b.source != 6)) // no tx everytime & static objects
                    blist.Add(b.AISNMEA);
            foreach (byte[] ba in blist)
                try { cd.stream.Write(ba, 0, ba.Length); } catch { };
        }

        private bool OnAPRSClient(ClientData cd, string loginstring)
        {
            #if DEBUGORUX
            WriteLineToConsole(String.Format("> " + loginstring));
            #endif

            string res = "# logresp user unverified, server " + serviceName.ToUpper();

            Match rm = Regex.Match(loginstring, @"^user\s([\w\-]{3,})\spass\s([\d\-]+)\svers\s([\w\d\-.]+)\s([\w\d\-.\+]+)");
            if (rm.Success)
            {
                string callsign = rm.Groups[1].Value.ToUpper();

                if (banlist.Contains(callsign))
                {
                    cd.client.Close();
                    return false;
                };

                string password = rm.Groups[2].Value;
                //string software = rm.Groups[3].Value;
                //string version = rm.Groups[4].Value;
                string doptext = loginstring.Substring(rm.Groups[0].Value.Length).Trim();

                if (doptext.IndexOf("filter") >= 0)
                    cd.SetFilter(doptext.Substring(doptext.IndexOf("filter") + 7));
                
                int psw = -1;
                int.TryParse(password, out psw);
                // check for valid HAM user or for valid OruxPalsServer user
                // these users can upload data to server
                if ((psw == APRSData.CallsignChecksum(callsign)) || (psw == Buddie.Hash(callsign)))
                {
                    if (callsign == "AFSKMODEM")
                    {
                        cd.state = 7; // AFSKMODEM
                        cd.user = callsign;

                        res = "# logresp " + callsign + " verified, server " + serviceName.ToUpper();
                        byte[] aret = Encoding.ASCII.GetBytes(res + "\r\n");
                        try { cd.stream.Write(aret, 0, aret.Length); }
                        catch { };

                        return true;
                    };

                    cd.state = 4; //APRS
                    cd.user = callsign; // .user - valid username for callsign

                    /* SEARCH REGISTERED as Global APRS user*/
                    if ((regUsers != null) && (regUsers.Length > 0))
                        foreach (OruxPalsServerConfig.RegUser u in regUsers)
                            if ((u.services != null) && (u.services.Length > 0))
                                foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                    if (svc.names.Contains("A"))
                                        if (callsign == svc.id)
                                            cd.user = u.name;

                    // remove ssid, `-` not valid symbol in name
                    if (cd.user.Contains("-")) cd.user = cd.user.Substring(0, cd.user.IndexOf("-"));

                    res = "# logresp " + callsign + " verified, server " + serviceName.ToUpper();
                    byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                    try { cd.stream.Write(ret, 0, ret.Length); }
                    catch { };

                    SendBuddies(cd);
                    return true;
                };
            };

            // Invalid user
            // these users cannot upload data to server (receive data only allowed)
            {
                cd.state = 6; // APRS Read-Only
                byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                try { cd.stream.Write(ret, 0, ret.Length); }
                catch { };

                SendBuddies(cd);
                return true;
            };
        }

        // on verified users // they can upload data to server
        private void OnAPRSData(ClientData cd, string line)
        {
            #if DEBUGORUX
            WriteLineToConsole(String.Format("> " + line));
            #endif

            // COMMENT STRING
            if (line.IndexOf("#") == 0)
            {
                string filter = "";
                if (line.IndexOf("filter") > 0) filter = line.Substring(line.IndexOf("filter"));
                // filter ... active
                if (filter != "")
                {
                    string fres = cd.SetFilter(filter.Substring(7));
                    string resp = "# filter '" + fres + "' is active\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(resp);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { }
                };
                return;
            };
            if (line.IndexOf(">") < 0) return;            
            
            // PARSE NORMAL PACKET
            Buddie b = APRSData.ParseAPRSPacket(line);
            if ((b != null) && (b.name != null) && (b.name != String.Empty))
            {
                bool forward2aprs = false;

                if((regUsers != null) && (regUsers.Length > 0))
                    foreach (OruxPalsServerConfig.RegUser u in regUsers)
                    {                        
                        if ((!callsignToUser) && (u.name == b.name)) b.regUser = u; // packet callsign as userName
                        if (callsignToUser && (u.name == cd.user)) b.regUser = u; // client logon with userName

                        if ((u.services != null) && (u.services.Length > 0))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if (svc.names.Contains("A") && (b.name == svc.id)) // client logon with Callsign
                                {
                                    b.regUser = u;
                                    // can forward to global only if user logon with callsign, and forward set to A
                                    forward2aprs = (u.forward != null) && (u.forward.Contains("A")); 
                                };                        
                    };

                // 2 SERVER MESSAGE
                // messages for server no need to save, broadcast & forward
                try { if (line.IndexOf("::") > 0) if (OnAPRSinternalMessage(cd, b, line)) return; }
                catch { };
                
                // Direct Forward if user logon with global Callsign (see xml comment) //
                if (forward2aprs && (aprsgw != null) && (aprscfg.aprs2global == "yes"))
                {
                    string broadcastline = b.APRS;
                    // if no comment passed set comment from registered user info
                    if ((b.regUser != null) && (b.PositionIsValid) && ((b.parsedComment == null) || (b.parsedComment == String.Empty)) && ((b.regUser.comment != null) && ((b.regUser.comment != String.Empty))))
                        broadcastline = line + " " + b.regUser.comment + "\r\n";
                    aprsgw.SendCommand(broadcastline);
                };
                
                // if status
                if ((line.IndexOf(":>") > 0) && (BUDS != null))
                    BUDS.UpdateStatus(b, line.Substring(line.IndexOf(":>")+2));

               // If callsignToUser is `yes` and
               // if packet sender is not user name (for registered users) or not as logged in name
               // for all packets will set sender as user name (for registered users) or as logged in name
               // If callsignToUser is `no` - verified user can send packets from any sender as is (no replacement)                               
               if ((callsignToUser) && (b.name != cd.user))
               {
                   b.APRS = b.APRS.Replace(b.name + ">", cd.user + ">");
                   b.APRSData = Encoding.ASCII.GetBytes(b.APRS);
                   b.name = cd.user;
               };

               if ((b.PositionIsValid)) // if Position Packet send to All (AIS + APRS + Web)
               {
                   OnNewData(b);
                   cd.lastFixYX = new double[3] { 1, b.lat, b.lon };                   
               }
               else // if not Position Packet send as is only to all APRS clients
                   Broadcast(b.APRSData, b.name, false, true, false, false);

               // broadcast to air
               Send2Air(cd, b.APRSData);               
            };
        }
        
        private void OnAir(ClientData cd, string line)
        {
            //WriteLineToConsole(String.Format("AIR> " + line));

            if (line.IndexOf("#") == 0) return;
            
            int id = line.IndexOf(":");
            int ip = line.IndexOf(">");
            if (id > 0)
            {
                string IGate = "ORXPLS-GW";
                if ((aprscfg != null) && (aprscfg.user != null) && (aprscfg.user != String.Empty)) IGate = aprscfg.user;
                line = line.Insert(line.IndexOf(":"), (id == (ip + 1) ? "qAR" : ",qAR") + "," + IGate);
            };
            
            // PARSE NORMAL PACKET
            Buddie b = APRSData.ParseAPRSPacket(line);

            // 2 SERVER MESSAGE
            // messages for server no need to save, broadcast & forward
            try { if (line.IndexOf("::") > 0) if (OnAPRSinternalMessage(cd, b, line)) return; }
            catch { };

            // Direct Forward to Global
            if ((aprsgw != null) && (aprscfg.aprs2global == "yes"))
                aprsgw.SendCommand(b.APRS);

            // if status
            if ((line.IndexOf(":>") > 0) && (BUDS != null))
                BUDS.UpdateStatus(b, line.Substring(line.IndexOf(":>") + 2));

            if ((b.PositionIsValid)) // if Position Packet send to All (AIS + APRS + Web)
                OnNewData(b);
            else // if not Position Packet send as is only to all APRS clients
                Broadcast(b.APRSData, b.name, false, true, false, true);

            // broadcast to air
            Send2Air(cd, b.APRSData);
        }

        private void Send2Air(ClientData sender, byte[] aprs)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if ((cd.state == 7) && ((sender == null) || (cd.id != sender.id)))
                        cdlist.Add(cd);
                };

            foreach (ClientData cd in cdlist)
                try { cd.client.GetStream().Write(aprs, 0, aprs.Length); }
                catch { };
        }

        private void SendBuddies(ClientData cd)
        {
            if (BUDS != null)
            {
                Buddie[] bup = BUDS.Current;
                List<byte[]> blist = new List<byte[]>();
                foreach (Buddie b in bup)
                {
                    if ((b.source == 5) && (cd.filter != null) && (cd.filter.inMyRadiusKM == -1))
                        continue;
                    if((b.source != 5) && (b.name != cd.user))
                        if((cd.filter != null) && (!cd.filter.PassName(b.name))) 
                            continue;
                    if (sendBack || (cd.state == 6) || (b.source == 5))
                        blist.Add(b.APRSData);
                    else if (b.name != cd.user)
                        blist.Add(b.APRSData);
                };
                foreach (byte[] ba in blist)
                    try { cd.stream.Write(ba, 0, ba.Length); }
                    catch { };
            };
        }

        private void SendNearest()
        {
            List<ClientData> cdl = new List<ClientData>();

            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                    if (ci.state == 4)
                        cdl.Add(ci);

            if (cdl.Count > 0)
                if (BUDS != null)
                    foreach (ClientData ci in cdl)
                        if ((ci.lastFixYX != null) && (ci.lastFixYX.Length == 3) && ((int)ci.lastFixYX[0] == 1))
                        {
                            if ((ci.filter == null) || (ci.filter.inMyRadiusKM > 0) || (ci.filter.inMyRadiusKM < -1)) // -2 - not specified
                            {
                                PreloadedObject[] nearest = BUDS.GetNearest(ci.lastFixYX[1], ci.lastFixYX[2], ci.filter);
                                if ((nearest != null) && (nearest.Length > 0))
                                    foreach (PreloadedObject near in nearest)
                                    {
                                        byte[] bts = Encoding.ASCII.GetBytes(near.ToString());
                                        try { ci.stream.Write(bts, 0, bts.Length); }
                                        catch { };
                                    };
                            };
                            ci.lastFixYX = new double[] { 0, 0, 0 };
                        };
        }

        private bool OnAPRSinternalMessage(ClientData cd, Buddie buddie, string line)
        {
            string frm = line.Substring(0, line.IndexOf(">"));
            string msg = line.Substring(line.IndexOf("::") + 12).Trim();

            bool sendack = false;
            byte[] tosendack = new byte[0];

            // FIND USER, callsign of the packet must be within registered users //
            OruxPalsServerConfig.RegUser cu = null;
            if ((frm != "") && (regUsers != null))
                foreach (OruxPalsServerConfig.RegUser u in regUsers)
                {
                    if (frm == u.name)
                    {
                        cu = u;
                        break;
                    };
                    if (u.services != null)
                        foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                            if (svc.names.Contains("A") && (svc.id == frm))
                            {
                                cu = u;
                                break;
                            };
                };

            if (line.IndexOf("::ORXPLS-GW:") > 0) // ping & forward
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                if ((msg != null) && (msg != String.Empty))
                {
                    string[] ms = msg.ToUpper().Split(new string[] { " " }, StringSplitOptions.None);
                    if (ms == null) return true;
                    if (ms.Length == 0) return true;

                    if (ms[0] == "FORWARD") // forward only for registered users
                    {                        
                        if (cu == null)
                        {
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": no forward privileges\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        }
                        else
                        {
                            if (ms.Length > 1)
                                cu.forward = ms[1].ToUpper();
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": forward set to `" + (cu.forward == null ? "" : cu.forward) + "`\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        };
                    }
                    else if ((ms[0] == "KILL") && (ms.Length > 1)) // kill only for registered users
                    {
                        string u2k = ms[1].ToUpper();
                        if (cu == null)
                        {
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": no kill privileges\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        }
                        else
                        {
                            bool ok = false;
                            if (BUDS != null) ok = BUDS.Kill(u2k);
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": kill `" + u2k + "` "+(ok ? "OK" : "FAILED")+"\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        };
                    }
                    else if ((ms[0] == "POSITION") && (ms.Length > 3)) // Emulate Position
                    {
                        string geoWho = ms[1].ToUpper().Trim();
                        string geoLat = ms[2].ToUpper().Trim();
                        string geoLon = ms[3].ToUpper().Trim();
                        if (cu == null)
                        {
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": no position privileges\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        }
                        else
                        {                            
                            double lat = 0;
                            double lon = 0;
                            bool ok = (double.TryParse(geoLat, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat)) && (double.TryParse(geoLon, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon));
                            if (ok)
                            {
                                if (BUDS == null)
                                    ok = false;
                                else
                                {
                                    Buddie b = new Buddie(0, geoWho, lat, lon, 0, 0);
                                    b.lastPacket = "MANUAL";
                                    OnNewData(b);
                                };
                            };
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": position `" + geoWho + "` " + (ok ? "OK" : "FAILED") + "\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        };
                    }
                    else if ((ms[0] == adminName.ToUpper()) && (ms.Length > 1)) // hashsum for any user that know admin
                    {
                        string res = "";
                        for (int i = 1; i < ms.Length; i++) res += "u " + ms[i] + " a " + APRSData.CallsignChecksum(ms[i]) + " p " + Buddie.Hash(ms[i]) + " ";
                        string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": " + res + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        try { cd.stream.Write(bts, 0, bts.Length); }
                        catch { };
                    };
                };
            };

            if (line.IndexOf("::ORXPLS-ST:") > 0) // global status only for APRS global users
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                cu = null;
                string id = "";
                if ((regUsers != null) && (aprsgw != null) && (aprscfg.aprs2global == "yes"))
                    foreach (OruxPalsServerConfig.RegUser u in regUsers)                    
                        if ((u.forward != null) && (u.forward.Contains("A")) && (u.services != null))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if (svc.names.Contains("A") && (svc.id == frm))
                                {
                                    cu = u;
                                    id = svc.id;
                                    break;
                                };

                if ((msg != null) && (msg != String.Empty))
                {
                    BUDS.UpdateStatus(buddie, msg);
                    if (id != "")
                    {
                        aprsgw.SendCommand(id + ">APRS,TCPIP*:>" + msg + "\r\n");

                        string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": " + msg + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        try { cd.stream.Write(bts, 0, bts.Length); }
                        catch { };
                    }
                    else if((buddie != null) && (buddie.regUser != null))
                    {                        
                        string cmd2s = buddie.regUser.name + ">APRS,TCPIP*:>" + msg + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        BroadcastAPRS(bts);
                    };
                }
                else
                {
                    string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": FAILED\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                };
            };

            if (line.IndexOf("::ORXPLS-CM:") > 0) // comment
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                msg = msg.Trim();
                bool updated = false;
                
                if ((msg != null) && (msg != String.Empty) && (msg != "?") && (buddie != null))
                {
                    if (BUDS != null)
                        updated = BUDS.UpdateComment(buddie, msg);
                    if ((!updated) && (buddie.regUser != null))
                    {
                        buddie.regUser.comment = msg;
                        updated = true;
                    };
                };
                if (updated)
                {
                    string cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": " + msg + "\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                }
                else
                {
                    string cmd2s = "";
                    if ((msg == "?") && (buddie != null) && (buddie.regUser != null))
                        cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": " + buddie.regUser.comment + "\r\n";
                    else
                        cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": User not found\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                };
            };

            if (sendack && (tosendack.Length > 0))
                try { cd.stream.Write(tosendack, 0, tosendack.Length); }
                catch { };

            return sendack;
        }

        private void OnGlobalAPRSData(string line)
        {
            if (aprscfg.global2aprs == "yes")
                BroadcastAPRS(Encoding.ASCII.GetBytes(line+"\r\n"), true);

            if ((aprscfg.global2ais == "yes") || (aprscfg.global2frs == "yes"))
            {
                Buddie b = APRSData.ParseAPRSPacket(line);
                if ((b != null) && (b.name != null) && (b.name != String.Empty) && (b.PositionIsValid))                    
                {
                    if (aprscfg.global2ais == "yes")
                    {
                        b.SetAIS();
                        b.green = true;
                        BroadcastAIS(b.AISNMEA);
                    };
                    if (aprscfg.global2frs == "yes")
                    {
                        b.SetFRS();
                        BroadcastFRS(b.FRPOSData);
                    };
                };
            };
        }

        private bool OnFRSClient(ClientData cd, string pairstring)
        {
            byte[] ba = Encoding.ASCII.GetBytes("# " + softshort + "\r\n");
            try { cd.stream.Write(ba, 0, ba.Length); } catch { };

            Match rx;
            if ((rx = Regex.Match(pairstring, @"^(\$FRPAIR),([\w\+]+),(\w+)\*(\w+)$")).Success)
            {
                string phone = rx.Groups[2].Value;
                // string imei = rx.Groups[3].Value;
                if(regUsers != null) 
                    foreach(OruxPalsServerConfig.RegUser u in regUsers)
                        if (u.phone == phone)
                        {
                            cd.state = 5;
                            cd.user = u.name;

                            // Welcome message
                            string pmsg = ChecksumAdd2Line("$FRCMD," + u.name + ",_Ping,Inline");
                            byte[] data = Encoding.ASCII.GetBytes(pmsg + "\r\n");
                            try { cd.client.GetStream().Write(data, 0, data.Length); }
                            catch { };

                            if (!_NoSendToFRS)
                            {
                                if (BUDS != null)
                                {
                                    Buddie[] bup = BUDS.Current;
                                    List<byte[]> blist = new List<byte[]>();
                                    foreach (Buddie b in bup)
                                    {
                                        if ((b.source != 5) && (b.source != 6)) // no tx everytime & static objects
                                            continue;
                                        if (sendBack)
                                            blist.Add(b.FRPOSData);
                                        else if (cd.user != b.name)
                                            blist.Add(b.FRPOSData);
                                    };
                                    foreach (byte[] ba2 in blist)
                                        try { cd.stream.Write(ba2, 0, ba2.Length); }
                                        catch { };
                                };
                            };

                            return true;
                        };
            };
            return false;
        }

        private void OnFRSData(ClientData cd, string line)
        {
            Match rx; 

            if ((rx = Regex.Match(line, @"^(\$FRCMD),(\w*),(\w+),(\w*),?([\w\s.,=]*)\*(\w{2})$")).Success)
            {
                string resp = "";

                // _ping
                if (rx.Groups[3].Value.ToLower() == "_ping")
                   resp = ChecksumAdd2Line("$FRRET," + rx.Groups[2].Value + ",_Ping,Inline");
                
                // _sendmessage
                if (rx.Groups[3].Value.ToLower() == "_sendmessage")
                {
                   string val = rx.Groups[4].Value;
                   string val2 = rx.Groups[5].Value;
                   resp = ChecksumAdd2Line("$FRRET," + rx.Groups[2].Value + ",_SendMessage,Inline");
                   
                   // 0000.00000,N,00000.00000,E,0.0,0.000,0.0,190117,122708.837,0,BatteryLevel=78
                   // 0000.00000,N,00000.00000,E,0.0,0.000,0.0,190117,122708.837,0,Temperature=-2
                   // DDMM.mmmm,N,DDMM.mmmm,E,AA.a,SSS.ss,HHH.h,DDMMYY,hhmmss.dd,fixOk,NOTE*xx

                   Match rxa = Regex.Match(val2, @"^(\d{4}.\d+),(N|S),(\d{5}.\d+),(E|W),([0-9.]*),([0-9.]*),([0-9.]*),(\d{6}),([0-9.]{6,}),([\w.\s=]),([\w.\s=\-,]*)$");
                   if (rxa.Success)
                   {
                       string sFix = sFix = rxa.Groups[10].Value;
                       if (sFix == "1")
                       {
                           string sLat = rxa.Groups[1].Value;
                           string lLat = rxa.Groups[2].Value;
                           string sLon = rxa.Groups[3].Value;
                           string lLon = rxa.Groups[4].Value;
                           string sSpeed = rxa.Groups[6].Value;
                           string sHeading = rxa.Groups[7].Value;

                           double rLat = double.Parse(sLat.Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                           rLat = double.Parse(sLat.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + rLat / 60;
                           if (lLat == "S") rLat *= -1;

                           double rLon = double.Parse(sLon.Substring(3, 7), System.Globalization.CultureInfo.InvariantCulture);
                           rLon = double.Parse(sLon.Substring(0, 3), System.Globalization.CultureInfo.InvariantCulture) + rLon / 60;
                           if (lLon == "W") rLon *= -1;


                           double rHeading = double.Parse(sHeading, System.Globalization.CultureInfo.InvariantCulture);
                           double rSpeed = double.Parse(sSpeed, System.Globalization.CultureInfo.InvariantCulture) * 1.852;

                           Buddie b = new Buddie(4, cd.user, rLat, rLon, (short)Math.Round(rSpeed), (short)Math.Round(rHeading));
                           b.lastPacket = line;
                           OnNewData(b);
                       };
                   };
               };

               if (resp != "")
               {
                   byte[] ba = Encoding.ASCII.GetBytes(resp + "\r\n");
                   try { cd.stream.Write(ba, 0, ba.Length); }
                   catch { };
               };
            };
        }

        private void OnWebSocketMessage(ClientData cd, string rxText)
        {
            if (String.IsNullOrEmpty("rxText")) return;

            if (rxText == "ping")
            {
                byte[] pa = GetWebSocketFrameFromString("[]");
                cd.client.GetStream().Write(pa, 0, pa.Length);
                return;
            };

            string json = API_getList(rxText.Trim().ToUpper());
            byte[] ba = GetWebSocketFrameFromString(json);
            cd.client.GetStream().Write(ba, 0, ba.Length);
        }


        // Init WebSocket Connection
        private void OnWebSocketInit(ClientData cd, string query, string rxText)
        {
            //int hi = rxText.IndexOf("HTTP");
            //if (hi <= 0) { throw new Exception("NOT VALID REQUEST"); };
            //string query = rxText.Substring(4, hi - 4).Trim();

            int asp = query.IndexOf("?");
            System.Collections.Specialized.NameValueCollection ask = new System.Collections.Specialized.NameValueCollection();
            if (asp > 0)
            {
                ask = HttpUtility.ParseQueryString(query.Substring(asp));
                query = query.Remove(asp);
            };

            string swk = Regex.Match(rxText, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
            string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
            string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

            byte[] response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");
            cd.client.GetStream().Write(response, 0, response.Length);
            cd.state = 8;

            string json = API_getList(null, (ask["hide"] != null) && (ask["hide"] == "virtual") ? false : true);
            byte[] ba = GetWebSocketFrameFromString(json);
            cd.client.GetStream().Write(ba, 0, ba.Length);

            return;
        }

        private Dictionary<string, DateTime> regSSIDS = new Dictionary<string, DateTime>();
        private static string GetCookiesSSID(string rxText)
        {
            string ssid = Guid.NewGuid().ToString().ToLower();
            int ci = rxText.IndexOf("ookie: ");
            if (ci > 0)
            {
                int si = rxText.IndexOf("sid=", ci);
                if (si > 0)
                {
                    int ni = rxText.IndexOf("\n", si);
                    ssid = rxText.Substring(si + 4, ni - si - 4).Trim('\r').Trim();
                };
            };
            return ssid;
        }
        private void RegisterCookiesSSID(string ssid)
        {
            lock (regSSIDS) // register ssid
            {
                if (regSSIDS.ContainsKey(ssid))
                    regSSIDS[ssid] = DateTime.UtcNow;
                else
                    regSSIDS.Add(ssid, DateTime.UtcNow);

                List<string> k2r = new List<string>();
                foreach (KeyValuePair<string, DateTime> sd in regSSIDS)
                    if (DateTime.UtcNow.Subtract(sd.Value).TotalHours > 168)
                        k2r.Add(sd.Key);

                foreach (string key in k2r)
                    regSSIDS.Remove(key);                
            };
        }

        private void OnTraccarAPI(ClientData cd, string method, string query, string rxText)
        {
            string ssid = GetCookiesSSID(rxText);            

            /* // No Server Info
            if (query == "/api/server")
            {
                string json = "{";
                json += "\"id\":1,\"registration\":true,\"readonly\":true,\"deviceReadonly\":true,\"limitCommands\":false,";
                json += "\"map\":\"\",\"bingKey\":\"\",\"mapUrl\":\"\",\"poiLayer\":\"\",";
                json += "\"latitude\":55.5,\"longitude\":37.5,\"zoom\":8,";
                json += "\"twelveHourFormat\":false,\"version\":\"48\",\"forceSettings\":false,\"coordinateFormat\":\"\",\"attributes\":{}";
                json += "}";
                HTTPClientSendJSON(cd.client, json);
                return;
            };
            */

            // Authorizate user
            if (query == "/api/session")
            {
                int bb = rxText.IndexOf("\r\n\r\n");
                string user = "Traccar Viewer";
                string pass = "";
                if (bb > 0)
                {
                    System.Collections.Specialized.NameValueCollection ask = HttpUtility.ParseQueryString(rxText.Substring(bb + 4));
                    user = (ask["email"] != null) ? ask["email"].ToUpper() : "";
                    pass = (ask["password"] != null) ? ask["password"] : "";
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendError(cd.client, 401); return; };
                    if (Buddie.Hash(user).ToString() != pass) { HTTPClientSendError(cd.client, 401); return; };
                    RegisterCookiesSSID(ssid);
                };

                string json = "{";
                json += "\"id\":1,\"name\":\"" + user + "\",\"email\":\"" + user + "\",\"readonly\":true,\"administrator\":false,";
                json += "\"map\":\"\",\"latitude\":55.5,\"longitude\":37.5,\"zoom\":8,\"password\":\"" + pass + "\",";
                json += "\"twelveHourFormat\":false,\"coordinateFormat\":\"\",\"disabled\":\"false\",";
                json += "\"expirationTime\":\"" + DateTime.UtcNow.AddMonths(12).ToString("yyyy-MM-ddTHH:mm:ss") + "Z\",";
                json += "\"deviceLimit\":9999,\"userLimit\":9999,\"deviceReadonly\":false,\"limitCommands\":false,";
                json += "\"poiLayer\":\"\",\"token\":\"TRACCAR_" + user + "\",";
                json += "\"attributes\":{}";
                json += "}";
                HTTPClientSendJSON(cd.client, json, "ssid=" + ssid);
                return;
            };

            // for authorizated users only
            if (regSSIDS.ContainsKey(ssid))
            {
                if (query == "/api/socket") // Get TraccarWebSocket (state = 9)
                {
                    string swk = Regex.Match(rxText, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                    string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                    byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
                    string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

                    // HTTP/1.1 defines the sequence CR LF as the end-of-line marker
                    byte[] response = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");
                    cd.client.GetStream().Write(response, 0, response.Length);
                    cd.state = 9;

                    string json = "{\"positions\": " + API_GetPositions4TraccarWebSocket(null) + ", \"devices\": " + API_GetDevices4TraccarWebSocket(null) + "}";
                    byte[] ba = GetWebSocketFrameFromString(json);
                    cd.client.GetStream().Write(ba, 0, ba.Length);
                    return;
                };

                if (query == "/api/devices") // Traccar Manager Get Device List over HTTP GET
                {
                    string json = API_GetDevices4TraccarWebSocket(null);
                    HTTPClientSendJSON(cd.client, json);
                    return;
                };

                if (query == "/api/positions") // Traccar Manager Get Positions List over HTTP GET
                {
                    string json = API_GetPositions4TraccarWebSocket(null);
                    HTTPClientSendJSON(cd.client, json);
                    return;
                };
            };

            // if Traccar Viewer 2.3
            if ((query == "/api/devices") || (query == "/api/positions"))
            {
                OnTraccarAPIAuth_TraccarViewer(cd, query.Substring(4), rxText, "GET");
                return;
            };

            HTTPClientSendError(cd.client, 404);
            return;
        }

		// https://learn.javascript.ru/websockets
		// http://tools.ietf.org/html/rfc6455
        public static string GetStringFromWebSocketFrame(byte[] buffer, int length)
        {
            if ((buffer == null) || (buffer.Length < 2)) return ""; // throw new Exception("The buffer length couldn't be less than 2 bytes");
            bool FIN = (buffer[0] & 0x80) == 0x80; // last packet?
            int OPCODE = buffer[0] & 0x0F; // packet type: 0 continue, 1 - Text Frame, 2 - Binary frame, 8 - Close, 9 - Ping, 10 - Pong                        
            bool MASKED = (buffer[1] & 0x80) == 0x80; // has mask?
            int dataLength = buffer[1] & 0x7F; // data length

            if (OPCODE != 1) return ""; // Not Text Frame;
			
            int nextIndex = 0;
            if (dataLength <= 125) // length here
            {
                nextIndex = 2; // [][] (M M M M) byte (no addit bytes length) 2/6
            }
            else if (dataLength == 126) // length next 2 bytes
            {
                if (buffer.Length < 4) return ""; // throw new Exception("The buffer length couldn't be less than 4 bytes");
                dataLength = (int)BitConverter.ToUInt16(new byte[] { buffer[3], buffer[2] }, 0);
                nextIndex = 4; // [][] X X (M M M M) byte (2 addit bytes length) 4/8
            }
            else if (dataLength == 127) // length next 8 bytes
            {
                if (buffer.Length < 10) return ""; // throw new Exception("The buffer length couldn't be less than 10 bytes");
                dataLength = (int)BitConverter.ToUInt64(new byte[] { buffer[9], buffer[8], buffer[7], buffer[6], buffer[5], buffer[4], buffer[3], buffer[2] }, 0);
                nextIndex = 10;// [][] X X X X X X X X (M M M M) byte (8 addit bytes length) 10/14
            };
                        
            int dataFrom = MASKED ? nextIndex + 4 : nextIndex;
            if ((dataFrom + dataLength) > length) return ""; //throw new Exception("The buffer length is smaller than the data length");
            if (MASKED)
            {
                byte[] mask = new byte[] { buffer[nextIndex], buffer[nextIndex + 1], buffer[nextIndex + 2], buffer[nextIndex + 3] };
                int byteNum = 0;
                int dataTill = dataFrom + dataLength;
                for (int i = dataFrom; i < dataTill; i++)
                    buffer[i] = (byte)(buffer[i] ^ mask[byteNum++ % 4]);
            };

            try
            {
                string res = Encoding.UTF8.GetString(buffer, dataFrom, dataLength);
                return res;
            }
            catch (Exception ex)
            {
                return "";
            };
        }

		// https://learn.javascript.ru/websockets
		// http://tools.ietf.org/html/rfc6455
        public static byte[] GetWebSocketFrameFromString(string Message)
        {
            if (String.IsNullOrEmpty(Message)) return null;
            
            Random rnd = new Random();
            byte[] BODY = Encoding.UTF8.GetBytes(Message);
            byte[] MASK = new byte[0]; // no mask
            // byte[] MASK = new byte[4] { (byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255) }; // new byte[0]
            int OPCODE = 1; // 1 - Text, 2 - Binary
            byte[] FRAME = null;

            int nextIndex = 0;
            if (BODY.Length < 126)
            {
                nextIndex = 2;
                FRAME = new byte[2 + MASK.Length + BODY.Length];
                FRAME[1] = (byte)((MASK.Length == 4 ? 0x80 : 0) + BODY.Length);                
            }
            else if (BODY.Length <= short.MaxValue)
            {
                nextIndex = 4;
                FRAME = new byte[4 + MASK.Length + BODY.Length];
                FRAME[1] = (byte)((MASK.Length == 4 ? 0x80 : 0) + 126);
                FRAME[2] = (byte)((BODY.Length >> 8) & 255);
                FRAME[3] = (byte)(BODY.Length & 255);
            }
            else
            {
                nextIndex = 10;
                FRAME = new byte[10 + MASK.Length + BODY.Length];
                FRAME[1] = (byte)((MASK.Length == 4 ? 0x80 : 0) + 127);
                ulong blen = (ulong)BODY.Length;
                FRAME[2] = (byte)((blen >> 56) & 255);
                FRAME[3] = (byte)((blen >> 48) & 255);
                FRAME[4] = (byte)((blen >> 40) & 255);
                FRAME[5] = (byte)((blen >> 32) & 255);
                FRAME[6] = (byte)((blen >> 24) & 255);
                FRAME[7] = (byte)((blen >> 16) & 255);
                FRAME[8] = (byte)((blen >> 08) & 255);
                FRAME[9] = (byte)(blen & 255);
            };
            FRAME[0] = (byte)(0x80 + OPCODE); // FIN + OPCODE
            if (MASK.Length == 4)
            {
                for (int mi = 0; mi < MASK.Length; mi++)
                    FRAME[nextIndex + mi] = MASK[mi];
                nextIndex += MASK.Length;
            };
            for (int bi = 0; bi < BODY.Length; bi++)
                FRAME[nextIndex + bi] = MASK.Length == 4 ? (byte)(BODY[bi] ^ MASK[bi % 4]) : BODY[bi];

            return FRAME;            
        }
                
        private void OnGet(ClientData cd, string rxText)
        {
            bool directHTTP = cd.state == 100;
            cd.state = 2;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) { HTTPClientSendError(cd.client, 400); return; };
            string query = rxText.Substring(4, hi - 4).Trim();
            if (!IsValidQuery(query))
            {
                if (query.StartsWith("/api")) { OnTraccarAPI(cd, "GET", query, rxText); return; };
                if ((query == "/") && (LocalNetwork.Count > 0))
                    foreach (string ln in LocalNetwork)
                        if ((new Regex(ln, RegexOptions.IgnoreCase)).Match(cd.IP).Success)
                        {
                            HTTPClientRedirect(cd.client, urlPath + (urlPath.EndsWith("/") ? "info" : "/info"));
                            return;
                        };
                if (directHTTP && (query == "/"))
                    HTTPClientSendError(cd.client, 501);
                if (directHTTP)
                    HTTPClientSendError(cd.client, 404);
                return;
            };
            switch(query[10])
            {
                case '$':
                    OnAdmin(cd, query.Substring(11), rxText);
                    return;
                case 'i':
                    OnBrowser(cd, query.Substring(11), rxText);
                    return;
                case '@':
                    OnCmd(cd, query.Substring(11));
                    return;
                //case 'm':
                //    OnMMT(cd, rxText);
                //    return;
                case 'v':
                    OnView(cd, query.Substring(11), rxText);
                    return;
                case 'f':
                    OnView(cd, query.Substring(11), rxText);
                    return;
                case 'o':
                    if ((query.Length > 16) && (query[11] == 'a') && (query[12] == '/'))
                        OnOsmAndTraccar(cd, query.Substring(13), rxText);
                    return;
                case 't': // traccar
                    if((query.Length > 17) && ((query.Substring(10,7).ToLower() == "traccar")))
                        OnTraccarAPIAuth_TraccarViewer(cd, query.Substring(17), rxText, "GET");
                    return;
                case 's':
                    if ((query.Length > 15) && ((query.Substring(10, 6).ToLower() == "socket")))
                        OnWebSocketInit(cd, query, rxText);
                    return;
                default:
                    HTTPClientSendError(cd.client, 403);
                    return;
            };
        }

        private string GetPageHTMLHeader()
        {
            return "<html>\r\n<head>\r\n<meta charset=\"windows-1251\"/>\r\n" +
                "<meta name=\"robots\" content=\"noindex, nofollow\"/>\r\n" +
                "<meta name=\"generator\" content=\"" + softver + "\">\r\n" +
                "<meta http-equiv=\"pragma\" content=\"no-cache\">\r\n" +
                "<meta http-equiv=\"content-language\" content=\"en-EN\">\r\n" +
                "<meta http-equiv=\"cache-control\" content=\"no-cache\">\r\n" + 
                "<meta name=\"author\" CONTENT=\"milokz@gmail.com\">\r\n" + 
                "<title>" + softver + "</title>\r\n</head>\r\n<body>";
        }

        private string GetPageHeader(byte p, bool isadmin)
        {
            string admc = isadmin ? "maroon" : "silver";
            string admt = isadmin ? "Admin Page" : "Log In";
            return String.Format(
                    "<span style=\"color:red;\">Server: {0}</span>\r\n<br/>" +
                    "<span style=\"color:maroon;\">Name: " + ServerName + "</span>\r\n<br/>" +
                    "Main Port: {4}\r\n<br/>" +
                    "Started: {1} UTC\r\n<br/>" +
                    "<a href=\"{5}info\">" + (p == 0 ? "<b>" : "") + "Main View" + (p == 0 ? "</b>" : "") + "</a> | "+
                    "<a href=\"{5}vusers\">" + (p == 5 ? "<b>" : "") + "List View" + (p == 5 ? "</b>" : "") + "</a> | " +
                    "<a href=\"{5}view\">" + (p == 1 ? "<b>" : "") + "Map View" + (p == 1 ? "</b>" : "") + "</a> | " +                   
                    "<a href=\"{5}vlmap\">" + (p == 7 ? "<b>" : "") + "Live Map" + (p == 7 ? "</b>" : "") + "</a> | " +                    
                    "<a href=\"{5}vlive\">" + (p == 6 ? "<b>" : "") + "Live List" + (p == 6 ? "</b>" : "") + "</a> | " +
                    "<a href=\"{5}v/resources\">" + (p == 2 ? "<b>" : "") + "Resources" + (p == 2 ? "</b>" : "") + "</a> | " +
                    "<a href=\"{5}v/objects\">" + (p == 3 ? "<b>" : "") + "Objects" + (p == 3 ? "</b>" : "") + "</a> " +
                    "<span style=\"color:silver;\">|</span> <a href=\"{5}$master\" style=\"color:" + admc + ";\">" + (p == 4 ? "<b>" : "") + admt + (p == 4 ? "</b>" : "") + "</a>\r\n<hr/>",
                    softver, started,  null, null, ListenPort, urlPath);
        }

        private void OnAdmin(ClientData cd, string query, string rxText)
        {
            string[] ss = query.Split(new char[] { '/' }, 2);
            if ((ss != null) && (ss[0] == "master"))
            {
                if (!IsAdmin(rxText))
                {
                    string hdr = GetPageHTMLHeader() + GetPageHeader(4, false) + "You Must Login First</body></html>";
                    SendAuthReq(cd.client, "Server Admin Authorization", hdr);
                    return;
                };

                string resp = GetPageHTMLHeader() + GetPageHeader(4, true);
                System.Collections.Specialized.NameValueCollection ask = new System.Collections.Specialized.NameValueCollection();
                int bb = rxText.IndexOf("\r\n\r\n");
                if (bb > 0) ask = HttpUtility.ParseQueryString(rxText.Substring(bb+4));
                {
                    string v_who = "";
                    if ((!ask.HasKeys()) && (query.IndexOf("?") >= 0))
                    {
                        System.Collections.Specialized.NameValueCollection qq = HttpUtility.ParseQueryString(query.Substring(query.IndexOf("?")));
                        if (qq["who"] != null) v_who = qq["who"];
                    };
                    string v_sel = "";
                    if ((ask.HasKeys()) && (ask["who"] != null)) v_who = ask["who"];
                    if ((ask.HasKeys()) && (ask["tochange"] != null)) v_sel = ask["tochange"];
                    resp += "<b>User Managment</b>:<form method=\"post\">" +
                        "<input style=\"width:200px;\" type=\"text\" name=\"who\" maxlength=\"15\" value=\""+v_who+"\"/><br/>" +
                        "<select style=\"width:200px;\" name=\"tochange\">"+                        
                        "<option value=\"justask\"" + (v_sel == "justask" ? " selected" : "") + ">Get User Information</option>" +
                        "<option disabled=\"disabled\">--- set user's info ---</option>" +
                        "<option value=\"icon\"" + (v_sel == "icon" ? " selected" : "") + ">Set User's Icon to ...</option>" +
                        "<option value=\"forward\""+(v_sel == "forward" ? " selected" : "")+">Set User's Forward to ...</option>"+
                        "<option value=\"status\"" + (v_sel == "status" ? " selected" : "") + ">Set User's Status to ...</option>" +
                        "<option value=\"comment\"" + (v_sel == "comment" ? " selected" : "") + ">Set User's Comment to ...</option>" +                        
                        "<option disabled=\"disabled\">--- emulate users ---</option>" +
                        "<option value=\"new\"" + (v_sel == "new" ? " selected" : "") + ">Create New User (Simulate)</option>" +
                        "<option value=\"emulate\"" + (v_sel == "emulate" ? " selected" : "") + ">Emulate User's Activity</option>" +
                        "<option disabled=\"disabled\">--- kill users ---</option>" +
                        "<option value=\"kill\"" + (v_sel == "kill" ? " selected" : "") + ">Kill User (*-all,?-unknown,r,t)</option>" +
                        "<option disabled=\"disabled\">--- preferences ---</option>" +
                        "<option value=\"setprop\"" + (v_sel == "setprop" ? " selected" : "") + ">Set Property (be careful)</option>" +
                        "</select><br/>" +
                        "<input style=\"width:200px;\" type=\"text\" name=\"newval\"/><br/>" +
                        "<input style=\"width:200px;\" type=\"submit\" value=\"Get Value or Set New\"/></form>";
                };
 
                if (ask.HasKeys())
                {
                    string who = (!String.IsNullOrEmpty(ask["who"])) ? ask["who"].Trim().ToUpper() : null;
                    if ((!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "setprop"))
                    {
                        if ((ask["newval"] != null) && (ask["newval"] != ""))
                        {
                            resp += "<div style=\"color:red;\">";
                            if (who == "ServerName".ToUpper()) { ServerName = ask["newval"].Trim(); resp += "<b>ServerName</b> set to <b>" + ServerName + "</b>"; };
                            if (who == "MaxClientAlive".ToUpper()) { ushort.TryParse(ask["newval"].Trim(), out MaxClientAlive); resp += "<b>MaxClientAlive</b> set to <b>" + MaxClientAlive.ToString() + "</b>"; };
                            if (who == "maxHours".ToUpper()) { byte.TryParse(ask["newval"].Trim(), out maxHours); resp += "<b>maxHours</b> set to <b>" + maxHours.ToString() + "</b>"; };
                            if (who == "greenMinutes".ToUpper()) { ushort.TryParse(ask["newval"].Trim(), out greenMinutes); resp += "<b>greenMinutes</b> set to <b>" + greenMinutes.ToString() + "</b>"; };
                            if (who == "disableAIS".ToUpper()) { disableAIS = ask["newval"].Trim().ToUpper() == "TRUE"; resp += "<b>disableAIS</b> set to <b>" + disableAIS.ToString() + "</b>"; };
                            if (who == "KMLObjectsRadius".ToUpper()) { int.TryParse(ask["newval"].Trim(), out KMLObjectsRadius); resp += "<b>KMLObjectsRadius</b> set to <b>" + KMLObjectsRadius.ToString() + "</b>"; };
                            if (who == "KMLObjectsLimit".ToUpper()) { int.TryParse(ask["newval"].Trim(), out KMLObjectsLimit); resp += "<b>KMLObjectsLimit</b> set to <b>" + KMLObjectsLimit.ToString() + "</b>"; };
                            if (who == "sendBack".ToUpper()) { sendBack = ask["newval"].Trim().ToUpper() == "TRUE"; resp += "<b>sendBack</b> set to <b>" + sendBack.ToString() + "</b>"; };
                            if (who == "callsignToUser".ToUpper()) { callsignToUser = ask["newval"].Trim().ToUpper() == "TRUE"; resp += "<b>callsignToUser</b> set to <b>" + callsignToUser.ToString() + "</b>"; };
                            //if (who == "RESTART") 
                            //{
                            //    if (BUDS != null) BUDS.SaveToTempFile();
                            //    System.Diagnostics.Process.Start(OruxPalsServerConfig.GetCurrentDir() + @"\DelayStart.cmd", Environment.UserInteractive ? "/console" : "/service");                                
                            //    Environment.Exit(0); 
                            //};
                            resp += "</div>";
                        };
                        {
                            resp += "<table cellpadding=\"1\" cellspacing=\"1\" border=\"0\">";
                            resp += "<tr><td><b>ServerName: </b></td><td>" + ServerName + " </td><td><span style=\"color:green;\">[string]</span></td></tr>";
                            resp += "<tr><td><b>MaxClientAlive: </b></td><td>" + MaxClientAlive.ToString() + " </td><td><span style=\"color:green;\">[minutes]</span></td></tr>";
                            resp += "<tr><td><b>maxHours: </b></td><td>" + maxHours.ToString() + " </td><td><span style=\"color:green;\">[hours]</span></td></tr>";
                            resp += "<tr><td><b>greenMinutes	: </b></td><td>" + greenMinutes.ToString() + " </td><td><span style=\"color:green;\">[minutes]</span></td></tr>";
                            resp += "<tr><td><b>disableAIS: </b></td><td>" + disableAIS.ToString() + " </td><td><span style=\"color:green;\">[true/false]</span></td></tr>";
                            resp += "<tr><td><b>KMLObjectsRadius: </b></td><td>" + KMLObjectsRadius.ToString() + " </td><td><span style=\"color:green;\">[km]</span></td></tr>";
                            resp += "<tr><td><b>KMLObjectsLimit: </b></td><td>" + KMLObjectsLimit.ToString() + " </td><td><span style=\"color:green;\">[max]</span></td></tr>";
                            resp += "<tr><td><b>sendBack: </b></td><td>" + sendBack.ToString() + " </td><td><span style=\"color:green;\">[true/false]</span></td></tr>";
                            resp += "<tr><td><b>callsignToUser: </b></td><td>" + callsignToUser.ToString() + " </td><td><span style=\"color:green;\">[true/false]</span></td></tr>";
                            //resp += "<tr><td><b>Restart: </b></td><td> False </td><td><span style=\"color:green;\">[true/false]</span></td></tr>";
                            resp += "</table>";
                        };                        
                    }
                    else if ((who != null) && Buddie.BuddieCallSignRegex.IsMatch(who) && (!String.IsNullOrEmpty(ask["tochange"])))
                    {
                        string tochange = ask["tochange"];
                        if (tochange == "kill")
                        {
                            if (BUDS.Kill(who))
                                resp += who + " <b>killed</b><br/>";
                            else
                                resp += who + " <b>not killed</b><br/>";
                        };
                        if (tochange == "new")
                        {
                            bool ex = false;
                            if (BUDS != null)
                            {
                                Buddie[] all = BUDS.Current;
                                foreach (Buddie b in all)
                                    if (who == b.name)
                                    {
                                        ex = true;
                                        break;
                                    };
                            };
                            if (!ex)
                            {
                                Random rnd = new Random();
                                Buddie nb = new Buddie(1, who, 56 - (double)rnd.Next(0, 9999) / 10000.0, 38 - (double)rnd.Next(0, 9999) / 10000.0,
                                    (short)rnd.Next(90), (short)rnd.Next(359));
                                nb.lastPacket = "MANUAL";
                                OnNewData(nb);
                                resp += "User <b>" + who + "</b> created<br/>";
                            }
                            else
                                resp += "User <b>" + who + "</b> is <b>online</b><br/>";
                        };

                        if (tochange == "emulate")
                        {
                            string bs = "";
                            if (BUDS != null)
                            {
                                Buddie[] all = BUDS.Current;
                                foreach (Buddie b in all)
                                    if (who == b.name)
                                    {
                                        bs = b.IconSymbol;
                                        break;
                                    };
                            };
                            if (bs != "")
                            {
                                Random rnd = new Random();
                                Buddie nb = new Buddie(1, who, 56 - (double)rnd.Next(0, 9999) / 10000.0, 38 - (double)rnd.Next(0, 9999) / 10000.0,
                                    (short)rnd.Next(90), (short)rnd.Next(359));
                                nb.IconSymbol = bs;
                                nb.lastPacket = "MANUAL";
                                OnNewData(nb);
                                resp += "User <b>" + who + "</b> activity updated<br/>";
                            }
                            else
                                resp += "User <b>" + who + "</b> not found<br/>";
                        };

                        string newval = ask["newval"] == null ? null : ask["newval"];
                        if (!String.IsNullOrEmpty(newval))
                        {
                            if (tochange == "forward")
                            {
                                if (regUsers != null)
                                    foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                        if (who == u.name)
                                        {
                                            u.forward = System.Security.SecurityElement.Escape(newval.ToUpper().Trim());
                                            break;
                                        };
                            };
                            if (tochange == "status")
                            {
                                if (BUDS != null)
                                {
                                    Buddie[] all = BUDS.Current;
                                    foreach (Buddie b in all)
                                        if (who == b.name)
                                        {
                                            b.Status = System.Security.SecurityElement.Escape(newval.Trim());
                                            break;
                                        };
                                };
                            };
                            if (tochange == "comment")
                            {
                                OruxPalsServerConfig.RegUser cu = null;
                                if (regUsers != null)
                                    foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                        if (who == u.name)
                                        {
                                            u.comment = System.Security.SecurityElement.Escape(newval.Trim());
                                            break;
                                        };
                                if (BUDS != null)
                                {
                                    Buddie[] all = BUDS.Current;
                                    foreach (Buddie b in all)
                                        if (who == b.name)
                                        {
                                            b.Comment = System.Security.SecurityElement.Escape(newval.Trim());
                                            break;
                                        };
                                };
                            };
                            if (tochange == "icon")
                            {
                                try
                                {
                                    OruxPalsServerConfig.RegUser cu = null;
                                    if (regUsers != null)
                                        foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                            if (who == u.name)
                                            {
                                                u.aprssymbol = newval.Trim().Substring(0, 2);
                                                break;
                                            };
                                    if (BUDS != null)
                                    {
                                        Buddie[] all = BUDS.Current;
                                        foreach (Buddie b in all)
                                            if (who == b.name)
                                            {
                                                b.IconSymbol = newval.Trim().Substring(0, 2);
                                                break;
                                            };
                                    };
                                }
                                catch { };
                            };
                        };
                        string forw = who + " is <b style=\"color:red;\">not registered</b>, no forward";
                        string comm = who + " <b>not found</b>, no comment";
                        string stat = who + " is <b>offline</b>, no status";
                        string symb = who + " has no symbol";
                        if (regUsers != null)
                            foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                if (who == u.name)
                                {
                                    forw = "Forward for <b>" + who + "</b> is `<b>" + u.forward + "</b>`";
                                    comm = "Comment for <b>" + who + "</b> is `<b>" + u.comment + "</b>`";
                                    symb = "Symbol for <b>" + who + "</b> is `<b>" + u.aprssymbol + "</b>` - " + IconToHtml(u.aprssymbol) + "";
                                    break;
                                };
                        if (BUDS != null)
                        {
                            Buddie[] all = BUDS.Current;
                            foreach (Buddie b in all)
                                if (who == b.name)
                                {
                                    stat = "Status for user <b>" + who + "</b> is `<b>" + b.Status + "</b>`";
                                    comm = "Comment for user <b>" + who + "</b> is `<b>" + b.Comment + "</b>`";
                                    symb = "Symbol for <b>" + who + "</b> is `<b>" + b.IconSymbol + "</b>` - " + IconToHtml(b.IconSymbol) + "";
                                };
                        };
                        if (Buddie.BuddieCallSignRegex.IsMatch(who))
                            resp += who + " server code is <b>" + Buddie.Hash(who) + "</b><br/>";
                        if (Buddie.BuddieCallSignRegex.IsMatch(who))
                            resp += who + " aprs code is <b>" + APRSData.CallsignChecksum(who) + "</b><br/>";
                        resp += forw + "<br/>" + stat + "<br/>" + comm + "<br/>" + symb + "<br/>";
                    }
                    else if ((who != null) && (who == "*") && (!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "kill"))
                    {
                        if (BUDS != null) BUDS.Clear();
                        resp += "Buddies List is Empty";
                    }
                    else if ((who != null) && (who == "?") && (!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "kill"))
                    {
                        if (BUDS != null) BUDS.ClearUnknown();
                        resp += "Unknown users deleted";
                    }
                    else if ((who != null) && (who == "R") && (!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "kill"))
                    {
                        if (BUDS != null) BUDS.ClearKnown();
                        resp += "Known users deleted";
                    }
                    else if ((who != null) && (who == "T") && (!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "kill"))
                    {
                        if (BUDS != null) BUDS.ClearTemp();
                        resp += "Temp users deleted";
                    }
                    else if ((!String.IsNullOrEmpty(ask["tochange"])) && (ask["tochange"] == "new") && ((ask["who"] == null) || (ask["who"] == "")))
                    {
                        Random rnd = new Random();
                        string nuser = "T-";
                        for (int a = 0; a < 3; a++)
                            nuser += ((char)rnd.Next(0x41, 0x5A)).ToString();
                        nuser += rnd.Next(0, 99).ToString("00");
                        Buddie nb = new Buddie(1, nuser, 56 - (double)rnd.Next(0, 9999) / 10000.0, 38 - (double)rnd.Next(0, 9999) / 10000.0,
                            (short)rnd.Next(90), (short)rnd.Next(359));
                        nb.ID = ++Buddie._id;
                        nb.IconSymbol = Buddie.symbolAny.Substring((int)nb.ID % Buddie.symbolAnyLength, 1) + "0";
                        nb.lastPacket = "MANUAL";
                        OnNewData(nb);
                        resp += "User <a href=\"" + urlPath + "$master/?who=" + nuser + "\"><b>" + nuser + "</b></a> created";
                    }
                    else if ((ask["tochange"] != null) && (ask["tochange"] == "justask"))
                    {
                        if (BUDS != null)
                        {
                            Buddie[] all = BUDS.Current;
                            int bbc = 0;
                            resp += "<table cellpadding=\"1\" cellspacing=\"1\" border=\"0\">";
                            foreach (Buddie b in all)
                            {
                                string icon = IconToHtml(b.IconSymbol);
                                resp += "<tr><td>" + (++bbc).ToString("00") + "</td><td>" + icon + "</td><td><a href=\"" + urlPath + "$master/?who=" + b.name + "\">" + b.name + "</a>" +
                                    (b.regUser == null ? "" : " <sup>&reg;</sup>") +
                                    "</td><td><small>" + Buddie.BuddieToSource(b) + "</small></td></tr>";
                            };
                            resp += "</table>";
                            if (bbc == 0)
                                resp += "No Users Online";
                            else
                                resp += "<a href=\"" + urlPath + "vkml\">Users Online KML</a>";
                        };
                    }
                    else
                        resp += "<b>BAD USER NAME</b>";
                };
                HTTPClientSendResponse(cd.client, resp + "</body></html>");
            }
            else
            {
                HTTPClientSendError(cd.client, 403);
                return;
            };
        }

        private bool IsAdmin(string rxText)
        {
            try
            {
                int aut = rxText.IndexOf("Authorization: Basic");
                if (aut < 0)
                    return false;
                else
                {
                    aut += 21;
                    string cup = rxText.Substring(aut, rxText.IndexOf("\n", aut) - aut).Trim('\r').Trim();
                    string dup = Encoding.UTF8.GetString(Convert.FromBase64String(cup));
                    string[] up = dup.Split(new char[] { ':' }, 2);
                    if ((up == null) || (up.Length != 2)) return false;
                    if (up[0] != adminName) return false;
                    if (up[1] != adminPass) return false;
                    return true;
                };
            }
            catch { };
            return false;
        }

        private string IconToHtml(string symb)
        {
            string prose = "primary";
            string label = "";
            if (symb.Length == 2)
            {
                if (symb[0] == '\\')
                    prose = "secondary";
                else if ((symb[0] != '/') && (("#&0>AW^_acnsuvz").IndexOf(symb[1]) >= 0))
                {
                    prose = "secondary";
                    label = symb[0].ToString();
                    if (("#0A^cv").IndexOf(symb[1]) >= 0)
                        label = "<span style=\"color:black;\">" + label + "</span>";
                };
                symb = symb.Substring(1);
            };
            string symbtable = "!\"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
            int idd = symbtable.IndexOf(symb);
            if (idd < 0) idd = 14;
            int itop = (int)Math.Truncate(idd / 16.0) * 24;
            int ileft = (idd % 16) * 24;
            symb = "background:url(" + urlPath + "v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;";
            return "<span style=\"display:inline-block;height:24px;width:24px;font-weight:bold;color:white;text-align:center;padding:1px;" + symb + "\">&nbsp;" + label + "&nbsp;</span>";

        }

        private void OnBrowser(ClientData cd, string query, string rxText)
        {
            bool isAdmin = IsAdmin(rxText);
            query = query.Replace("/", "").ToUpper();
            string user = "user";

            int[] cHTTP = new int[3]; // 0 - BigBrotherGPS, 1 - OwnTracks, 2 - Traccar

            string addit = "";
            if ((query.Length > 0) && (Buddie.BuddieCallSignRegex.IsMatch(query)))
            {
                addit = "";
                Buddie[] all = BUDS.Current;
                foreach (Buddie b in all)
                {
                    if (b.source == 8) cHTTP[0]++;
                    if (b.source == 9) cHTTP[1]++;
                    if (b.source == 10) cHTTP[2]++;
                    if (b.name == query)
                    {
                        string src = Buddie.BuddieToSource(b);
                        string symb = b.IconSymbol;// = "Az";
                        string prose = "primary";
                        string label = "";
                        if (symb.Length == 2)
                        {
                            if (symb[0] == '\\')
                                prose = "secondary";
                            else if ((symb[0] != '/') && (("#&0>AW^_acnsuvz").IndexOf(symb[1]) >= 0))
                            {
                                prose = "secondary";
                                label = symb[0].ToString();
                                if (("#0A^cv").IndexOf(symb[1]) >= 0) 
                                    label = "<span style=\"color:black;\">" + label + "</span>";
                            };
                            symb = symb.Substring(1);
                        };
                        string symbtable = "!\"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
                        int idd = symbtable.IndexOf(symb);
                        if (idd < 0) idd = 14;
                        int itop = (int)Math.Truncate(idd / 16.0) * 24;
                        int ileft = (idd % 16) * 24;
                        symb = "background:url(../v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;";
                        symb = "<span style=\"display:inline-block;height:24px;width:24px;font-weight:bold;color:white;text-align:center;padding:1px;" + symb + "\">&nbsp;" + label + "&nbsp;</span>";

                        user = b.name;
                        addit += "<table cellpadding=\"1\" cellspacing=\"1\" border=\"0\">";
                        string admin = "";
                        if (isAdmin)
                            admin = " <sub><a href=\"../$master/?who=" + b.name + "\" style=\"color:gray;\">[admin]</a></sub>";
                        addit += String.Format("<tr><td><small>User:</small></td><td> <b><a href=\"../v/mapf.html#{0}\">{0}</a> {1} {2}</td>", b.name, b.regUser == null ? "" : "&reg; " + (b.regUser.forward == null ? "" : b.regUser.forward), admin);
                        addit += "<td rowspan=\"11\"><a target=\"_blank\" href=\"../vlmpp#" + b.name + "\"><img src=\"../vlmpq?user=" + b.name + "\" border=\"0\"/></a></td></tr>";
                        addit += String.Format("<tr><td><small>Source:</small></td><td> {0}</td></tr>", src);
                        addit += String.Format("<tr><td><small>Received:</small></td><td> {0} UTC</td></tr>", b.last);
                        addit += String.Format("<tr><td><small>Valid till:</small></td><td> {0} UTC</td></tr>", b.last.AddHours(maxHours));
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, "<tr><td><small>Position:</small></td><td> {0:00.00000000} {1:00.00000000}</td></tr>", b.lat, b.lon);
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, "<tr><td><small>Speed:</small></td><td> {0} km/h; {1:0.0} mph; {2:0.0} knots</td></tr>", b.speed, b.speed * 0.62137119, b.speed / 1.852);
                        addit += String.Format("<tr><td><small>Heading:</small></td><td> {0}&deg; {1}</td></tr>", b.course, HeadingToText(b.course));
                        addit += String.Format("<tr><td><small>Symbol:</small></td><td> {0} {1}</td></tr>", System.Security.SecurityElement.Escape(b.IconSymbol), symb);
                        addit += String.Format("<tr><td><small>Comment:</small></td><td style=\"color:navy;\"> {0}</td></tr>", b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment));
                        addit += String.Format("<tr><td><small>Status:</small></td><td style=\"color:maroon;\"> {0}</td></tr>", b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status));
                        addit += String.Format("<tr><td><small>Origin:</small></td><td style=\"color:gray;\"><small> {0}</small></td></tr>", b.lastPacket);
                        addit += "</table>";

                        addit += "<div id=\"ZM13\" style=\"display:inline-block;\"></div><div id=\"ZM15\" style=\"display:inline-block;\"></div>";

                        addit += String.Format("<div id=\"YA13\" style=\"border:solid 1px gray;display:inline-block;width:500px;height:300px;background:url('http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=13&l=map&pt={0},{1},round') 0px 0px no-repeat;\">" +
                            "<div style=\"display:block;width:24;height:24;font-weight:bold;color:white;text-align:center;padding:1px;overflow:hidden;position:relative;left:238;top:138;background:url(../v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;\">&nbsp;" + label + "&nbsp;</div>" +
                            "<div style=\"display:block;width:32;height:32;overflow:visible;position:relative;left:238;top:110;\"><a target=\"_blank\" href=\"" + (urlPath + "view#" + b.name) + "\"><img src=\"../v/images/arrow.png\" style=\"transform:rotate(" + b.course.ToString() + "deg);transform-origin:50% 50% 0px;\" title=\"" + b.name + "\"/></a></div>" +
                            "</div>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);
                        addit += String.Format("<div id=\"YA15\" style=\"border:solid 1px gray;display:inline-block;width:500px;height:300px;background:url('http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=15&l=map&pt={0},{1},round') 0px 0px no-repeat;\">" +
                            "<div style=\"display:block;width:24;height:24;font-weight:bold;color:white;text-align:center;padding:1px;overflow:hidden;position:relative;left:238;top:138;background:url(../v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;\">&nbsp;" + label + "&nbsp;</div>" +
                            "<div style=\"display:block;width:32;height:32;overflow:visible;position:relative;left:238;top:110;\"><a target=\"_blank\" href=\"" + (urlPath + "view#" + b.name) + "\"><img src=\"../v/images/arrow.png\" style=\"transform:rotate(" + b.course.ToString() + "deg);transform-origin:50% 50% 0px;\" title=\"" + b.name + "\"/></a></div>" +
                            "</div>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);
                        addit += String.Format("<br/><small><a target=\"_blank\" href=\"../v/mapf.html#" + b.name + "\">User info</a> | <a target=\"_blank\" href=\"../vlmap#" + b.name + "\">View on map</a> | <a target=\"_blank\" href=\"../vlmpp#" + b.name + "\">FOLLOW</a> | <a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\">view on yandex</a> | <a href=\"http://maps.google.com/?q={1}+{0}\" target=\"_blank\">view on google</a> | <a href=\"http://qrcoder.ru/code/?geo%3A{1}%2C{0}&8&0" + "\" target=\"_blank\">View GEO QR Code</a><small>"+
                            "<br/><h1>Open <a target=\"_blank\" href=\"geo:{1},{0}\">{1}, {0}</a> in External Program</h1>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);

                        addit += "<script>\r\n";
                        addit += " var mm = new MapMerger(500,300);\r\n ";
                        addit += " mm.InitIcon('" + b.IconSymbol.Replace(@"\", @"\\").Replace(@"'", @"\'") + "', " + b.course.ToString() + ", '" + b.name + "', '" + (urlPath + "view#" + b.name) + "');\r\n ";
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, " document.getElementById('ZM13').innerHTML = mm.GetMap({0}, {1}, 13);\r\n", b.lat, b.lon);
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, " document.getElementById('ZM15').innerHTML = mm.GetMap({0}, {1}, 15);\r\n", b.lat, b.lon);
                        addit += " </script>\r\n";
                    };
                };
            };

            bool isloc = false;
            if (LocalNetwork.Count > 0)
                foreach (string ln in LocalNetwork)
                    if ((new Regex(ln, RegexOptions.IgnoreCase)).Match(cd.IP).Success)
                    {
                        isloc = true;                        
                        break;
                    };
            if (isloc || isAdmin)
            {
                if (BlackListIP.Count > 0)
                {
                    addit += "<div style=\"font-weight:bold;\"><b>BlackListIP:</b> ";
                    for (int ib = 0; ib < BlackListIP.Count; ib++)
                        addit += (ib == 0 ? "" : ", ") + BlackListIP[ib];
                    addit += "</div>";
                };
                if (banlist.Count > 0)
                {
                    addit += "<div style=\"font-weight:bold;\"><b>Ban List: </b> ";
                    for (int ib = 0; ib < banlist.Count; ib++)
                        addit += (ib == 0 ? "" : ", ") + banlist[ib];
                    addit += "</div>";
                };
            };

            int cAIS = 0;
            int cAPRS = 0;
            int cAPRSr = 0;
            int cFRS = 0;
            int cAIR = 0;
            int cWSW = 0;
            int cWST = 0;
            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                {
                    if (ci.state == 1) cAIS++;
                    if (ci.state == 4) { cAPRS++; cAPRSr++; };
                    if (ci.state == 5) cFRS++;
                    if (ci.state == 6) cAPRS++;
                    if (ci.state == 7) cAIR++;
                    if (ci.state == 8) cWSW++;
                    if (ci.state == 9) cWST++;
               };
            int bc = 0, rbc = 0;
            string rbds = "";
            string ubds = "";
            if (BUDS != null)
            {
                Buddie[] all = BUDS.Current;
                foreach (Buddie b in all)
                {
                    if (b.source == 5) continue; // Everytime object
                    if (b.source == 6) continue; // Static object
                    bc++;
                    bool isreg = false;
                    if(regUsers != null)
                        foreach (OruxPalsServerConfig.RegUser u in regUsers)
                        {
                            if (u.name == b.name)
                            {
                                isreg = true;
                                rbc++;
                                break;
                            };
                            if((u.services != null) && (u.services.Length > 0))
                                foreach(OruxPalsServerConfig.RegUserSvc svc in u.services)
                                    if(svc.names.Contains("A") &&(svc.id == b.name))
                                    {
                                        isreg = true;
                                        rbc++;
                                        break;
                                    };
                        };
                    if(isreg)
                      rbds += "<a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a> ";
                    else
                      ubds += "<a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a> ";
                };
                if (rbds.Length > 0) rbds = "Registered: " + rbds + "\r\n<br/>";
                if (ubds.Length > 0) ubds = "Unregistered: " + ubds + "\r\n<br/>";
            };
            string fsvc = "";
            if (aprsgw != null)
            {
                int uc = 0;
                if(regUsers != null)
                    foreach(OruxPalsServerConfig.RegUser u in regUsers)
                        if((u.forward != null) && (u.forward.Contains("A"))) uc++;
                fsvc += String.Format("&nbsp; &nbsp; A as a with {0} clients: \r\n<br/>", uc);
                fsvc += "<span style=\"color:silver;font-size:11px;\">";
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>status:</b> {0}\r\n<br/>", aprsgw.State);
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>last rx:</b> {0}\r\n<br/>", aprsgw.lastRX);
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>last tx:</b> {0}\r\n<br/>", aprsgw.lastTX);
                fsvc += "</span>";
            };
            if (forwardServices != null)
                foreach (OruxPalsServerConfig.FwdSvc svc in forwardServices)
                    if (svc.forward == "yes")
                    {
                        string inUsr = "";
                        int uc = 0;
                        if (regUsers != null)
                            foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                if ((u.forward != null) && (u.forward.Contains(svc.name)))
                                {
                                    inUsr += (inUsr.Length > 0 ? ", " : "") + u.name;
                                    uc++;
                                };
                        fsvc += "&nbsp; &nbsp; " + String.Format("{0} as {1} with {2} clients{3}\r\n<br/>", svc.name, svc.type, uc, isloc || isAdmin ? ": " + inUsr : "");
                    };
            //if ((BUDS != null) && (addit == "")) addit = "<span style=\"color:green;\">Static Objects Data:</span><br/>" + BUDS.GetStaticObjectsInfo();
            HTTPClientSendResponse(cd.client, GetPageHTMLHeader() + GetPageHeader(0, isAdmin) + String.Format(
                "<div style=\"color:blue;\">Clients " + (disableAIS ? "" : "AIS/") + "APRS[FR24](R)/FRS/AIR: " + (disableAIS ? "" : "{2} / ") + "{7} ({10}) / {8} / {11}\r\n</div>" +
                "<div style=\"color:navy;\">Clients HTTP: " + String.Format("BigBrotherGPS - {0}, Owntracks - {1}, OsmAnd or Traccar - {2}", new object[] { cHTTP[0], cHTTP[1], cHTTP[2] }) + "\r\n</div>" +
                "<div style=\"color:blue;\">Clients WebSocket B/T: {12} / {13}\r\n</div><hr/>" +
                "<div style=\"color:green;\">Buddies Online/Registered/Unregistered: {3}\r\n<br/>" +
                "{6}\r\n</div><hr/>" +
                "<div style=\"color:maroon;\">Forward Services:\r\n<br/>" +
                "{9}\r\n</div><hr/>" +
                "<div style=\"color:#003366;\">" +
                "<div style=\"color:navy;\">Client connect information:\r\n<br/>" +
                (OnlyHTTPPort > 0 ? " &nbsp; &nbsp; <b style=\"color:maroon;\">HTTP Port: <a href=\"#\" onclick=\"javascript:event.target.port=" + OnlyHTTPPort.ToString() + "\">" + OnlyHTTPPort.ToString() + "</a> or <a href=\"#\" onclick=\"javascript:event.target.port=" + ListenPort.ToString() + "\">" + ListenPort.ToString() + "</a></b>\r\n<br/>" : "") +
                (OnlyAPRSPort > 0 ? " &nbsp; &nbsp; <b style=\"color:maroon;\">APRS Port: " + OnlyAPRSPort.ToString() + " or " + ListenPort.ToString() + "</b>\r\n<br/>" : "") +
                (OnlyAISPort > 0 ? " &nbsp; &nbsp; <b style=\"color:maroon;\">AIS Port: " + OnlyAISPort.ToString() + " or " + ListenPort.ToString() + "</b>\r\n<br/>" : "") +
                (OnlyFRSPort > 0 ? " &nbsp; &nbsp; <b style=\"color:maroon;\">FRS Port: " + OnlyFRSPort.ToString() + " or " + ListenPort.ToString() + "</b>\r\n<br/>" : "") +                
                "&nbsp; &nbsp; <b>OruxMaps</b>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - AIS <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">receive data only</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - APRS <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - - Filter: me/0 <i style=\"color:gray;\">- no static objects;</i> me/-1 <i style=\"color:gray;\">- no static, no everytime objects;</i> -fn/AIR <i style=\"color:gray;\">- block all incoming to user APRS-IS or APRS-on-AIR DATA;</i> FR24/S15-20/7/40/VKO/DME/SVO<i style=\"color:gray;\"> - FR24 DATA</i>; +nm/10/50<i style=\"color:gray;\"> - NarodMon Weather Data in 10 km limit 50 max</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - GPSGate <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}@" + user + "/</span> <i style=\"color:gray;\">set user password as IMEI</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - MapMyTracks <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}m/</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; <b>Other Software</b>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; APRS (<b>APRSDroid</b>) <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; FRS (GPSGate Tracker) <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user's phone number must be registered</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; <b>Big Brother GPS</b> <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}bb/user_password</span><i style=\"color:gray;\"> user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; Owntracks <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}ot/user_password</span><i style=\"color:gray;\"> - HTTP mode, user and password required, identification is empty</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; <b>Owntracks</b> <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}ot/</span><i style=\"color:gray;\"> - HTTP mode, identification user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; <b>Traccar Client</b> <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}oa/</span><i style=\"color:gray;\">, user_password as device id (&id=user_password) required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; Traccar Viewer <span style=\"color:black;\">HTTP:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span><i style=\"color:gray;\"> user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; TraccarM <span style=\"color:black;\">HTTP:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span><i style=\"color:gray;\"> user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; GPS Monitor <span style=\"color:black;\">HTTP:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span><i style=\"color:gray;\"> user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; OsmAnd <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}oa/?id=user_password</span><span style=\"color:silver;\">&lat=&#123;0&#125;&lon=&#123;1&#125;&speed=&#123;5&#125;&bearing=&#123;6&#125;</span><i style=\"color:gray;\"> user_password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; Tracker (Tracker for Traccar) <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span>, <span style=\"color:black;\">Protocol:</span> <span style=\"color:blue;\">OSMAnd, http</span>, <span style=\"color:black;\">Path:</span> " + urlPath.Substring(1) + "oa/ <span style=\"color:black;\">ID:</span> <span style=\"color:blue;\">user_password</span><i style=\"color:gray;\">, user_password required</i>\r\n<br/>" +                
                "</div><hr/>" +
                addit +
                "</body></html>",
                new object[] { 
                softver, 
                started, 
                cAIS, 
                bc.ToString()+" / " + rbc.ToString()+" / " + (bc - rbc).ToString(),
                ListenPort,
                urlPath,
                rbds + ubds,
                cAPRS,
                cFRS,
                fsvc,
                cAPRSr,
                cAIR, cWSW, cWST
                }));
        }   

        private void OnView(ClientData cd, string query, string rxText)
        {
            int asp = query.IndexOf("?");
            System.Collections.Specialized.NameValueCollection ask = new System.Collections.Specialized.NameValueCollection();
            if (asp > 0)
            {
                ask = HttpUtility.ParseQueryString(query.Substring(asp));
                query = query.Remove(asp);
            };
            string[] ss = query.Split(new char[] { '/' }, 2);
            string prf = ss[0];
            string ptf = "";
            if (ss.Length > 1) ptf = ss[1];

            bool isAdmin = IsAdmin(rxText);

            if (prf == "list")
            {
                string us = null;
                if ((ask["user"] != null) && (ask["user"].Length > 0)) us = ask["user"].Trim().ToUpper();
                string cdata = API_getList(us);
                HTTPClientSendResponse(cd.client, cdata);
            }
            else if (prf.StartsWith("lmap"))
            {
                HTTPClientSendFile(cd.client, "maplive.html", "MAP");
            }
            else if (prf.StartsWith("lmpp"))
            {
                HTTPClientSendFile(cd.client, "maplone.html", "MAP");
            }
            else if (prf.StartsWith("lmpq"))
            {
                GetQR(cd, ask["user"], rxText);
            }
            else if (prf.StartsWith("live"))
            {
                string resp = GetPageHTMLHeader() + GetPageHeader(6, isAdmin);
                // resp += "<script type=\"text/javascript\">\r\nvar socket_url = 'ws://" + infoIP + ":" + ServerPort + urlPath + "socket?hide=virtual';\r\n</script>\r\n";
                string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\MAP\live.html";
                if (File.Exists(ffn))
                {
                    try
                    {
                        System.IO.FileStream fs = new FileStream(ffn, FileMode.Open, FileAccess.Read);
                        byte[] buff = new byte[fs.Length];
                        fs.Read(buff, 0, buff.Length);
                        fs.Close();
                        string fdata = System.Text.Encoding.UTF8.GetString(buff);
                        resp += fdata;
                    }
                    catch { }
                };
                HTTPClientSendResponse(cd.client, resp + "</body></html>");
            }
            else if (prf.StartsWith("users"))
            {
                string resp = GetPageHTMLHeader() + GetPageHeader(5, isAdmin);
                if (BUDS != null)
                {
                    Buddie[] all = BUDS.Current;
                    int bbc = 0, breg = 0, bunk = 0;
                    resp += "<table cellpadding=\"1\" cellspacing=\"1\" border=\"0\">";
                    resp += "<tr><td colspan=\"3\">User/Object</td><td>Last Update</td><td>Age</td><td>Latitude</td><td>Longitude</td><td>Speed</td><td>Heading</td><td>Source</td></tr>";
                    foreach (Buddie b in all)
                    {
                        if (b.IsVirtual) continue;
                        if ((ask["only"] != null) && (ask["only"] == "registered") && (b.regUser == null)) continue;
                        if ((ask["only"] != null) && (ask["only"] == "unknown") && (b.regUser != null)) continue;
                        if (b.regUser == null) bunk++; else breg++;
                        string icon = IconToHtml(b.IconSymbol);
                        string bcol = (bbc % 2 == 0 ? "white" : "#FFDDDD");
                        double age = DateTime.UtcNow.Subtract(b.last).TotalMinutes;
                        if (age > greenMinutes) bcol = "#DDDDCC";
                        resp += "<tr style=\"background-color:" + bcol + ";\">" +
                            "<td><a href=\"" + urlPath + "v/mapf.html#" + b.name + "\">" + (++bbc).ToString("00") + "</a></td><td>" + icon + "</td>" +
                            "<td><a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a>" + (b.regUser == null ? "" : " <sup>&reg;</sup>") + " &nbsp; </td>" +
                            "<td style=\"color:maroon;\">" + b.last.ToString("HH:mm:ss dd.MM.yyyy") + " &nbsp; </td>" +
                            "<td style=\"color:maroon;\">" + age.ToString("0.") + " m &nbsp; </td>" +
                            "<td style=\"color:navy;\">" + b.lat.ToString("0.0000000", System.Globalization.CultureInfo.InvariantCulture) + " &nbsp; </td>" +
                            "<td style=\"color:navy;\">" + b.lon.ToString("0.0000000", System.Globalization.CultureInfo.InvariantCulture) + " &nbsp; </td>" +
                            "<td style=\"color:green;\">" + String.Format(System.Globalization.CultureInfo.InvariantCulture, "<b>{0} km/h</b>; {1:0.0} mph; {2:0.0} knots", b.speed, b.speed * 0.62137119, b.speed / 1.852) + " &nbsp; </td>" +
                            "<td style=\"color:purple;\">" + String.Format("{0}&deg; {1}", b.course, HeadingToText(b.course)) + " &nbsp; </td>" +
                            "<td style=\"color:gray;\"><small>" + Buddie.BuddieToSource(b) + "</small> &nbsp; </td></tr>";
                    };
                    resp += "</table>";
                    if (bbc == 0)
                        resp += " ... <b>No Users <a href=\"?\">Online</a></b>";
                    else
                        resp += " ... <b>" + bbc.ToString() + " </b> Users " +
                            "<a href=\"?\">Online</a> (<a href=\"" + urlPath + "vkml\">kml</a>), <b>" + breg.ToString() + "</b> " +
                            "<a href=\"?only=registered\">registered</a> (<a href=\"" + urlPath + "vkml?only=registered\">kml</a>), <b>" + bunk.ToString() + "</b> " +
                            "<a href=\"?only=unknown\">unknown</a> (<a href=\"" + urlPath + "vkml?only=unknown\">kml</a>)";
                };
                HTTPClientSendResponse(cd.client, resp + "</body></html>");
            }
            else if (prf == "icon")
            {
                if (ask.HasKeys() && (ask["symbol"] != null))
                {
                    string sic = ask["symbol"];
                    int siz = 24;
                    try
                    {
                        sic = System.Text.Encoding.ASCII.GetString(ConvertHexStringToByteArray(sic));
                        if ((ask["size"] != null) && (ask["size"] == "128")) siz = 128;
                        if ((ask["size"] != null) && (ask["size"] == "64")) siz = 64;

                    }
                    catch { };
                    byte[] icon = BinaryAPRSImage(sic, siz);
                    HTTPClientSendBinary(cd.client, "image/png", icon);
                }
                else
                    HTTPClientSendResponse(cd.client, "NO ICON");
            }
            else if (prf.StartsWith("kml"))
            {
                Buddie[] bs = null;
                if (BUDS != null) bs = BUDS.Current;
                string cdata = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>";
                cdata += "<name>" + ServerName + "</name>";
                if (BUDS != null)
                {
                    List<string> symb = new List<string>();
                    foreach (Buddie b in bs)
                    {
                        if (b.IsVirtual) continue;
                        if ((ask["only"] != null) && (ask["only"] == "registered") && (b.regUser == null)) continue;
                        if ((ask["only"] != null) && (ask["only"] == "unknown") && (b.regUser != null)) continue;
                        if (!symb.Contains(b.IconSymbol))
                        {
                            symb.Add(b.IconSymbol);
                            string si = BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(b.IconSymbol)).Replace("-", string.Empty);
                            cdata += "<Style id=\"icon-" + si + "\"><IconStyle><scale>1.0</scale><Icon><href>http://" + infoIP + ":" + ListenPort.ToString() + urlPath + "vicon?symbol=" + si + "</href></Icon></IconStyle></Style>";
                        };
                    };
                };
                cdata += "<Folder><name>Users Online</name>";
                if (BUDS != null)
                {
                    int bc = 0;
                    foreach (Buddie b in bs)
                    {
                        bc++;
                        if (b.IsVirtual) continue;
                        if ((ask["only"] != null) && (ask["only"] == "registered") && (b.regUser == null)) continue;
                        if ((ask["only"] != null) && (ask["only"] == "unknown") && (b.regUser != null)) continue;
                        string src = Buddie.BuddieToSource(b);

                        cdata += String.Format(
                            "<Placemark><name>{0}</name>" +
                            "<description>id: {7}, user: {0}, received: {1}, lat: {2}, lon: {3}, speed: {4}, hdg: {5}, source: {6} , age: {8}, symbol: '{9}', r: {10}, comment: {11} , status: {12}</description>" +
                            "<styleUrl>#icon-{13}</styleUrl><Point><coordinates>{3},{2},0</coordinates></Point></Placemark>",
                            new object[] { 
                                b.name, b.last, 
                                b.lat.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.lon.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.speed, b.course, 
                                src, b.ID, 
                                (int)DateTime.UtcNow.Subtract(b.last).TotalSeconds, 
                                b.IconSymbol, 
                                b.regUser == null ? 0 : 1, 
                                (b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment)), 
                                (b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status)),
                                BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(b.IconSymbol)).Replace("-", string.Empty) });
                    };
                };
                cdata += "</Folder></Document></kml>";
                HTTPClientSendXML(cd.client, "application/xml", cdata); // "application/vnd.google-earth.kml+xml"
            }
            else if (prf == "near")
            {
                Match m = Regex.Match(ptf, @"^([\d.]+)/([\d.]+)");
                if (m.Success)
                {
                    double lat = 0;
                    double lon = 0;
                    try
                    {
                        lat = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        lon = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        HTTPClientSendError(cd.client, 417);
                    };
                    string cdata = "";
                    if (BUDS != null)
                    {
                        PreloadedObject[] no = BUDS.GetNearest(lat, lon);

                        if ((no != null) && (no.Length > 0))
                            foreach (PreloadedObject po in no)
                            {
                                string src = "Unknown";
                                if (po.radius < 0)
                                    src = "Everytime Object";
                                else
                                    src = "Static Object";
                                cdata += (cdata.Length > 0 ? "," : "") + "{" + String.Format(
                                    "id:{7},user:'{0}',received:'{1}',lat:{2},lon:{3},speed:{4},hdg:{5},source:'{6}',age:{8},symbol:'{9}',r:{10},comment:'{11}',status:'{12}'",
                                    new object[] { 
                                po.name, DateTime.UtcNow.ToString(), 
                                po.lat.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                po.lon.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                0, 0, 
                                src, -1, 
                                0, 
                                po.symbol.Replace(@"\", @"\\").Replace(@"'", @"\'"), 
                                0, 
                                System.Security.SecurityElement.Escape(po.comment), 
                                "" }) + "}";
                            };
                    };
                    cdata = "[" + cdata + "]";
                    HTTPClientSendResponse(cd.client, cdata);
                }
                else
                    HTTPClientSendError(cd.client, 417);
            }
            else
            {
                if (ptf == "") // MAP VIEW
                    HTTPClientSendFile(cd.client, "map.html", "MAP");
                else if (ptf.StartsWith("resources")) // send 
                {
                    string path = "resources";
                    if (ptf.Length > 9)
                    {
                        string[] sub = ptf.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < sub.Length; i++)
                            sub[i] = sub[i].Replace("..", "00").Replace("%20", " ");
                        path = String.Join(@"\", sub);
                    };
                    string full_path = OruxPalsServerConfig.GetCurrentDir() + @"\" + path;
                    bool possiblePath = full_path.IndexOfAny(Path.GetInvalidPathChars()) == -1;
                    if (!possiblePath) { HTTPClientSendError(cd.client, 406); return; };

                    if (System.IO.Directory.Exists(full_path))
                    {
                        string txtout = GetPageHTMLHeader() + GetPageHeader(2, isAdmin); //+ "Resources:\r\n<br/>";
                        {
                            string readme = full_path + @"\.readme";
                            if (File.Exists(readme))
                            {
                                System.IO.FileStream fs = new FileStream(readme, FileMode.Open, FileAccess.Read);
                                StreamReader sr = new StreamReader(fs, System.Text.Encoding.GetEncoding(1251));
                                string fdata = sr.ReadToEnd();
                                sr.Close();
                                fs.Close();
                                if (fdata.Length > 0) txtout += "<div style=\"color:green;margin: 2px 20px 2px 20px;\">" + fdata.Trim().Replace("\r\n", "<br/>") + "</div>";
                            };
                        };
                        txtout += "<table cellpadding=\"1\" cellspacing=\"1\">";

                        string rel_path = path;
                        if (path.LastIndexOf(@"\") > 0)
                        {
                            rel_path = rel_path.Substring(0, path.LastIndexOf(@"\"));
                            txtout += "<tr><td><span style=\"color:silver;\">00</span> - <a href=\"" + urlPath + "v/" + rel_path + "/\" style=\"color:gray;\"><b>[UP]</b><a/></td><td>&nbsp;</td></td>";
                        };
                        // else txtout += "<tr><td><span style=\"color:silver;\">00</span> - <a href=\"" + urlPath + "info\" style=\"color:gray;\"><b>[Main View]</b><a/></td><td>&nbsp;</td></tr>";


                        int el = 1;

                        string[] dlist = Directory.GetDirectories(full_path, "*.*", SearchOption.TopDirectoryOnly);
                        if (dlist != null)
                            foreach (string f in dlist)
                            {
                                DirectoryInfo di = new DirectoryInfo(f);
                                txtout += "<tr><td><span style=\"color:silver;\">" + (el++).ToString("00") + "</span> - <a href=\"" + urlPath + "v/" + path.Replace(@"\", "/") + "/" + di.Name + "\"><b>" + di.Name + "</b><a/></td><td> &nbsp; DIR\r\n</td></tr>";
                            };

                        string[] flist = Directory.GetFiles(full_path, "*.*", SearchOption.TopDirectoryOnly);
                        if (flist != null)
                            foreach (string f in flist)
                            {
                                string fn = System.IO.Path.GetFileName(f);
                                if (fn == ".readme") continue;
                                FileInfo fi = new FileInfo(f);
                                txtout += "<tr><td><span style=\"color:silver;\">" + (el++).ToString("00") + "</span> - &nbsp; <a href=\"" + urlPath + "v/" + path.Replace(@"\", "/") + "/" + fn + "\">" + fn + "<a/></td><td> &nbsp; " + String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} MB", fi.Length / 1024.0 / 1024.0) + "\r\n</td></tr>";
                            };
                        txtout += "</table>";

                        HTTPClientSendResponse(cd.client, txtout + "</body></html>");
                    }
                    else if (File.Exists(full_path))
                    {
                        HTTPClientSendFile(cd.client, path, String.Empty);
                    };
                }
                else if (ptf == "objects") // send 
                {
                    string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\OBJECTS\";
                    string[] flist = Directory.GetFiles(ffn, "*.*", SearchOption.TopDirectoryOnly);
                    string txtout = GetPageHTMLHeader() + GetPageHeader(3, isAdmin);// +"Objects:\r\n<br/>";
                    txtout += "- <a href=\"../info\">&nbsp;..&nbsp;<a/><br/>";
                    if (flist != null)
                        foreach (string f in flist)
                        {
                            FileInfo fi = new FileInfo(f);
                            txtout += "- <a href=\"objects/" + System.IO.Path.GetFileName(f) + "\">" + System.IO.Path.GetFileName(f) + "<a/> " + String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} MB", fi.Length / 1024.0 / 1024.0) + "\r\n<br/>";
                        };
                    HTTPClientSendResponse(cd.client, txtout + "</body></html>");
                }
                else if (ptf.Contains("objects/"))
                    HTTPClientSendFile(cd.client, ptf.Substring(8), "OBJECTS");
                else
                    HTTPClientSendFile(cd.client, ptf, "MAP");
            };
        }

        private void GetQR(ClientData cd, string user, string rxText)
        {
            if(String.IsNullOrEmpty(user))
            {
                HTTPClientSendError(cd.client, 409);
                return;
            };

            byte[] img = new byte[0];
            try
            {
                System.Drawing.Image im = GenerateQRCode(
                    String.Format("http://{0}:{1}{2}vlmpp#{3}", infoIP, ListenPort, urlPath, user),
                    user, null);
                MemoryStream ms = new MemoryStream();
                im.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                im.Dispose();
                img = ms.ToArray();
                ms.Close();
            }
            catch {};
            HTTPClientSendBinary(cd.client, "image/png", img);
        }

        public static byte[] ConvertHexStringToByteArray(string hexString)
        {
            if (hexString.Length % 2 != 0)
            {
                throw new ArgumentException(String.Format(System.Globalization.CultureInfo.InvariantCulture, "The binary key cannot have an odd number of digits: {0}", hexString));
            }

            byte[] data = new byte[hexString.Length / 2];
            for (int index = 0; index < data.Length; index++)
            {
                string byteValue = hexString.Substring(index * 2, 2);
                data[index] = byte.Parse(byteValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            }

            return data;
        }

        private void OnCmd(ClientData cd, string query)
        {
            int s2f = query.IndexOf("/?cmd=");
            if (s2f < 3) { HTTPClientSendError(cd.client, 403); return; };
            string user = query.Substring(0, s2f).ToUpper();
            if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendError(cd.client, 403); return; };
            string cmd = query.Substring(s2f + 6);

            string[] pData = cmd.Split(new string[] { "," }, StringSplitOptions.None);
            if (pData.Length < 13) { HTTPClientSendError(cd.client, 417); return; };
            if (pData[2] != "_SendMessage") { HTTPClientSendError(cd.client, 417); return; };
            int pass = 0;
            if (!int.TryParse(pData[1], out pass)) { HTTPClientSendError(cd.client, 417); return; };
            if (Buddie.Hash(user) != pass) { HTTPClientSendError(cd.client, 403); return; };

            cd.user = user;

            // PARSE //
            try
            {
                double lat = double.Parse(pData[4].Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                lat = double.Parse(pData[4].Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + lat / 60;
                if (pData[5] == "S") lat *= -1;

                double lon = 0;
                if (pData[6].IndexOf(".") > 4)
                {
                    lon = double.Parse(pData[6].Substring(3, 7), System.Globalization.CultureInfo.InvariantCulture);
                    lon = double.Parse(pData[6].Substring(0, 3), System.Globalization.CultureInfo.InvariantCulture) + lon / 60;
                }
                else
                {
                    lon = double.Parse(pData[6].Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                    lon = double.Parse(pData[6].Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + lon / 60;
                };
                if (pData[7] == "W") lon *= -1;

                // double alt = double.Parse(pData[8], System.Globalization.CultureInfo.InvariantCulture);
                double speed = double.Parse(pData[9], System.Globalization.CultureInfo.InvariantCulture) * 1.852;
                double heading = double.Parse(pData[10], System.Globalization.CultureInfo.InvariantCulture);

                HTTPClientSendResponse(cd.client, "accepted");
                cd.client.Close();

                Buddie b = new Buddie(1, user, lat, lon, (short)speed, (short)heading);
                b.lastPacket = cmd;
                OnNewData(b);
            }
            catch { HTTPClientSendError(cd.client, 417); return; };
        }

        private void OnPost(ClientData cd, string rxText)
        {
            cd.state = 3;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) { HTTPClientSendError(cd.client, 400); return; };
            string query = rxText.Substring(5, hi - 5).Trim();
            if (!IsValidQuery(query))
            {
                if (query.StartsWith("/api")) { OnTraccarAPI(cd, "POST", query, rxText); return; };
                HTTPClientSendError(cd.client, 404);
                return;
            };
            if (query[10] == 'm')
            {
                OnMMT(cd, rxText);
                return;
            };
            if (query[10] == '$')
            {
                OnAdmin(cd, query.Substring(11), rxText);
                return;
            };
            if ((query.Length > 16) && (query[10] == 'b') && (query[11] == 'b') && (query[12] == '/'))
            {
                OnBBG(cd, query.Substring(13), rxText);
                return;
            };
            if ((query.Length > 11) && (query[10] == 'o') && (query[11] == 't') && (query[12] == '/'))
            {
                OnOwnTracks(cd, query.Length > 13 ? query.Substring(13) : null, rxText);
                return;
            };
            if ((query.Length > 16) && (query[10] == 'o') && (query[11] == 'a') && (query[12] == '/'))
            {
                OnOsmAndTraccar(cd, query.Substring(13), rxText);
                return;
            };
            if ((query.Length > 17) && ((query.Substring(10, 7).ToLower() == "traccar")))
            {
                OnTraccarAPIAuth_TraccarViewer(cd, query.Substring(17), rxText, "POST");
                return;
            };
            HTTPClientSendError(cd.client, 403);
        }

        private string API_getList(string user)
        {
            return API_getList(user, true);
        }

        private string API_getList(string user, bool allowVirt)
        {
            string cdata = "";
            if (BUDS != null)
            {
                Buddie[] bs = BUDS.Current;
                foreach (Buddie b in bs)
                {
                    if ((!allowVirt) && (b.IsVirtual)) continue;
                    if ((!String.IsNullOrEmpty(user)) && (user != b.name)) continue;
                    string src = Buddie.BuddieToSource(b);
                    cdata += (cdata.Length > 0 ? "," : "") + "{" + String.Format(
                        "id:{7},user:'{0}',received:'{1}',lat:{2},lon:{3},speed:{4},hdg:{5},source:'{6}',age:{8},symbol:'{9}',r:{10},comment:'{11}',status:'{12}',upurl:'{13}'",
                        new object[] { 
                                b.name, b.last, 
                                b.lat.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.lon.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.speed, b.course, 
                                src, b.ID, 
                                (int)DateTime.UtcNow.Subtract(b.last).TotalSeconds, 
                                b.IconSymbol.Replace(@"\", @"\\").Replace(@"'", @"\'"), 
                                b.regUser == null ? 0 : 1, 
                                (b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment)), 
                                (b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status)),
                                urlPath + "vlist?user=" + b.name }) + "}";
                };
            };
            cdata = "[" + cdata + "]";
            return cdata;
        }

        // Traccar Manager Applications
        private string API_GetDevices4TraccarWebSocket(string user)
        {
            Buddie[] bs = BUDS != null ? BUDS.Current : null;
            List<string> added = new List<string>();

            string json = "[";
            if (bs != null)
            {
                CRC32 crc32 = new CRC32();
                foreach (Buddie b in bs)
                {
                    if (b.IsVirtual) continue;
                    if ((!String.IsNullOrEmpty(user)) && (user != b.name)) continue;
                    string src = Buddie.BuddieToSource(b);
                    if (json.Length > 1) json += ",";
                    try
                    {
                        int id = crc32.CRC32Num(b.name);
                        json += "{" + String.Format("\"id\":{0}, \"name\":\"{1}\", \"uniqueId\":\"{1}\", \"status\":\"online\", \"disabled\":false," +
                         "\"lastUpdate\":\"{3}\", \"positionId\":{0}, \"groupId\":1," +
                         "\"phone\":\"{5}\", \"model\":\"{4}\", \"contact\":\"{6}\", \"category\":\"vehicle\", " +
                         "\"geofenceIds\":[], ",
                          new object[] { 
                                id, b.name, (b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status)),
                                b.last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")+"Z", "car", b.regUser == null ? 0 : 1,
                               (b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment))}
                        ) + "\"attributes\":{}}";
                        added.Add(b.name);
                    }
                    catch (Exception ex) { };
                };
            };

            if ((regUsers != null) && (regUsers.Length > 0))
            {
                CRC32 crc32 = new CRC32();
                for (int i = 0; i < regUsers.Length; i++)
                {
                    if (added.Contains(regUsers[i].name)) continue;
                    if (json.Length > 1) json += ",";
                    try
                    {
                        int id = crc32.CRC32Num(regUsers[i].name);
                        json += "{" + String.Format("\"id\":{0}, \"name\":\"{1}\", \"uniqueId\":\"{1}\", \"status\":\"online\", \"disabled\":false," +
                         "\"lastUpdate\":\"{3}\", \"positionId\":{0}, \"groupId\":1," +
                         "\"phone\":\"{5}\", \"model\":\"{4}\", \"contact\":\"{6}\", \"category\":\"vehicle\", " +
                         "\"geofenceIds\":[], ",
                          new object[] { 
                                id, regUsers[i].name, "",
                                DateTime.UtcNow.AddMonths(-1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")+"Z", "car", 1,
                               (regUsers[i].comment == null ? "" : System.Security.SecurityElement.Escape(regUsers[i].comment))}
                        ) + "\"attributes\":{}}";
                    }
                    catch (Exception ex) { };
                };
            };

            json += "]";
            return json;
        }

        // Traccar Manager Applications
        private string API_GetPositions4TraccarWebSocket(string user)
        {
            Buddie[] bs = BUDS != null ? BUDS.Current : null;

            string json = "[";
            if (bs != null)
            {
                CRC32 crc32 = new CRC32();
                foreach (Buddie b in bs)
                {
                    if (b.IsVirtual) continue;
                    if ((!String.IsNullOrEmpty(user)) && (user != b.name)) continue;
                    string src = Buddie.BuddieToSource(b);
                    if (json.Length > 1) json += ",";

                    try
                    {
                        int id = crc32.CRC32Num(b.name);
                        json += "{" + String.Format(
                                "\"id\": {0}, \"deviceId\": {0}, \"protocol\": \"{1}\"," +
                                "\"deviceTime\": \"{2}\", " +
                                "\"fixTime\": \"{2}\", " +
                                "\"serverTime\": \"{3}\", " +
                                "\"outdated\": false, \"valid\": true," +
                                "\"latitude\": {4}, \"longitude\": {5}," +
                                "\"altitude\": 0, \"speed\": {6}, \"cource\": {7}," +
                                "\"address\": \"\", \"accuracy\": 3, \"network\": \"\", " +
                                "",
                          new object[] { 
                                id, src, b.last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")+"Z", 
                                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")+"Z", 
                                b.lat.ToString().Replace(",","."), b.lon.ToString().Replace(",","."),
                                b.speed.ToString().Replace(",","."), b.course.ToString().Replace(",",".")}
                        ) + "\"attributes\": {}}";
                    }
                    catch (Exception ex) { };
                };
            };

            json += "]";
            return json;
        }

        // Traccar Viewer 2.3 HAS BUGS -- FIX IT
        private string API_GetDevices4TraccarViewer(string user)
        {
            Buddie[] bs = null;
            bool allowVirt = false;
            if (BUDS != null)
            {
                int rc = 0;
                bs = BUDS.Current;
                if (String.IsNullOrEmpty(user))
                {
                    foreach (Buddie b in bs) if (!b.IsVirtual) rc++;
                    if (rc == 0) allowVirt = true;
                }
                else
                    allowVirt = true;
            };

            string json = "[";
            if (bs != null)
            {
                CRC32 crc32 = new CRC32();
                foreach (Buddie b in bs)
                {
                    if ((!allowVirt) && b.IsVirtual) continue;
                    if ((!String.IsNullOrEmpty(user)) && (user != b.name)) continue;
                    string src = Buddie.BuddieToSource(b);
                    if (json.Length > 1) json += ",";
                    try
                    {
                        int id = crc32.CRC32Num(b.name);
                        json += "{" + String.Format("\"id\":{0}, \"name\":\"{1}\", \"uniqueId\":\"{1}\", \"status\":\"{2}\", \"disabled\":false," +
                         "\"lastUpdate\":\"{3}\", \"positionId\":{0}, \"groupId\":1," +
                         "\"phone\":\"{5}\", \"model\":\"{4}\", \"contact\":\"{6}\", \"category\":\"vehicle\", " +
                         "\"geofenceIds\":[], ",
                          new object[] { 
                                id, b.name, (b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status)),
                                b.last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")+"Z", "car", b.regUser == null ? 0 : 1,
                               (b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment))}
                        ) + "\"attributes\":{}}";
                    }
                    catch (Exception ex) { };
                };
            };

            json += "]";
            return json;
        }

        // Traccar Viewer 2.3 HAS BUGS -- FIX IT
        private string API_GetPositions4TraccarViewer(string user)
        {
            Buddie[] bs = null;
            bool allowVirt = false;
            if (BUDS != null)
            {
                int rc = 0;
                bs = BUDS.Current;
                if (String.IsNullOrEmpty(user))
                {
                    foreach (Buddie b in bs) if (!b.IsVirtual) rc++;
                    if (rc == 0) allowVirt = true;
                }
                else
                    allowVirt = true;
            };

            string json = "[";
            if (bs != null)
            {
                CRC32 crc32 = new CRC32();
                foreach (Buddie b in bs)
                {
                    if ((!allowVirt) && b.IsVirtual) continue;
                    if ((!String.IsNullOrEmpty(user)) && (user != b.name)) continue;
                    string src = Buddie.BuddieToSource(b);
                    if (json.Length > 1) json += ",";

                    try
                    {
                        int id = crc32.CRC32Num(b.name);
                        json += "{" + String.Format(
                                "\"id\": {0}, \"deviceId\": {0}, \"protocol\": \"{1}\"," +
                                "\"deviceTime\": \"{2}\", " +
                                "\"fixTime\": \"{2}\", " +
                                "\"serverTime\": \"{3}\", " +
                                "\"outdated\": false, \"valid\": true," +
                                "\"latitude\": {4}, \"longitude\": {5}," +
                                "\"altitude\": 0, \"speed\": {6}, \"cource\": {7}," +
                                "\"address\": \"\", \"accuracy\": 3, \"network\": \"\", " +
                                "",
                          new object[] { 
                                id, src, b.last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")+"Z", 
                                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")+"Z", 
                                b.lat.ToString().Replace(",","."), b.lon.ToString().Replace(",","."),
                                b.speed.ToString().Replace(",","."), b.course.ToString().Replace(",",".")}
                        ) + "\"attributes\": {}}";
                    }
                    catch (Exception ex) { };
                };
            };

            json += "]";
            return json;
        }


        // https://www.traccar.org/osmand/
        // https://www.traccar.org/documentation/
        // https://www.traccar.org/api-reference/
        // Traccar Viewer 2.3 HAS BUGS -- FIX IT
        private void OnTraccarAPIAuth_TraccarViewer(ClientData cd, string query, string rxText, string method)
        {
            string user = "";
            int pass = 0;
            try
            {
                int aut = rxText.IndexOf("Authorization: Basic");
                if (aut < 0)
                {
                    SendAuthReq(cd.client);
                    return;
                }
                else
                {
                    aut += 21;
                    string cup = rxText.Substring(aut, rxText.IndexOf("\r\n", aut) - aut).Trim();
                    string dup = Encoding.UTF8.GetString(Convert.FromBase64String(cup));
                    string[] up = dup.Split(new char[] { ':' }, 2);
                    if ((up == null) || (up.Length != 2)) { SendAuthReq(cd.client); return; };
                    user = up[0].ToUpper();
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { SendAuthReq(cd.client); return; };
                    if (!int.TryParse(up[1], out pass)) { SendAuthReq(cd.client); return; };
                };
            }
            catch { SendAuthReq(cd.client); return; };

            if (Buddie.Hash(user) != pass)
            {
                SendAuthReq(cd.client);
                return;
            };

            if (query == "/devices") // Traccar Viewer
            {
                string json = API_GetDevices4TraccarViewer(null);
                HTTPClientSendJSON(cd.client, json);
                return;
            };            

            if (query == "/positions") // Traccar Viewer
            {
                string json = API_GetPositions4TraccarViewer(null);
                HTTPClientSendJSON(cd.client, json);
                return;
            };
            
            // default
            HTTPClientSendJSON(cd.client, "[]");
        }

        // https://www.traccar.org/osmand/
        // https://www.traccar.org/documentation/
        // https://livegpstracks.com/forum/viewtopic.php?f=12&t=376
        // https://livegpstracks.com/forum/viewtopic.php?f=42&t=970
        private void OnOsmAndTraccar(ClientData cd, string query, string rxText)
        {
            System.Collections.Specialized.NameValueCollection ask = null;
            int db = query.IndexOf("?");
            if (db >= 0)
                ask = HttpUtility.ParseQueryString(query.Substring(db + 1));
            else
            {
                HTTPClientSendError(cd.client, 415); 
                return;
            };

            if (!ask.HasKeys()) { HTTPClientSendError(cd.client, 415); return; };
            if (ask["id"] == null) { HTTPClientSendError(cd.client, 401); return; };
            string identifier = ask["id"].ToString();

            if (identifier.IndexOf("_") <= 0) { HTTPClientSendError(cd.client, 401); return; };
            string[] uc = identifier.Split(new char[] { '_' });
            string user = uc[0].ToUpper();
            int pass = 0;
            if (uc.Length > 1) int.TryParse(uc[1], out pass);

            if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };
            if (Buddie.Hash(user) != pass) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };

            double lat = 0;
            double lon = 0;
            double spd = 0;
            double crs = 0;

            if (ask["lat"] != null) double.TryParse(ask["lat"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat);
            if (ask["lon"] != null) double.TryParse(ask["lon"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon);
            if (ask["speed"] != null) double.TryParse(ask["speed"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out spd);
            if (ask["bearing"] != null) double.TryParse(ask["bearing"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out crs);

            Buddie b = new Buddie(10, user, lat, lon, (short)Math.Round(spd), (short)Math.Round(crs));
            b.lastPacket = ask.ToString();
            OnNewData(b);

            if (b.regUser != null)
                HTTPClientSendResponse(cd.client, "Welcome, " + b.name + "! " + DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy"));
            else
                HTTPClientSendResponse(cd.client, "Data received OK " + DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy"));
        }

        // own tracks (JSON)
        // https://owntracks.org/booklet/tech/http/
        private void OnOwnTracks(ClientData cd, string identifier, string rxText)
        {
            // Authorization Required //
            string user = "";
            int pass = 0;

            try
            {
                int aut = rxText.IndexOf("Authorization: Basic");
                if (aut > 0)
                {
                    aut += 21;
                    string cup = rxText.Substring(aut, rxText.IndexOf("\r\n", aut) - aut).Trim();
                    string dup = Encoding.UTF8.GetString(Convert.FromBase64String(cup));
                    string[] up = dup.Split(new char[] { ':' }, 2);
                    if ((up == null) || (up.Length != 2)) { SendAuthReq(cd.client); return; };
                    user = up[0].ToUpper();
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { SendAuthReq(cd.client); return; };
                    if (!int.TryParse(up[1], out pass)) { SendAuthReq(cd.client); return; };
                }
                else if (identifier != null)
                {
                    if (identifier.IndexOf("_") <= 0) { HTTPClientSendError(cd.client, 401); return; };
                    string[] uc = identifier.Split(new char[] { '_' });
                    user = uc[0].ToUpper();
                    if (uc.Length > 1) int.TryParse(uc[1], out pass);
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };
                    if (Buddie.Hash(user) != pass) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };
                }
                else
                {
                    HTTPClientSendError(cd.client, 401);
                    return;
                };
            }
            catch { HTTPClientSendError(cd.client, 401); return; };                        

            string json = "";
            int db = rxText.IndexOf("\r\n\r\n");
            if (db > 0)
                json = rxText.Substring(db + 4);
            else
            {
                db = rxText.IndexOf("\n\n");
                if (db > 0)
                    json = rxText.Substring(db + 2);
                else
                { HTTPClientSendError(cd.client, 415); return; };
            };
            if (String.IsNullOrEmpty(json)) { HTTPClientSendError(cd.client, 415); return; };

            Regex rx = new Regex("(['\"](\\w+)['\"][\\s\\r\\n]{0,}:[\\s\\r\\n]{0,}['\"]{0,1}([\\w.]+)['\"]{0,1})+");
            MatchCollection mc = rx.Matches(json);
            if (mc.Count > 0)
            {
                bool _loc = false;
                foreach (Match m in mc)
                    if ((m.Groups[2].Value == "_type") && (m.Groups[3].Value == "location"))
                        _loc = true;
                if (_loc)
                {
                    double lat = 0;
                    double lon = 0;
                    double spd = 0;
                    double crs = 0;

                    foreach (Match m in mc)
                    {
                        if (m.Groups[2].Value == "lat") double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat);
                        if (m.Groups[2].Value == "lon") double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon);
                        if (m.Groups[2].Value == "vel") double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out spd);
                        if (m.Groups[2].Value == "cog") double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out crs);
                    };
                    
                    Buddie b = new Buddie(9, user, lat, lon, (short)Math.Round(spd), (short)Math.Round(crs));
                    b.lastPacket = "&lat=" + lat.ToString().Replace(",", ".") + "&lon=" + lon.ToString().Replace(",", ".") + "&vel=" + spd.ToString() + "&cog=" + crs.ToString().Replace(",", ".").Replace(",", ".");
                    OnNewData(b);

                    if (b.regUser != null) // Send Friends Locations and Icons
                    {
                        string res = "[";
                        if (BUDS != null)
                        {
                            Buddie[] bup = BUDS.Current;
                            foreach (Buddie bb in bup)
                            {
                                DateTime timestamp = DateTime.UtcNow.AddDays(-7);
                                if (bb.source != 5) timestamp = bb.last.ToUniversalTime();

                                //if (bb.source == 5) continue; // everytime
                                if (bb.source == 6) continue; // static
                                if (bb.source == 7) continue; // fr24
                                if (bb.name == user) continue; // itself

                                string ic = System.Convert.ToBase64String(BinaryAPRSImage(bb.IconSymbol, 64));

                                if (res.Length > 1)
                                    res += ",";

                                if ((ic != null) && (ic != "null"))
                                    res += "{\"_type\":\"card\",\"name\":\"" + bb.name + "\",\"tid\":\"" + bb.name + "\", \"face\":\"" + ic + "\"},";
                                else
                                    res += "{\"_type\":\"card\",\"name\":\"" + bb.name + "\",\"tid\":\"" + bb.name + "\"},";

                                res += "{\"_type\":\"location\",\"lat\":" + bb.lat.ToString().Replace(",", ".") + ",\"lon\":" + bb.lon.ToString().Replace(",", ".") +
                                    ",\"t\":\"u\",\"tid\":\"" + bb.name + "\",\"tst\":" + DateTimeToUnixTimestamp(timestamp).ToString("0") +
                                    ",\"vel\":" + bb.speed.ToString() + ",\"cog\":" + bb.course.ToString() + "}";
                            };
                        };
                        res += "]";

                        //{
                        //    System.IO.FileStream fs = new FileStream(OruxPalsServerConfig.GetCurrentDir() + @"\_.txt", FileMode.Create, FileAccess.Write);
                        //    byte[] tb = System.Text.Encoding.UTF8.GetBytes(res);
                        //    fs.Write(tb, 0, tb.Length);
                        //    fs.Close();
                        //};

                        HTTPClientSendJSON(cd.client, res);
                    }
                    else
                        HTTPClientSendJSON(cd.client, "[]");
                    return;
                };
            };
            HTTPClientSendError(cd.client, 415);
        }
        
        // Big Brother GPS Android Application
        private void OnBBG(ClientData cd, string identifier, string rxText)
        {
            if (identifier.IndexOf("_") <= 0) { HTTPClientSendError(cd.client, 401); return; };
            string[] uc = identifier.Split(new char[] { '_' });
            string user = uc[0].ToUpper();
            int pass = 0;
            if (uc.Length > 1) int.TryParse(uc[1], out pass);

            if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };
            if (Buddie.Hash(user) != pass) { HTTPClientSendResponse(cd.client, "Unauthorized"); return; };

            System.Collections.Specialized.NameValueCollection ask = null;
            int db = rxText.IndexOf("\r\n\r\n");
            if (db > 0)
                ask = HttpUtility.ParseQueryString(rxText.Substring(db + 4));
            else
            {
                db = rxText.IndexOf("\n\n");
                if (db > 0)
                    ask = HttpUtility.ParseQueryString(rxText.Substring(db + 2));
                else
                { HTTPClientSendError(cd.client, 415); return; };
            };

            if (ask.HasKeys())
            {
                double lat = 0;
                double lon = 0;
                double spd = 0;
                double crs = 0;

                if (ask["latitude"] != null) double.TryParse(ask["latitude"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat);
                if (ask["longitude"] != null) double.TryParse(ask["longitude"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon);
                if (ask["speed"] != null) double.TryParse(ask["speed"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out spd);
                if (ask["bearing"] != null) double.TryParse(ask["bearing"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out crs);

                Buddie b = new Buddie(8, user, lat, lon, (short)Math.Round(spd * 3.6), (short)Math.Round(crs));
                b.lastPacket = ask.ToString();
                OnNewData(b);

                if (b.regUser != null)
                    HTTPClientSendResponse(cd.client, "Welcome, " + b.name + "! " + DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy"));
                else
                    HTTPClientSendResponse(cd.client, "Data received OK " + DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy"));
            }
            else
                HTTPClientSendError(cd.client, 406);
        }

        private void OnMMT(ClientData cd, string rxText)
        {
            // Authorization Required //
            string user = "";
            int pass = 0;
            
            try
            {
                int aut = rxText.IndexOf("Authorization: Basic");
                if (aut < 0)
                {
                    SendAuthReq(cd.client);
                    return;
                }
                else
                {
                    aut += 21;
                    string cup = rxText.Substring(aut, rxText.IndexOf("\r\n", aut) - aut).Trim();
                    string dup = Encoding.UTF8.GetString(Convert.FromBase64String(cup));
                    string[] up = dup.Split(new char[] { ':' }, 2);
                    if ((up == null) || (up.Length != 2)) { SendAuthReq(cd.client); return; };
                    user = up[0].ToUpper();
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { SendAuthReq(cd.client); return; };                    
                    if (!int.TryParse(up[1], out pass)) { SendAuthReq(cd.client); return; };                    
                };
            }
            catch { SendAuthReq(cd.client); return; };

            if (Buddie.Hash(user) != pass)
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>error</type><reason>unauthorised</reason></message>");
                return;
            };

            cd.user = user;

            System.Collections.Specialized.NameValueCollection ask = null;
            int db = rxText.IndexOf("\r\n\r\n");
            if (db > 0)
                ask = HttpUtility.ParseQueryString(rxText.Substring(db + 4));
            else
            {
                db = rxText.IndexOf("\n\n");
                if (db > 0)
                    ask = HttpUtility.ParseQueryString(rxText.Substring(db + 2));
                else
                { HTTPClientSendError(cd.client, 415); return; };
            };

            if ((ask["request"] == "start_activity") || (ask["request"] == "update_activity"))
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>activity_started</type><activity_id>" + (++mmtactCounter).ToString() + "</activity_id></message>");

                string points = ask["points"];
                if ((points != null) && (points != String.Empty))
                {
                    string[] pp = points.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if ((pp.Length > 3) && ((pp.Length % 4) == 0))
                    {
                        double lat = 0;
                        double lon = 0;
                        //double alt = 0;
                        //DateTime DT = DateTime.MinValue;
                        for (int i = 0; i < pp.Length; i += 4)
                        {
                            lat = double.Parse(pp[i + 0], System.Globalization.CultureInfo.InvariantCulture);
                            lon = double.Parse(pp[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                            //double alt = double.Parse(pp[i + 2], System.Globalization.CultureInfo.InvariantCulture);
                            //DT = UnixTimeStampToDateTime(double.Parse(pp[i + 3], System.Globalization.CultureInfo.InvariantCulture));                            
                        };
                        Buddie b = new Buddie(2, user, lat, lon, 0, 0);
                        b.lastPacket = ask.ToString();
                        OnNewData(b);
                    };
                };

                return;
            };
            if (ask["request"] == "stop_activity")
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>activity_stopped</type></message>");
                return;
            };
            if (ask["request"] == "get_time")
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>time</type><server_time>" + ((long)DateTimeToUnixTimestamp(DateTime.UtcNow)).ToString() + "</server_time></message>");
                return;
            };

            HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>error</type><reason>request not supported </reason></message>");
            return;
        }

        private byte[] BinaryAPRSImage(string symbol, int imsz)
        {
            string symb = symbol;
            string prose = "primary";
            string label = "";
            bool revcol = false;
            if (symb.Length == 2)
            {
                if (symb[0] == '\\')
                    prose = "secondary";
                else if ((symb[0] != '/') && (("#&0>AW^_acnsuvz").IndexOf(symb[1]) >= 0))
                {
                    if(("#0A^cv").IndexOf(symb[1]) >= 0) revcol = true;
                    prose = "secondary";
                    label = symb[0].ToString();
                };
                symb = symb.Substring(1);
            };
            if (imsz == 64) prose += "64";
            if (imsz == 128) prose += "128";
            string symbtable = "!\"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
            int idd = symbtable.IndexOf(symb);
            if (idd < 0) idd = 14;
            int itop = (int)Math.Truncate(idd / 16.0) * imsz;
            int ileft = (idd % 16) * imsz;
            try
            {
                System.Drawing.Image im = System.Drawing.Image.FromFile(OruxPalsServerConfig.GetCurrentDir() + @"\MAP\images\" + prose + ".png");
                System.Drawing.Image sm = new System.Drawing.Bitmap(imsz, imsz);
                System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(sm);
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImage(im, new System.Drawing.Point(-1 * ileft, -1 * itop));
                if (label != "")
                {                    
                    int fs = 9, top = 0, left = 2;
                    if (imsz == 64) { fs = 24; top = 3;  };
                    if (imsz == 128) { fs = 48; top = 6; left = 5; };
                    System.Drawing.Font f = new System.Drawing.Font("Arial", fs, System.Drawing.FontStyle.Bold);
                    System.Drawing.SizeF w = g.MeasureString(label, f);
                    System.Drawing.SolidBrush br1 = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                    System.Drawing.SolidBrush br2 = new System.Drawing.SolidBrush(System.Drawing.Color.White);

                    if(revcol)
                    { br1 = br2; br2 = new System.Drawing.SolidBrush(System.Drawing.Color.Black); };
                    g.DrawString(label, f, br1, new System.Drawing.PointF(imsz / 2 + left - 1 - w.Width / 2, imsz / 2 + top + 1 - w.Height / 2));
                    g.DrawString(label, f, br1, new System.Drawing.PointF(imsz / 2 + left + 1 - w.Width / 2, imsz / 2 + top - 1 - w.Height / 2));
                    g.DrawString(label, f, br1, new System.Drawing.PointF(imsz / 2 + left - 1 - w.Width / 2, imsz / 2 + top - 1 - w.Height / 2));
                    g.DrawString(label, f, br1, new System.Drawing.PointF(imsz / 2 + left + 1 - w.Width / 2, imsz / 2 + top + 1 - w.Height / 2));
                    g.DrawString(label, f, br2, new System.Drawing.PointF(imsz / 2 + left - w.Width / 2, imsz / 2 + top - w.Height / 2));
                };
                g.Dispose();
                im.Dispose();

                MemoryStream ms = new MemoryStream();
                sm.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                sm.Dispose();
                return ms.ToArray();                
            }
            catch (Exception exception)
            { 
            };
            return new byte[0];
        }

        private bool CheckRegisteredUser(Buddie buddie)
        {
            if (regUsers != null)
                foreach (OruxPalsServerConfig.RegUser u in regUsers)
                {
                    bool found = false;
                    if (u.name == buddie.name) found = true;
                    if(!found)
                        if ((u.services != null) && (u.services.Length > 0))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if ((svc.names != null) && (svc.names.Contains("A")) && (svc.id != null) && (svc.id == buddie.name))
                                    found = true;
                    if (found)
                    {
                        buddie.regUser = u;
                        if ((Buddie.IsNullIcon(buddie.IconSymbol)) && (u.aprssymbol != null))
                        {
                            buddie.IconSymbol = u.aprssymbol;
                            while (buddie.IconSymbol.Length < 1)
                                buddie.IconSymbol = "/" + buddie.IconSymbol;
                        };
                        return true;
                    };
                };
            return false;
        }
       
        private void OnNewData(Buddie buddie)
        {
            if(buddie.regUser == null)
                CheckRegisteredUser(buddie);

            if (BUDS != null)
                BUDS.Update(buddie);

            //forward data
            if ((buddie.regUser != null) && (buddie.regUser.forward != null) && (buddie.regUser.forward.Length > 0) && (buddie.regUser.services != null) && (buddie.regUser.services.Length > 0))
            {
                // forward to APRS Global
                if ((aprsgw != null) && (aprscfg.any2global == "yes") && (buddie.source != 3) && (buddie.regUser.forward.Contains("A")))
                {                    
                    foreach (OruxPalsServerConfig.RegUserSvc svc in buddie.regUser.services)
                        if (svc.names.Contains("A"))
                        {
                            string comment = "#ORXPLS" + buddie.source.ToString() + " ";
                            if (buddie.source == 0) comment = "#ORXPLS. ";
                            if (buddie.source == 1) comment = "#ORXPLSg ";
                            if (buddie.source == 2) comment = "#ORXPLSm ";
                            if (buddie.source == 3) comment = "#ORXPLSa ";
                            if (buddie.source == 4) comment = "#ORXPLSf ";
                            if (buddie.Comment != null) comment += buddie.Comment;
                            string aprs = buddie.APRS.Replace(buddie.name + ">", svc.id + ">").Replace("\r\n", " " + comment + "\r\n");
                            aprsgw.SendCommandWithDelay(svc.id, aprs);
                        };
                };

                // forward to Web services
                if((forwardServices != null) && (forwardServices.Length > 0))
                {
                    string toFwd = buddie.regUser.forward;
                    for (int i = 0; i < toFwd.Length; i++)
                    {
                        string l = toFwd[i].ToString();
                        string id = null;
                        foreach (OruxPalsServerConfig.RegUserSvc svc in buddie.regUser.services)
                            if (svc.names.Contains(l))
                                id = svc.id;
                        if (id != null)
                            foreach (OruxPalsServerConfig.FwdSvc fs in forwardServices)
                                if ((fs.name == l) && (fs.forward == "yes"))
                                    ForwardData2WebServices(fs, buddie, id);
                    };
                };
            };                      
        }

        public void BroadcastAIS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, true, false, false, false);
        }

        public void BroadcastAIS(byte[] data)
        {
            Broadcast(data, "", true, false, false, false);
        }

        public void BroadcastAPRS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, false, true, false, false);
        }

        public void BroadcastAPRS(byte[] data)
        {
            BroadcastAPRS(data, false);
        }

        public void BroadcastAPRS(byte[] data, bool byAIR)
        {
            Broadcast(data, "", false, true, false, true);
        }

        public void BroadcastFRS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, false, false, true, false);
        }

        public void BroadcastFRS(byte[] data)
        {
            Broadcast(data, "", false, false, true, false);
        }

        public void BroadcastWeb(BroadCastInfo bdata)
        {
            // WebSocketBrowser
            string json = API_getList(bdata.user);
            byte[] ba = GetWebSocketFrameFromString(json);
            Broadcast(ba, bdata.user, false, false, false, false, true, false);
            // WebSocketTraccar
            json = "{\"positions\": " + API_GetPositions4TraccarWebSocket(bdata.user) + ", \"devices\": " + API_GetDevices4TraccarWebSocket(bdata.user) + "}";
            ba = GetWebSocketFrameFromString(json);
            Broadcast(ba, bdata.user, false, false, false, false, false, true);
        }

        public void PingFRS()
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if (cd.state == 5) // FRS
                        cdlist.Add(cd);
                };
            foreach (ClientData cd in cdlist)
            {
                string pmsg = ChecksumAdd2Line("$FRCMD," + cd.user + ",_Ping,Inline");
                byte[] data = Encoding.ASCII.GetBytes(pmsg + "\r\n");
                try { cd.client.GetStream().Write(data, 0, data.Length); }
                catch { };
            };
        }

        public void Broadcast(byte[] data, string fromUser, bool bAIS, bool bAPRS, bool bFRS, bool byAIR)
        {
            Broadcast(data, fromUser, bAIS, bAPRS, bFRS, byAIR, false, false);
        }

        public void Broadcast(byte[] data, string fromUser, bool bAIS, bool bAPRS, bool bFRS, bool byAIR, bool bWebSocketBrowser, bool bWebSocketTraccar)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if ((cd.state == 1) && bAIS) // AIS readonly
                        cdlist.Add(cd);
                    if ((cd.state == 4)  && bAPRS) // APRS rx/tx
                    {
                        if (fromUser != cd.user)
                            if ((cd.filter != null) && (!cd.filter.PassName(fromUser)))
                                continue;

                        if ((byAIR) && (cd.filter != null) && (cd.filter.noAIR))
                            continue;

                        if (sendBack || (cd.state == 6))
                            cdlist.Add(cd);
                        else if (fromUser != cd.user)
                            cdlist.Add(cd);

                        //if (sendBack)
                        //    cdlist.Add(cd);
                        //else if (fromUser != cd.user)
                        //    cdlist.Add(cd);
                    };
                    if (!_NoSendToFRS)
                    {
                        if ((cd.state == 5) && bFRS) // FRS
                        {
                            if (sendBack)
                                cdlist.Add(cd);
                            else if (fromUser != cd.user)
                                cdlist.Add(cd);
                        };
                    };
                    if ((cd.state == 6) && bAPRS) // APRS readonly
                        cdlist.Add(cd);
                    if ((cd.state == 8) && bWebSocketBrowser) // Web Socket
                        cdlist.Add(cd);
                    if ((cd.state == 9) && bWebSocketTraccar) // Web Socket
                        cdlist.Add(cd);

                };

            foreach (ClientData cd in cdlist)
                try { cd.client.GetStream().Write(data, 0, data.Length); }
                catch { };
        }

        public void BroadcastFR24(List<byte[]> data,  ushort[] speedKnots, string[] Text)
        {                        
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;                    
                    ClientData cd = (ClientData)obj;
                    if (cd.state != 4) continue;
                    if (!cd.FR24) continue;

                    ///////////////////////////

                    List<byte> d2s = new List<byte>();
                    for (int i = 0; i < data.Count; i++)
                    {
                        if ((cd.filter == null) || (cd.filter._fr24_minspeeKnots <= 0) || (speedKnots[i] >= cd.filter._fr24_minspeeKnots))
                        {
                            if (cd.filter._fr24_textFilter.Count > 0)
                            {
                                bool add = false;
                                foreach (string filter in cd.filter._fr24_textFilter)
                                    if (Text[i].ToUpper().Contains(filter.ToUpper()))
                                        add = true;
                                if (add) d2s.AddRange(data[i]);
                            }
                            else
                                d2s.AddRange(data[i]);
                        };
                    };

                    if(d2s.Count > 0)
                        try { cd.client.GetStream().Write(d2s.ToArray(), 0, d2s.Count); }
                        catch { };                    
                };
        }

        private void ForwardData2WebServices(OruxPalsServerConfig.FwdSvc svc, Buddie buddie, string id)
        {
            try
            {
                switch (svc.type)
                {
                    case "m":
                        {
                            // Meitrack GT60 packet
                            string[] x;// [IP,PORT]
                            x = svc.ipp.Split(new string[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                            SendTCP(x[0], Convert.ToInt32(x[1]), GetPacketText_Meitrack_GT60_Protocol(buddie, id));
                        };
                        break;
                    case "x":
                        {
                            // Xenun TK-102B packet
                            string[] x;// [IP,PORT]
                            x = svc.ipp.Split(new string[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                            SendTCP(x[0], Convert.ToInt32(x[1]), GetPacketText_TK102B_Normal(buddie, id));
                        };
                        break;
                    case "o":
                        {
                            // OpenGPS
                            SendHTTP(svc.ipp + GetPacketText_OpenGPSNET_HTTPReq(buddie, id));
                        };
                        break;
                    case "b":
                        {
                            // Big Brother GPS
                            SendHTTP(svc.ipp.Replace("{ID}", id), GetPostText_BigBrotherGPS_HTTPReq(buddie, id));
                        };
                        break;
                };
            }
            catch { };
        }

        private bool IsValidQuery(string query)
        {
            if (query.Length < 11) return false;
            string subQuery = query.Substring(0, 10).ToLower();
            if (subQuery != urlPath) return false;
            return true;
        }

        private static string ChecksumHex(string str)
        {
            int checksum = 0;
            for (int i = 1; i < str.Length; i++)
                checksum ^= Convert.ToByte(str[i]);
            return checksum.ToString("X2");
        }

        public static string ChecksumAdd2Line(string line)
        {
            return line + "*" + ChecksumHex(line);
        }

        private static string GetPacketText_Meitrack_GT60_Protocol(Buddie tr, string id)
        {
            // Meitrack GT60 Protocol
            //   $$<packageflag><L>,<IMEI>,<command>,<event_code>,<(-)yy.dddddd>,<(-)xxx.dddddd>,<yymmddHHMMSS>,
            //      <Z(A-ok/V-bad)>,<N(sat count)>,<G(GSM signal)>,<Speed>,<Heading>,<HDOP>,<Altitude>,<Journey>,<Runtime>,<Base ID>,<State>,<AD>,<*checksum>\r\n 
            //   $$A,IMEI,AAA,35,55.450000,037,390000,140214040000,
            //      A,5,60,359,5,118,0,0,MCC|MNC|LAC|CI(460|0|E166|A08B),0000,0,<*checksum>\r\n 
            //
            // Example packet length & checksum: 
            //   $$E28,353358017784062,A15,OK*F4\r\n 

            string packet_prefix = "$$A";
            string packet_data = "," + id + ",AAA,35," + tr.lat.ToString("00.000000").Replace(",", ".") + "," + tr.lon.ToString("000.000000").Replace(",", ".") + "," + DateTime.UtcNow.ToString("yyMMddHHmmss") + "," +
                "A,5," +/*GSM SIGNAL*/DateTime.UtcNow.ToString("HHmmss") + "," + ((int)tr.speed).ToString() + "," + ((int)tr.course).ToString() + ",5,0,0,0," +
                // base
                ",0000,0," + "*";
            string checksum_packet = packet_prefix + (packet_data.Length + 4).ToString() + packet_data;
            byte cs = 0;
            for (int i = 0; i < checksum_packet.Length; i++) cs += (byte)checksum_packet[i];
            string full_data = checksum_packet + cs.ToString("X") + "\r\n";

            return full_data;
        }

        private static string GetPacketText_TK102B_Normal(Buddie tr, string id)
        {
            return GetPacketText_TK102B_Normal(DateTime.UtcNow, id, tr.lat, tr.lon, tr.speed, tr.course, 0);
        }

        private static string GetPacketText_OpenGPSNET_HTTPReq(Buddie tr, string id)
        {
            return GetPacketText_OpenGPSNET_HTTPReq(id, tr.lat, tr.lon, tr.speed, tr.course, 0);
        }

        private static string GetPostText_BigBrotherGPS_HTTPReq(Buddie tr, string id)
        {
            return GetPostText_BigBrotherGPS_HTTPReq(id, tr.lat, tr.lon, tr.speed, tr.course, 0);            
        }

        private static string GetPacketText_OpenGPSNET_HTTPReq(string imei, double lat, double lon, double speed, double heading, double altitude)
        {
            Random rnd = new Random();

            if (lat == 0) lat = 55.54404 + (0.5 - rnd.NextDouble()) / 10;
            if (lon == 0) lon = 37.55860 + (0.5 - rnd.NextDouble()) / 10;
            if (speed < 0) speed = 10; // kmph
            if (heading < 0) heading = rnd.Next(0, 359);
            if (altitude < 0) altitude = 251;

            //http://www.opengps.net/configure.php
            return
                "&imei=" + imei + "&data=" +
                DateTime.UtcNow.ToString("HHmmss") + ".000," +
                Math.Truncate(lat).ToString("00") + ((lat - Math.Truncate(lat)) * 60).ToString("00.0000").Replace(",", ".") + "N," + // Lat
                Math.Truncate(lon).ToString("000") + ((lon - Math.Truncate(lon)) * 60).ToString("00.0000").Replace(",", ".") + "E," + // Lon
                "2.6," + // HDOP
                altitude.ToString("0.0").Replace(",", ".") + "," + // altitude
                "3," + // 0 - noFix, 2-2D,3-3D
                heading.ToString("000.00").Replace(",", ".") + "," + //heading
                speed.ToString("0.0").Replace(",", ".") + "," + // kmph
                (speed / 1.852).ToString("0.0").Replace(",", ".") + "," + // knots
                DateTime.UtcNow.ToString("ddMMyy") + "," + // date
                "12" // sat count
                ;
            ;
        }

        private static string GetPacketText_TK102B_Normal(DateTime dt, string imei, double lat, double lon, double speed, double heading, double altitude)
        {
            Random rnd = new Random();

            if (lat == 0) lat = 55.54404 + (0.5 - rnd.NextDouble()) / 10;
            if (lon == 0) lon = 37.55860 + (0.5 - rnd.NextDouble()) / 10;
            if (speed < 0) speed = 10; // kmph
            if (heading < 0) heading = rnd.Next(0, 359);
            if (altitude < 0) altitude = 251;
            return
                dt.ToString("yyMMddHHmmss") + "," + //Serial no.(year, month, date, hour, minute, second )
                "0," + // Authorized phone no.
                "GPRMC," + // begin GPRMC sentence
                dt.ToString("HHmmss") + ".000,A," + // Time
                Math.Truncate(lat).ToString("00") + ((lat - Math.Truncate(lat)) * 60).ToString("00.0000").Replace(",", ".") + ",N," + // Lat
                Math.Truncate(lon).ToString("000") + ((lon - Math.Truncate(lon)) * 60).ToString("00.0000").Replace(",", ".") + ",E," + // Lon
                (speed / 1.852).ToString("0.00").Replace(",", ".") + "," +//Speed in knots
                heading.ToString("0").Replace(",", ".") + "," +//heading
                dt.ToString("ddMMyy") + ",,,A*62," +// Date
                "F," +//F=GPS signal is full, if it indicate " L ", means GPS signal is low
                "imei:" + imei + "," + //imei
                // CRC
                "05," +// GPS fix (03..10)
                altitude.ToString("0.0").Replace(",", ".") //altitude
                //",F:3.79V,0"//0-tracker not charged,1-charged
                // ",122,13990,310,01,0AB0,345A" //
            ;

            // lat: 5722.5915 -> 57 + (22.5915 / 60) = 57.376525
        }

        private static string GetPostText_BigBrotherGPS_HTTPReq(string imei, double lat, double lon, double speed, double heading, double altitude)
        {
            Random rnd = new Random();

            if (lat == 0) lat = 55.54404 + (0.5 - rnd.NextDouble()) / 10;
            if (lon == 0) lon = 37.55860 + (0.5 - rnd.NextDouble()) / 10;
            if (speed < 0) speed = 10; // kmph
            if (heading < 0) heading = rnd.Next(0, 359);
            if (altitude < 0) altitude = 251;

            // http://livegpstracks.com/bbg.php
            // http://livegpstracks.com/bbg.php?imei=anonymous_0
            return
                  "altitude=" + altitude.ToString().Replace(",", ".") +
                 "&latitude=" + lat.ToString().Replace(",", ".") +
                 "&longitude=" + lon.ToString().Replace(",", ".") +
                 "&speed=" + speed.ToString().Replace(",", ".") +
                 "&bearing=" + heading.ToString().Replace(",", ".") +
                 "&accuracy=3" +
                 "&provider=network" +
                 "&time=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + ".00";
        }

        private static void SendTCP(string IP, int Port, string data)
        {
            try
            {
                TcpClient tc = new TcpClient();
                tc.Connect(IP, Port);
                byte[] buf = System.Text.Encoding.GetEncoding(1251).GetBytes(data);
                tc.GetStream().Write(buf, 0, buf.Length);
                tc.Close();
            }
            catch (Exception ex) { throw ex; };
        }

        private static void SendHTTP(string query)
        {
            try
            {
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(query);
                System.Net.WebResponse rp = wr.GetResponse();
                System.IO.Stream ss = rp.GetResponseStream();
                System.IO.StreamReader sr = new System.IO.StreamReader(ss);
                string rte = sr.ReadToEnd();
                sr.Close();
                ss.Close();
                rp.Close();
            }
            catch (Exception ex) { throw ex; };
        }

        private static void SendHTTP(string query, string body)
        {
            try
            {
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(query);
                wr.Method = "POST";
                wr.ContentType = "application/x-www-form-urlencoded";

                byte[] byteArray = new ASCIIEncoding().GetBytes(body);
                wr.ContentLength = byteArray.Length;
                Stream dataStream = wr.GetRequestStream();
                dataStream.Write(byteArray, 0, byteArray.Length);
                dataStream.Close();

                System.Net.WebResponse rp = wr.GetResponse();
                System.IO.Stream ss = rp.GetResponseStream();
                System.IO.StreamReader sr = new System.IO.StreamReader(ss);
                string rte = sr.ReadToEnd();
                sr.Close();
                ss.Close();
                rp.Close();
            }
            catch (Exception ex) { throw ex; };
        } 

        private static bool IsConnected(TcpClient Client)
        {
            if (!Client.Connected) return false;
            if (Client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                try
                {
                    if (Client.Client.Receive(buff, SocketFlags.Peek) == 0)
                        return false;
                }
                catch
                {
                    return false;
                };
            };
            return true;
        }

        private static void SendAuthReq(TcpClient Client)
        {
            string Str =
                        "HTTP/1.1 401 Unauthorized\r\n" +
                        "Date: " + DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") + " GMT\r\n" +
                        "WWW-Authenticate: Basic realm=\"Map My Tracks API\"\r\n" +
                        "Vary: Accept-Encoding\r\nContent-Length: 12\r\n" +
                        "Server: " + softver + "\r\n" +
                        "Author: milokz@gmail.com\r\n" +                        
                        "Connection: close\r\n" +
                        "Content-Type: text/html\r\n\r\n" +
                        "Unauthorized";
            byte[] Buffer = Encoding.ASCII.GetBytes(Str);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static void SendAuthReq(TcpClient Client, string realm, string html)
        {
            string Content = (String.IsNullOrEmpty(html) ? "Unauthorized: " + realm : html);
            string Str =
                        "HTTP/1.1 401 Unauthorized\r\n" +
                        "Date: " + DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") + " GMT\r\n" +
                        "WWW-Authenticate: Basic realm=\"" + realm + "\"\r\n" +
                        "Vary: Accept-Encoding\r\n"+
                        "Content-Length: " + Content.Length.ToString() + "\r\n" +
                        "Server: " + softver + "\r\n" +
                        "Author: milokz@gmail.com\r\n" +                        
                        "Connection: close\r\n" +
                        "Content-Type: text/html\r\n\r\n" +
                        Content;
            byte[] Buffer = Encoding.GetEncoding(1251).GetBytes(Str);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); }
            catch { }
            Client.Close();
        }

        private static void HTTPClientSendResponse(TcpClient Client, string text)
        {
            string Headers = 
                "HTTP/1.1 200 OK\r\n"+
                "Server: " + softver + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-Type: text/html, charset=windows-1251\r\n" +
                "Content-Length: " + text.Length + "\r\n\r\n";
            byte[] Buffer = Encoding.GetEncoding(1251).GetBytes(Headers + text);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static void HTTPClientSendBinary(TcpClient Client, string mimeType, byte[] data)
        {
            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-Type: " + mimeType + "\r\n" +
                "Content-Length: " + data.Length + "\r\n\r\n";
            byte[] Buffer = Encoding.GetEncoding(1251).GetBytes(Headers );
            try { 
                Client.GetStream().Write(Buffer, 0, Buffer.Length);
                Client.GetStream().Write(data, 0, data.Length); 
            }
            catch { }
            Client.Close();
        }

        private static void HTTPClientSendXML(TcpClient Client, string mimeType, string text)
        {
            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-Type: " + mimeType + ", charset=utf-8\r\n" +
                "Content-Length: " + text.Length + "\r\n\r\n";
            byte[] Buffer = Encoding.UTF8.GetBytes(Headers + text);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); }
            catch { }
            Client.Close();
        }

        private static void HTTPClientSendJSON(TcpClient Client, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +
                "Connection: close\r\n" +
                "Content-Type: application/json, charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString() + "\r\n\r\n";
            byte[] hdr = Encoding.UTF8.GetBytes(Headers);
            try { Client.GetStream().Write(hdr, 0, hdr.Length); }
            catch { }
            try { Client.GetStream().Write(body, 0, body.Length); }
            catch { }
            Client.Close();
        }

        private static void HTTPClientSendJSON(TcpClient Client, string text, string cookie)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Set-cookie: " + cookie + "\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-Type: application/json, charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString() + "\r\n\r\n";
            byte[] hdr = Encoding.UTF8.GetBytes(Headers);
            try { Client.GetStream().Write(hdr, 0, hdr.Length); }
            catch { }
            try { Client.GetStream().Write(body, 0, body.Length); }
            catch { }
            Client.Close();
        }

        private static void HTTPClientRedirect(TcpClient Client, string url)
        {
            string html = "<html><body>Redirected to <a href=\"" + url + "\">" + url + "</a></body></html>";
            byte[] body = Encoding.ASCII.GetBytes(html);
            string Headers =
                "HTTP/1.1 307 Temporary Redirect\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Location: "+url+"\r\n" +
                "Content-Type: text/html\r\n" +
                "Content-Length: " + body.Length.ToString() + "\r\n\r\n";
            byte[] hdr = Encoding.UTF8.GetBytes(Headers);
            try { Client.GetStream().Write(hdr, 0, hdr.Length); }
            catch { }
            try { Client.GetStream().Write(body, 0, body.Length); }
            catch { }
            Client.Close();
        }

        private static void HTTPClientSendFile(TcpClient Client, string fileName, string subdir)
        {
            string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\" + subdir + @"\" + fileName.Replace("..", "00").Replace("%20"," ");
            if (!File.Exists(ffn))
            {
                HTTPClientSendError(Client, 404);
                Client.Close();
                return;
            };

            string ctype = "text/html; charset=utf-8";
            System.IO.FileStream fs = new FileStream(ffn, FileMode.Open, FileAccess.Read);
            string ext = Path.GetExtension(ffn).ToLower();
            if (ext == ".css") ctype = "";// "text/css; charset=windows-1251";
            if (ext == ".js") ctype = "text/javascript; charset=windows-1251";
            if (ext == ".png") ctype = "image/png";
            if (ext == ".gif") ctype = "image/gif";
            if ((ext == ".txt") || (ext == ".csv") || (ext == ".readme")) ctype = "text/plain";
            if ((ext == ".jpg") || (ext == ".jpeg")) ctype = "image/jpeg";
            if ((ext == ".xml") || (ext == ".kml")) ctype = "text/xml; charset=utf-8";
            if ((ext == ".apk") || (ext == ".exe") || (ext == ".rar") 
                || (ext == ".zip") || (ext == ".bin") || (ext == ".kmz")
                || (ext == ".rte") || (ext == ".gpx") || (ext == ".wpt")) ctype = "application/octet-stream";

            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-Type: " + ctype + "\r\n" +
                "Content-Length: " + fs.Length + "\r\n\r\n";
            byte[] Buffer = new byte[8192];
            try {
                Buffer = Encoding.ASCII.GetBytes(Headers);
                Client.GetStream().Write(Buffer, 0, Buffer.Length);
                int btr = (int)(fs.Length - fs.Position);
                while (btr > 0)
                {
                    int rdd = fs.Read(Buffer, 0, Buffer.Length > btr ? btr : Buffer.Length);
                    Client.GetStream().Write(Buffer, 0, rdd);
                    btr -= rdd;
                };                
            }
            catch { }
            fs.Close();
            Client.Close();
        }

        private static void HTTPClientSendError(TcpClient Client, int Code)
        {
            string CodeStr = Code.ToString() + " " + ((HttpStatusCode)Code).ToString();
            string Html = "<html><body><h1>" + CodeStr + "</h1></body></html>";
            string Str = 
                "HTTP/1.1 " + CodeStr + "\r\n"+
                "Server: " + softshort + "\r\n" +
                "Author: milokz@gmail.com\r\n" +                
                "Connection: close\r\n" +
                "Content-type: text/html\r\n"+
                "Content-Length:" + Html.Length.ToString() + "\r\n\r\n" + 
                Html;
            byte[] Buffer = Encoding.ASCII.GetBytes(Str);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static double DateTimeToUnixTimestamp(DateTime dateTime)
        {
            return (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
        }

        private static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public static string HeadingToText(int hdg)
        {
            int d = (int)Math.Round(hdg / 22.5);
            switch (d)
            {
                case 0: return "N";
                case 1: return "NNE";
                case 2: return "NE";
                case 3: return "NEE";
                case 4: return "E";
                case 5: return "SEE";
                case 6: return "SE";
                case 7: return "SSE";
                case 8: return "S";
                case 9: return "SSW";
                case 10: return "SW";
                case 11: return "SWW";
                case 12: return "W";
                case 13: return "NWW";
                case 14: return "NW";
                case 15: return "NNW";
                case 16: return "N";
                default: return "";
            };
        }

        private Mutex lpMutex = new Mutex();
        
        public static System.Drawing.Image GenerateQRCode(string url, string text, System.Drawing.Image img)
        {
            // https://en.wikipedia.org/wiki/Geo_URI_scheme
            // geo:37.786971,-122.399677
            ThoughtWorks.QRCode.Codec.QRCodeEncoder qrCodeEncoder = new ThoughtWorks.QRCode.Codec.QRCodeEncoder();
            qrCodeEncoder.QRCodeEncodeMode = ThoughtWorks.QRCode.Codec.QRCodeEncoder.ENCODE_MODE.BYTE;
            qrCodeEncoder.QRCodeScale = 5;
            qrCodeEncoder.QRCodeVersion = 7;
            qrCodeEncoder.QRCodeErrorCorrect = ThoughtWorks.QRCode.Codec.QRCodeEncoder.ERROR_CORRECTION.M;
            System.Drawing.Bitmap bmp = qrCodeEncoder.Encode(url);
            System.Drawing.Bitmap bmpE = new System.Drawing.Bitmap(bmp.Width + 8, bmp.Height + 8 /*+ 20*/);
            System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmpE);
            g.FillRectangle(System.Drawing.Brushes.White, new System.Drawing.Rectangle(0, 0, bmpE.Width, bmpE.Height));
            g.DrawImage(bmp, 4, 4);
            System.Drawing.Font ff = new System.Drawing.Font("MS Sans Serif", 14, System.Drawing.FontStyle.Bold);
            System.Drawing.SizeF ms = g.MeasureString(text, ff);
            //g.DrawString(text, ff, System.Drawing.Brushes.Black, bmpE.Width / 2 - ms.Width / 2, bmpE.Height - 34);
            //if (img != null)
            //{
            //    g.FillRectangle(System.Drawing.Brushes.White, new System.Drawing.Rectangle(bmpE.Width / 2 - img.Width / 2 - 2, bmp.Height / 2 + 16 - img.Height / 2 - 2, img.Width + 4, img.Height + 4));
            //    g.DrawImage(img, bmpE.Width / 2 - img.Width / 2, bmp.Height / 2 + 16 - img.Height / 2);
            //};
            g.Dispose();
            bmp.Dispose();
            return bmpE;
        }

        private static Mutex consoleMutex = new Mutex();
        public static void WriteToConsole(string text)
        {
            consoleMutex.WaitOne();
            Console.Write(text);
            consoleMutex.ReleaseMutex();
        }

        public static void WriteLineToConsole(string line)
        {
            consoleMutex.WaitOne();
            Console.WriteLine(line);
            consoleMutex.ReleaseMutex();
        }

        private class ClientData
        {
            // 0 - undefined; 1 - listen (AIS); 2 - gpsgate; 3 - mapmytracks; 4 - APRS; 5 - FRS (GPSGate by TCP); 6 - listen (APRS)            
            // 7 - AFSKMODEM, 8 - WebSocket(Browser), 9 - WebSocket(Traccar)
            // 100 - HTTP client by OnlyHTTPPort, 101 - AIS by OnlyAISPort, 106 - APRS by OnlyAPRSPort, 105 - FRS by OnlyFRSPort
            public byte state; 
            public Thread thread;
            public TcpClient client;
            public DateTime connected;
            public ulong id;
            public Stream stream;
            public int ServerPort = 0;

            public ClientData(Thread thread, TcpClient client, ulong clientID)
            {
                this.id = clientID;
                this.connected = DateTime.UtcNow;
                this.state = 0;
                this.thread = thread;
                this.client = client;
                this.stream = client.GetStream();
            }

            public string user = "unknown";
            public double[] lastFixYX = new double[] { 0, 0, 0 };

            public ClientAPRSFilter filter = null;
            public DateTime lastNarodMon = DateTime.MinValue;
            public bool FR24 = false;

            public string SetFilter(string filter)
            {
                this.filter = new ClientAPRSFilter(filter);
                return this.filter.ToString();
            }

            public string IP { get { return ((IPEndPoint)this.client.Client.RemoteEndPoint).Address.ToString(); } }
            public int Port { get { return ((IPEndPoint)this.client.Client.RemoteEndPoint).Port; } }
        }

        public class ClientAPRSFilter
        {
            private string filter = "";
            public int inMyRadiusKM = -2;
            public int maxStaticObjectsCount = -2;
            public string[] allowStartsWith = new string[0];
            public string[] allowEndsWith = new string[0];
            public string[] allowFullName = new string[0];
            public string[] denyStartsWith = new string[0];
            public string[] denyEndsWith = new string[0];
            public string[] denyFullName = new string[0];
            public int[] narodmon = new int[0];
            public bool noAIR = false;

            public string _fr24_zone = "F";
            public float _fr24_zoneW = -1;
            public float _fr24_zoneH = -1;
            public short _fr24_interval = -1;
            public short _fr24_minspeeKnots = -1;
            public List<string> _fr24_textFilter = new List<string>();

            public ClientAPRSFilter(string filter)
            {
                this.filter = filter;
                Init();
            }

            private void Init()
            {                
                string ffparsed = "";
                Match m = Regex.Match(filter, @"me/([\d\/\-]+)");
                if (m.Success)
                {
                    string[] rc = m.Groups[1].Value.Split(new char[] { '/' }, 2);
                    if (rc.Length > 0)
                    {
                        int.TryParse(rc[0], out inMyRadiusKM);
                        if (rc.Length > 1) int.TryParse(rc[1], out maxStaticObjectsCount);
                        ffparsed += m.ToString() + " ";
                    };
                };
                m = Regex.Match(filter, @"\+sw/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowStartsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\+ew/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowEndsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\+fn/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowFullName = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-sw/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyStartsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-ew/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyEndsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-fn/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyFullName = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                    if ((denyFullName != null) && (denyFullName.Length > 0))
                        foreach (string nm in denyFullName)
                            if (nm.ToUpper() == "AIR")
                                noAIR = true;
                };
                m = Regex.Match(filter, @"\+nm/{0,1}(\d{0,})/{0,1}(\d{0,})"); // narodmon +nm/radius/limit
                if (m.Success)
                {
                    narodmon = new int[] { 10, 50 };
                    if (!String.IsNullOrEmpty(m.Groups[1].Value)) narodmon[0] = int.Parse(m.Groups[1].Value);
                    if (narodmon[0] < 1) narodmon[0] = 1;
                    if (narodmon[0] > 100) narodmon[0] = 100;
                    if (!String.IsNullOrEmpty(m.Groups[2].Value)) narodmon[1] = int.Parse(m.Groups[2].Value);
                    if (narodmon[1] < 5) narodmon[1] = 5;
                    if (narodmon[1] > 50) narodmon[1] = 50;
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"FR24/([A-Z\d/\-]+)"); // FR24/ZoneWHSize/Interval/minspeedKnots/FILTER default FR24/20/15/0
                // ZoneWHSize can be: F20 (Zone with 20 degrees width & height) N - to north only; E - to east only; S - to south only; W - to west only;
                // NE - 0°..90°; SE - 90°..180°; SW - 180°..270°; NW - 270°..360°; EN - 0°..90°; ES - 90°..180°; WS - 180°..270°; WN - 270°..360°; 
                // ZoneWHSize can be: R10-20 -- 10 - zone 10 degrees width and 20 degrees height
                // ZoneWHSize can be: S10-20 -- zone 10 degrees width & 20 degrees to south
                // ZoneWHSize can be: NW5-15 -- zone 5 degrees to west and 15 degrees to north
                // FR24/ZoneWHSize/Interval/minspeed/FILTER
                // FR24/ZoneWHSize/Interval/minspeed/FILTER/FILTER/FILTER.../.../
                if (m.Success)
                {
                    string[] ris = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ris.Length > 0)
                    {
                        m = Regex.Match(ris[0], @"([FNEWSnews]{0,2})(\d+)[-]{0,1}(\d{0,})");
                        if (m.Success)
                        {
                            float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _fr24_zoneW);
                            if (!String.IsNullOrEmpty(m.Groups[1].Value)) 
                                _fr24_zone = m.Groups[1].Value.ToUpper();
                            if (!String.IsNullOrEmpty(m.Groups[3].Value))
                                float.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _fr24_zoneH);
                        };
                    };
                    if (ris.Length > 1)
                        short.TryParse(ris[1], out _fr24_interval);
                    if (ris.Length > 2)
                        short.TryParse(ris[2], out _fr24_minspeeKnots);
                    if (ris.Length > 3)
                    {
                        int rl = 3;
                        do { _fr24_textFilter.Add(ris[rl++]); }
                        while (ris.Length > rl);
                    };
                        
                };                
                filter = ffparsed.Trim();                
            }

            public bool PassName(string name)
            {
                if((name == null)||(name == "")) return true;
                if (filter == "") return true;
                name = name.ToUpper();
                bool pass = true;
                if ((allowStartsWith != null) && (allowStartsWith.Length > 0))
                {
                    pass = false;
                    foreach (string sw in allowStartsWith)
                        if (name.StartsWith(sw)) return true;
                };
                if ((allowEndsWith != null) && (allowEndsWith.Length > 0))
                {
                    pass = false;
                    foreach (string ew in allowEndsWith)
                        if (name.EndsWith(ew)) return true;
                };
                if ((allowFullName != null) && (allowFullName.Length > 0))
                {
                    pass = false;
                    foreach (string fn in allowFullName)
                        if (name == fn)
                            return true;
                };
                //
                if ((denyStartsWith != null) && (denyStartsWith.Length > 0))
                    foreach (string sw in denyStartsWith)
                        if (name.StartsWith(sw)) return false;
                if ((denyEndsWith != null) && (denyEndsWith.Length > 0))
                    foreach (string ew in denyEndsWith)
                        if (name.EndsWith(ew)) return false;
                if ((denyFullName != null) && (denyFullName.Length > 0))
                    foreach (string fn in denyFullName)
                        if (name == fn)
                            return false;
                return pass;
            }

            public override string ToString()
            {
                return filter;
            }
        }
    }

    [Serializable]
    public class OruxPalsServerConfig
    {
        public class RegUserSvc
        {
            [XmlAttribute]
            public string names = "";
            [XmlAttribute]
            public string id = "";
        }

        public class RegUsers
        {            
            [XmlElement("u")]
            public RegUser[] users;
        }

        public class RegUser
        {
            [XmlAttribute]
            public string name;
            [XmlAttribute]
            public string phone;
            [XmlAttribute]
            public string forward;
            [XmlAttribute]
            public string aprssymbol = "/>";
            [XmlAttribute]
            public string comment = "";            
            [XmlElement("service")]
            public RegUserSvc[] services;
        }

        public class FwdSvcs
        {
            [XmlElement("service")]
            public FwdSvc[] services;
        }

        public class FwdSvc
        {
            [XmlAttribute]
            public string name = "";
            [XmlAttribute]
            public string type = "?";
            [XmlAttribute]
            public string forward = "no";
            [XmlText]
            public string ipp = "127.0.0.1:0";
        }

        public string ServerName = "OruxPalsServer";
        public int ListenPort = 12015;
        public int OnlyAPRSPort = 0;
        public int OnlyAISPort = 0;
        public int OnlyHTTPPort = 0;
        public int OnlyFRSPort = 0;
        public ushort maxClientAlive = 60;
        public byte maxHours = 48;
        public ushort greenMinutes = 60;
        public int KMLObjectsRadius = 5;
        public int KMLObjectsLimit = 50;
        public string urlPath = "oruxpals";
        public string adminName = "admin";
        public string adminPass = "oruxpalsadmin";
        public bool disableAIS = false;
        public string sendBack = "no";
        public string callsignToUser = "yes";
        public string infoIP = "127.0.0.1";
        public string banlist = "";
        [XmlElement("users")]
        public RegUsers users;
        [XmlElement("APRSIS")]
        public APRSISConfig aprsis;
        [XmlElement("forwardServices")]
        public FwdSvcs forwardServices;
        [XmlElement("BlackListIP")]
        public string[] BlackListIP = new string[0];
        [XmlElement("LocalNetwork")]
        public string[] LocalNetwork = new string[0];

        public static OruxPalsServerConfig LoadFile(string file)
        {
            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(OruxPalsServerConfig));
            System.IO.StreamReader reader = System.IO.File.OpenText(GetCurrentDir() + @"\" + file);
            OruxPalsServerConfig c = (OruxPalsServerConfig)xs.Deserialize(reader);
            reader.Close();
            return c;
        }

        public static string GetCurrentDir()
        {
            string fname = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase.ToString();
            fname = fname.Replace("file:///", "");
            fname = fname.Replace("/", @"\");
            fname = fname.Substring(0, fname.LastIndexOf(@"\") + 1);
            return fname;
        }
    }

    public class CRC32
    {
        private const uint poly = 0xEDB88320;
        private uint[] checksumTable;

        public CRC32()
        {
            checksumTable = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint el = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((el & 1) != 0)
                        el = (poly ^ (el >> 1));
                    else
                        el = (el >> 1);
                };
                checksumTable[index] = el;
            };
        }

        public uint CRC32Num(byte[] data)
        {
            uint res = 0xFFFFFFFF;
            for (int i = 0; i < data.Length; i++)
                res = checksumTable[(res & 0xFF) ^ (byte)data[i]] ^ (res >> 8);
            return ~res;
        }

        public int CRC32Num(string data)
        {
            int res = (int)CRC32Num(Encoding.ASCII.GetBytes(data));
            if (res < 0) res *= -1;
            return res; ;
        }

        public byte[] CRC32Arr(byte[] data, bool isLittleEndian)
        {
            uint res = CRC32Num(data);
            byte[] hash = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (isLittleEndian)
                    hash[i] = (byte)((res >> (24 - i * 8)) & 0xFF);
                else
                    hash[i] = (byte)((res >> (i * 8)) & 0xFF);
            };
            return hash;
        }

        public ulong CRC32mod2Num(byte[] data)
        {
            uint res1 = 0xFFFFFFFF;
            uint res2 = 0xFFFFFFFF;

            for (int i = 0; i < data.Length; i++)
            {
                if (i % 2 == 0)
                    res1 = checksumTable[(res1 & 0xFF) ^ (byte)data[i]] ^ (res1 >> 8);
                else
                    res2 = checksumTable[(res2 & 0xFF) ^ (byte)data[i]] ^ (res2 >> 8);
            };

            res1 = ~res1;
            res2 = ~res2;

            ulong res = 0;
            for (int i = 0; i < 4; i++)
            {
                ulong u1 = ((res1 >> (24 - i * 8)) & 0xFF);
                ulong u2 = ((res2 >> (24 - i * 8)) & 0xFF);
                res += u1 << (56 - i * 16);
                res += u2 << (56 - i * 16 - 8);
            };

            return res;
        }

        public byte[] CRC32mod2Arr(byte[] data, bool isLittleEndian)
        {
            uint res1 = 0xFFFFFFFF;
            uint res2 = 0xFFFFFFFF;

            for (int i = 0; i < data.Length; i++)
            {
                if (i % 2 == 0)
                    res1 = checksumTable[(res1 & 0xFF) ^ (byte)data[i]] ^ (res1 >> 8);
                else
                    res2 = checksumTable[(res2 & 0xFF) ^ (byte)data[i]] ^ (res2 >> 8);
            };

            res1 = ~res1;
            res2 = ~res2;

            byte[] hash = new byte[8];
            for (int i = 0; i < 4; i++)
            {
                if (isLittleEndian)
                {
                    hash[i * 2] = (byte)((res1 >> (24 - i * 8)) & 0xFF);
                    hash[i * 2 + 1] = (byte)((res2 >> (24 - i * 8)) & 0xFF);
                }
                else
                {
                    hash[7 - i * 2] = (byte)((res1 >> (24 - i * 8)) & 0xFF);
                    hash[7 - i * 2 - 1] = (byte)((res2 >> (24 - i * 8)) & 0xFF);
                };
            };
            return hash;
        }
    } 
}
