using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace OruxPals
{
    public class APRSISGateWay
    {

        private APRSISConfig cfg = new APRSISConfig();
        private string server = "127.0.0.1";
        private int port = 14580;

        private TcpClient tcp_client = null;
        private Thread tcp_listen = null;
        private Hashtable timeoutedCmdList = new Hashtable();

        private string _state = "idle";
        private bool _active = false;

        public string State
        {
            get
            {
                return _state;
            }
        }

        public string lastRX = "";
        public string lastTX = "";
        
        public APRSISGateWay(APRSISConfig cfg)
        {
            this.cfg = cfg;
            string[] ipp = cfg.url.Split(new char[] { ':' }, 2);
            server = ipp[0];
            port = int.Parse(ipp[1]);
        }
       
        public void Start()
        {
            if (_active) return;
            lock (timeoutedCmdList) timeoutedCmdList.Clear();
            _active = true;
            _state = "starting";
            tcp_listen = new Thread(ReadIncomingDataThread);
            tcp_listen.Start();
            (new Thread(DelayedUpdate)).Start();
            _state = "started";
        }

        public void Stop()
        {
            if (!_active) return;
            lock (timeoutedCmdList) timeoutedCmdList.Clear();
            _state = "stopping";
            _active = false;
            if (tcp_client != null)
            {
                tcp_client.Close();
                tcp_client = null;
            };
            _state = "stopped";
        }

        public bool Connected
        {
            get
            {
                if (!_active) return false;
                if (tcp_client == null) return false;
                return IsConnected(tcp_client);
            }
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

        private void ReadIncomingDataThread()
        {
            uint incomingMessagesCounter = 0;
            DateTime lastIncDT = DateTime.UtcNow;
            while (_active)
            {
                // connect
                if ((tcp_client == null) || (!IsConnected(tcp_client)))
                {
                    tcp_client = new TcpClient();
                    try
                    {
                        _state = "connecting";
                        tcp_client.Connect(server, port);
                        string txt2send = "user " + cfg.user + " pass " + cfg.password + " vers " + OruxPalsServer.softver + ((cfg.filter != null) && (cfg.filter != String.Empty) ? " filter " + cfg.filter : "") + "\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_client.GetStream().Write(arr, 0, arr.Length);
                        incomingMessagesCounter = 0;
                        _state = "Connected";
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch
                    {
                        _state = "disconnected";
                        tcp_client.Close();
                        tcp_client = new TcpClient();
                        Thread.Sleep(5000);
                        continue;
                    };
                };

                // ping (keep alive connection)
                if (DateTime.UtcNow.Subtract(lastIncDT).TotalMinutes > 3)
                {
                    try
                    {
                        string txt2send = "#ping\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_client.GetStream().Write(arr, 0, arr.Length);
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch { continue; };
                };                
                
                // read
                try
                {
                    byte[] data = new byte[65536];
                    int ava = 0;
                    if ((ava = tcp_client.Available) > 0)
                    {
                        lastIncDT = DateTime.UtcNow;
                        int rd = tcp_client.GetStream().Read(data, 0, ava > data.Length ? data.Length : ava);
                        string txt = System.Text.Encoding.GetEncoding(1251).GetString(data, 0, rd);
                        string[] lines = txt.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                            do_incoming(line, ++incomingMessagesCounter);
                    };
                }
                catch
                {
                    tcp_client.Close();
                    tcp_client = new TcpClient();
                    Thread.Sleep(5000);
                    continue;
                };
                

                Thread.Sleep(100);
            };
        }        

        private void do_incoming(string line, uint incomingMessagesCounter)
        {
            if (incomingMessagesCounter == 2)
            {
                if (line.IndexOf(" verified") > 0)
                    _state = "Connected rx/tx, " + line.Substring(line.IndexOf("server"));
                if (line.IndexOf(" unverified") > 0)
                    _state = "Connected rx only, " + line.Substring(line.IndexOf("server"));
            };
                
            
            // Console.WriteLine(line); // DEBUG //            

            bool isComment = line.IndexOf("#") == 0;
            if (!isComment)
            {
                lastRX = DateTime.UtcNow.ToString() + " " + line;
                onPacket(line);
            };
        }

        public delegate void onAPRSGWPacket(string line);
        public onAPRSGWPacket onPacket;
                

        public bool SendCommand(string cmd)
        {
            if (Connected)
            {
                lastTX = DateTime.UtcNow.ToString() + " " + cmd;
                byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(cmd);
                try
                {
                    tcp_client.GetStream().Write(arr, 0, arr.Length);
                    return true;
                }
                catch
                {};
            };
            return false;
        }

        public void SendCommandWithDelay(string callsign, string cmd)
        {
            lock (timeoutedCmdList)
                timeoutedCmdList[callsign] = cmd;
        }

        private void DelayedUpdate()
        {
            int timer = 0;
            while (_active)
            {
                timer++;
                if (timer == 60)
                {
                    timer = 0;
                    List<string> keys = new List<string>();
                    lock (timeoutedCmdList)
                    {
                        foreach (string key in timeoutedCmdList.Keys)
                            keys.Add(key);
                        foreach (string key in keys)
                        {
                            string cmd = (string)timeoutedCmdList[key];
                            timeoutedCmdList.Remove(key);
                            SendCommand(cmd);
                        };
                    };
                };
                Thread.Sleep(1000);
            };
        }
    }

    [Serializable]
    public class APRSISConfig
    {
        [XmlAttribute]
        public string user = "ORXPLS-GW";
        [XmlAttribute]
        public string password = "-1";
        [XmlAttribute]
        public string filter = "";
        [XmlAttribute]
        public string global2ais = "no";
        [XmlAttribute]
        public string global2aprs = "no";
        [XmlAttribute]
        public string global2frs = "no";
        [XmlAttribute]
        public string aprs2global = "no";
        [XmlAttribute]
        public string any2global = "no";
        [XmlText]
        public string url = "127.0.0.1:14580";
    }
}
