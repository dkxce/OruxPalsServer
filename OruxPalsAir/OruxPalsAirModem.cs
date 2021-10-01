using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;

// https://github.com/naudio/NAudio
using NAudio;

namespace OruxPalsAir
{
    public class OruxPalsAirModem
    {
        public const string softver = "OruxPalsAir 0.1a";
        public const string serviceName = "OruxPalsAirModem";

        private string host = "127.0.0.1";
        private int port = 12015;
        private string callsign = "AFSKMODEM";
        private string password = "5140038";
        private string filter = "";
        private bool readAir = false;
        private bool writeAir = false;
        private int readAudioDeviceNo = 0;
        private int writeAudioDeviceNo = 0;
        private float writeAudioVolume = -1;

        private TcpClient tcpc = null;
        private Thread tcpt = null;
        private bool tcpr = false;        

        private ReadWave.DirectAudioAFSKDemodulator airl = null;
        private ax25.AFSK1200Modulator mod;

        public OruxPalsAirModem()
        {
            OruxPalsAirConfig config = OruxPalsAirConfig.LoadFile("OruxPalsAir.xml");
            if ((config.server != null) && (config.server != String.Empty))
            {
                string[] sp = config.server.Split(new char[] { ':' }, 2);
                host = sp[0];
                port = int.Parse(sp[1]);
            };
            if ((config.callsign != null) && (config.callsign != String.Empty)) callsign = config.callsign;
            if ((config.password != null) && (config.password != String.Empty)) password = config.password;
            if ((config.filter != null) && (config.filter != String.Empty)) filter = " filter " + config.filter;
            readAir = config.readAir == "yes";
            writeAir = config.writeAir == "yes";
            readAudioDeviceNo = config.readAudioDeviceNo;
            writeAudioDeviceNo = config.writeAudioDeviceNo;
            writeAudioVolume = config.writeAudioVolume;

            mod = new ax25.AFSK1200Modulator(44100);
            mod.txDelayMs = config.txDelayMs;
            mod.txTailMs = config.txTailMs;
        }

        public void Start()
        {
            string[] rad = ReadWave.DirectAudioAFSKDemodulator.WaveInDevices();
            string[] wad = ReadWave.DirectAudioAFSKDemodulator.WaveOutDevices();
            Console.WriteLine("Starting {0} ...", softver);
            Console.WriteLine("  remote APRS-IS: {0}:{1}{2}", host, port, filter);
            if (readAir)
                Console.WriteLine("Listen Audio {0}", rad[readAudioDeviceNo]);
            if (writeAir)
            {
                Console.WriteLine("Play to Audio {0}", wad[writeAudioDeviceNo]);
                Console.WriteLine("  signal delay = {0} ms, tail = {1}", mod.txDelayMs, mod.txTailMs);
            };
            Console.WriteLine("To get device list use: oruxpalsair.exe /listaudio");
            Console.WriteLine();

            tcpt = new Thread(listener);
            tcpr = true;
            tcpt.Start();

            if (readAir)
            {
                airl = new ReadWave.DirectAudioAFSKDemodulator(readAudioDeviceNo, new IncomingAir(this));
                airl.Start();
            };
        }

        public void Stop()
        {
            tcpr = false;

            if (tcpt != null)
                tcpt.Join();
            tcpt = null;

            if (tcpc != null)
                tcpc.Close();
            tcpc = null;

            if (airl != null)
                airl.Stop();
            airl = null;
        }        

        public bool IsRunning
        {
            get { return tcpr; }
        }

        ~OruxPalsAirModem() { Dispose(); }
        public void Dispose() { Stop(); }

        public void listener()
        {
            DateTime lastIncDT = DateTime.UtcNow;
            while (tcpr)
            {                
                // connect
                if ((tcpc == null) || (!IsConnected(tcpc)))
                {
                    tcpc = new TcpClient();
                    try
                    {
                        tcpc.Connect(host, port);
                        string auth = String.Format("user {0} pass {1} vers {2} {3}\r\n", callsign, password, softver, filter);
                        tcpc.Client.Send(System.Text.Encoding.ASCII.GetBytes(auth));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERROR {0}", ex.Message);
                        tcpc.Close();
                        tcpc = null;
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
                        tcpc.GetStream().Write(arr, 0, arr.Length);
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERROR {0}", ex.Message);
                        continue;
                    };
                };

                // read
                try
                {
                    byte[] data = new byte[65536];
                    int ava = 0;
                    if ((ava = tcpc.Available) > 0)
                    {
                        lastIncDT = DateTime.UtcNow;
                        int rd = tcpc.GetStream().Read(data, 0, ava > data.Length ? data.Length : ava);
                        string txt = System.Text.Encoding.GetEncoding(1251).GetString(data, 0, rd);
                        string[] lines = txt.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        if(lines.Length > 0)
                            FromTCP(lines);
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR {0}", ex.Message);
                    tcpc.Close();
                    tcpc = null;
                    Thread.Sleep(5000);
                    continue;
                };

                Thread.Sleep(10);
            };
        }

        internal void FromTCP(string[] lines)
        {
            foreach (string line in lines)
            {
                if(line.IndexOf("# logresp") == 0)
                    Console.WriteLine(line);

                if (line.IndexOf("#") == 0)
                    continue;

                Console.WriteLine("TCP{0}> {1}", writeAudioDeviceNo, line);
            };

            if (writeAir)
            {
                List<ax25.Packet> packets = new List<ax25.Packet>();
                foreach (string line in lines)
                {
                    if (line.IndexOf("#") == 0)
                        continue;

                    string from = line.Substring(0, line.IndexOf(">"));
                    string pckt = line.Substring(line.IndexOf(":") + 1);
                    ax25.Packet packet = new ax25.Packet(
                        "APRS", from, new string[] { "AFSKMD" },
                        ax25.Packet.AX25_CONTROL_APRS, ax25.Packet.AX25_PROTOCOL_NO_LAYER_3,
                        System.Text.Encoding.ASCII.GetBytes(pckt)
                    );
                    packets.Add(packet);
                };

                if (packets.Count > 0)
                {                    
                    double[] _samples;
                    mod.GetSamples(packets.ToArray(), out _samples);
                    ReadWave.WavePlayer wp = new ReadWave.WavePlayer(writeAudioDeviceNo, writeAudioVolume);
                    wp.PlaySamples(44100, _samples, false);
                    //ReadWave.WaveStream.PlaySamples(44100, _samples, false);
                };
            };
        }

        internal void FromAir(string line)
        {
            Console.WriteLine("AIR{0}> {1}", readAudioDeviceNo, line);

            // ignore itself packets
            int id = line.IndexOf(":");
            int im = line.IndexOf("AFSKMD");
            if ((im > 0) && (im < id)) return;

            if (IsConnected(tcpc))
            {
                try
                {
                    string txt2send = line + "\r\n";
                    byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                    tcpc.GetStream().Write(arr, 0, arr.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR {0}", ex.Message);
                };
            };
        }

        private static bool IsConnected(TcpClient Client)
        {
            if (Client == null) return false;
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
    }

    public class IncomingAir : ax25.PacketHandler
    {
        private OruxPalsAirModem modem;

        public IncomingAir(OruxPalsAirModem modem) { this.modem = modem; }
        
        public void handlePacket(sbyte[] bytes)
        {
            string data = ax25.Packet.Format(bytes);
            if(modem != null)
                modem.FromAir(data);
        }
    }

    [Serializable]
    public class OruxPalsAirConfig
    {
        public string server = "127.0.0.1:12015";
        public string callsign = "";
        public string password = "";
        public string filter = "";
        public string readAir = "no";
        public string writeAir = "no";
        public int readAudioDeviceNo = 0;
        public int writeAudioDeviceNo = 0;
        public float writeAudioVolume = -1;
        public int txDelayMs = 500;
        public int txTailMs = 0;

        public static OruxPalsAirConfig LoadFile(string file)
        {
            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(OruxPalsAirConfig));
            System.IO.StreamReader reader = System.IO.File.OpenText(GetCurrentDir() + @"\" + file);
            OruxPalsAirConfig c = (OruxPalsAirConfig)xs.Deserialize(reader);
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
}
