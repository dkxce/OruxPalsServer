/*
 * milokz@gmail.com 
 * FlightRadar24 Grabber
 */

using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Web;
using System.Xml;

namespace OruxPals
{
    /*
    class Program
    {
        static void Main(string[] args)
        {
            FlightRadarGrabber frg = new FlightRadarGrabber();
            frg.OnData += new FlightRadarGrabber.DataEvent(frg_OnData);
            frg.Start(55.45f, 37.39f, 20.00f, 7);

            Console.ReadLine();
            frg.Stop();
        }

        static void frg_OnData(AirCraft[] AirShips, DateTime updated)
        {
            Console.Clear();
            Console.WriteLine(DateTime.Now.ToString());
            AirShips = AirCraft.FilterShips(AirShips, "VKO");
            foreach (AirCraft ship in AirShips)
                Console.WriteLine(ship.ToString());
        }
    }
    */

    public class FlightRadarGrabber
    {
        private FRGWCFG configuration;
        private bool loop = false;
        private Thread mthr = null;
        private Thread ethr = null;        
        private AirCraft[] AirShipsDATA = null;
        private Mutex SyncMutex = new Mutex();
        private ulong SyncIncrement = 0;
        private string _lastURL = "";
        private string _lastErr = "";
        private ulong _counter = 0;
        private ulong _errors = 0;
        private DateTime _updated = DateTime.MinValue;


        public delegate void DataEvent(AirCraft[] AirShips, DateTime update);
        public event DataEvent OnAirData;
       
        public FlightRadarGrabber()
        {
            configuration = FRGWCFG.LoadFile("FlightRadarGW.xml");
        }

        /// <summary>
        ///     Grab interval in seconds
        /// </summary>
        public byte ScanInterval
        {
            get { return configuration.GrabInterval; }
            set 
            {
                if (value < 1) Stop();
                if (value < 5)
                    configuration.GrabInterval = 5;
                else
                    configuration.GrabInterval = value;

            }
        }

        /// <summary>
        ///     Grab start latitude in degrees
        /// </summary>
        public float ScanFromLat
        {
            get { return configuration.GrabZoneCenterLat; }
            set { configuration.GrabZoneCenterLat = value; }
        }

        /// <summary>
        ///     Grab start longitude in degrees
        /// </summary>
        public float ScanFromLon
        {
            get { return configuration.GrabZoneCenterLon; }
            set { configuration.GrabZoneCenterLon = value; }
        }

        /// <summary>
        ///     Grab zone with width in degrees
        /// </summary>
        public float ScanWidth
        {
            get { return configuration.GrabZoneWidth; }
            set { configuration.GrabZoneWidth = value; }
        }

        /// <summary>
        ///     Grab zone with height in degrees
        /// </summary>
        public float ScanHeight
        {
            get { return configuration.GrabZoneHeight; }
            set { configuration.GrabZoneHeight = value; }
        }

        /// <summary>
        ///     Grab Zone Direction
        ///     F (Full) or N,NE,E,SE,S,SW,W,NW
        /// </summary>
        public string ScanZone
        {
            get { return configuration.GrabZone; }
            set { configuration.GrabZone = value; }
        }

        /// <summary>
        ///     Gran URL with {BOUNDS} to be replaced
        /// </summary>
        public string ScanURL
        {
            get { return configuration.GrabURL; }
            set { configuration.GrabURL = value; }
        }

        /// <summary>
        ///     Last Grabbed URL
        /// </summary>
        public string LastURL
        {
            get { return _lastURL; }
        }

        /// <summary>
        ///     Last Error
        /// </summary>
        public string LastError
        {
            get { return _lastErr; }
        }

        /// <summary>
        ///     Total Grabs
        /// </summary>
        public ulong TotalScanCount
        {
            get { return _counter; }
        }

        /// <summary>
        ///     Total Errors
        /// </summary>
        public ulong TotalErrorCount
        {
            get { return _errors; }
        }

        /// <summary>
        ///     Grabbing is active
        /// </summary>
        public bool IsActive
        {
            get { return loop; }
        }

        /// <summary>
        ///     Last Grab Time
        /// </summary>
        public DateTime LastUpdate
        {
            get { return _updated; }
        }

        public string Parameters
        {
            get
            {
                return "FR24/" + configuration.GrabZone + configuration.GrabZoneWidth.ToString() + "-" + configuration.GrabZoneHeight.ToString() + "/" + configuration.GrabInterval.ToString() + " (" +
                    configuration.GrabZoneCenterLat.ToString("00.000000", System.Globalization.CultureInfo.InvariantCulture) + " " + configuration.GrabZoneCenterLon.ToString("000.000000", System.Globalization.CultureInfo.InvariantCulture)+")";
            }
        }

        public void Start(float Lat, float Lon, float RadiusInDegrees, byte Interval)
        {
            configuration.GrabZoneCenterLat = Lat;
            configuration.GrabZoneCenterLon = Lon;
            configuration.GrabZoneWidth = RadiusInDegrees;
            this.ScanInterval = Interval;
            Start();
        }

        public void Start(float Lat, float Lon, float ZoneWidthInDegrees)
        {
            configuration.GrabZoneCenterLat = Lat;
            configuration.GrabZoneCenterLon = Lon;
            configuration.GrabZoneWidth = ZoneWidthInDegrees;
            Start();
        }

        public void Start(float RadiusInDegrees)
        {
            configuration.GrabZoneWidth = RadiusInDegrees;
            Start();
        }

        public void Start(float Lat, float Lon)
        {
            configuration.GrabZoneCenterLat = Lat;
            configuration.GrabZoneCenterLon = Lon;
            Start();
        }

        public void Start(byte Interval)
        {
            this.ScanInterval = Interval;
            Start();
        }

        public void Start()
        {
            if (loop) Stop();
            if (configuration.GrabInterval == 0) return;
            
            mthr = new Thread(MainThread);
            ethr = new Thread(EventThread);
            loop = true;
            ethr.Start();
            mthr.Start();
        }

        public void Stop()
        {
            if (!loop) return;

            loop = false;            
            Thread.Sleep(200);

            mthr.Join(2000);            
            ethr.Join(2000);
            mthr.Abort();
            ethr.Abort();

            mthr = null;
            AirShipsDATA = null;
        }

        private void Grabb()
        {
            float MaxLat = configuration.GrabZoneCenterLat + configuration.GrabZoneHeight / 2.0f;
            float MinLat = configuration.GrabZoneCenterLat - configuration.GrabZoneHeight / 2.0f;
            float MinLon = configuration.GrabZoneCenterLon - configuration.GrabZoneWidth / 2.0f;
            float MaxLon = configuration.GrabZoneCenterLon + configuration.GrabZoneWidth / 2.0f;

            if (configuration.GrabZone.Contains("N"))
            {
                MaxLat = configuration.GrabZoneCenterLat + configuration.GrabZoneHeight;
                MinLat = configuration.GrabZoneCenterLat - 0.5f;                
            };
            if (configuration.GrabZone.Contains("E"))
            {
                MinLon = configuration.GrabZoneCenterLon - 0.5f;
                MaxLon = configuration.GrabZoneCenterLon + configuration.GrabZoneWidth;
            };
            if (configuration.GrabZone.Contains("S"))
            {
                MaxLat = configuration.GrabZoneCenterLat + 0.5f;
                MinLat = configuration.GrabZoneCenterLat - configuration.GrabZoneHeight;
            };
            if (configuration.GrabZone.Contains("W"))
            {
                MinLon = configuration.GrabZoneCenterLon - configuration.GrabZoneWidth;
                MaxLon = configuration.GrabZoneCenterLon + 0.5f;                
            };

            string url = configuration.GrabURL.Replace("{BOUNDS}",
                MaxLat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                MinLat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                MinLon.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                MaxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "&dt=" +
                DateTime.Now.ToString("yyyyMMddHHmmss");
            _lastURL = url;
            HttpWebRequest wreq = (HttpWebRequest)HttpWebRequest.Create(url);
            wreq.UserAgent = configuration.UserAgent;
            wreq.Referer = configuration.Referer;

            string DATA = "";

            try
            {
                HttpWebResponse wres = (HttpWebResponse)wreq.GetResponse();
                StreamReader sr = new StreamReader(wres.GetResponseStream());
                DATA = sr.ReadToEnd();
                sr.Close();
                wres.Close();
                
                _counter++;
                _updated = DateTime.Now;
            }
            catch  (Exception ex)
            {
                _errors++;
                _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
            };
            if (String.IsNullOrEmpty(DATA)) return;

            AirCraft[] ships = new AirCraft[0];

            try
            {
                ships = AirCraft.ParseData(DATA);
            }
            catch (Exception ex)
            {
                _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
            };

            SyncMutex.WaitOne();
            AirShipsDATA = ships;
            SyncMutex.ReleaseMutex();
            SyncIncrement++;
        }

        private void MainThread()
        {
            try
            {
                while (loop)
                {
                    try { Grabb(); }
                    catch (Exception ex)
                    {
                        _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
                    };

                    for (int i = 0; i < configuration.GrabInterval * 2; i++)
                    {
                        if (!loop) break;
                        Thread.Sleep(500);
                    };
                };
            }
            catch (Exception ex)
            {
                _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
            };
        }

        private void EventThread()
        {
            ulong currInc = SyncIncrement;
            try
            {
                while (loop)
                {
                    if (currInc != SyncIncrement)
                    {
                        AirCraft[] ships = new AirCraft[0];
                        
                        SyncMutex.WaitOne();
                        try { if ((AirShipsDATA != null) && (AirShipsDATA.Length > 0)) ships = (AirCraft[])AirShipsDATA.Clone(); }
                        catch (Exception ex)
                        {
                            _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
                        };
                        SyncMutex.ReleaseMutex();
                        
                        currInc = SyncIncrement;
                        OnAirData(ships, _updated);
                    };
                    Thread.Sleep(500);
                };
            }
            catch (Exception ex)
            {
                _lastErr = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy") + " " + ex.Message.ToString();
            };
        }
    }

    public class AirCraft
    {
        public string ID1;
        public string ID2;
        public float Lat; // in degrees
        public float Lon; // in degrees
        public short Hdg; // in degrees
        public ushort Alt; // in feets
        public ushort Spd; // in knots
        public string RegNo; // 
        public string AirCraftType; // Type of Aircraft
        public ulong TimeStamp; // Unix
        public string GoingFrom;
        public string GoingTo;
        public string FlightCS;
        public string FlightNo;
        public string AirLine;

        public bool StringIN(string toSearch)
        {
            return StringIN(toSearch, false);
        }

        public bool StringIN(string toSearch, bool caseIns)
        {
            if (caseIns)
            {
                return Hint.Contains(toSearch);
            }
            else
            {
                return Hint.ToUpper().Contains(toSearch.ToUpper());
            };
        }

        public string Flight
        {
            get
            {
                return (String.IsNullOrEmpty(FlightCS) ? "?" : FlightCS ) + @"/" + FlightNo;
            }
        }

        public string CallSign
        {
            get
            {
                if (!String.IsNullOrEmpty(FlightNo))
                    return FlightNo;
                else if (!String.IsNullOrEmpty(FlightCS))
                    return FlightCS;
                else if (!String.IsNullOrEmpty(RegNo))
                    return RegNo;
                else if (!String.IsNullOrEmpty(ID1))
                    return "U" + ID1;
                else
                    return "UNKNOWN";
            }
        }

        public string Hint
        {
            get
            {
                return Lat.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Lon.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                    RegNo + "(" + AirCraftType + ") FL" + Alt + " " + Spd + "k " + Time.ToString() + " \\ " + GoingFrom + " 2 " + GoingTo + " " + Flight + " by " + AirLine;
            }
        }

        public string ShortHint
        {
            get
            {
                return RegNo + "(" + AirCraftType + ") FL" + (Alt / 100).ToString("000", System.Globalization.CultureInfo.InvariantCulture) + " " + Spd + "k " + Time.ToString("HH:mm:ss dd.MM") + " " + GoingFrom + " 2 " + GoingTo + " " + Flight + " by " + AirLine;
            }
        }

        public override string ToString()
        {
            return Hint;
        }

        public DateTime Time
        {
            get
            {
                return UnixTimeStampToDateTime(TimeStamp);
            }
        }

        private static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public static AirCraft[] ParseData(string DATA)
        {
            List<AirCraft> res = new List<AirCraft>();
            int count = 0;

            int si = 0;
            int pf = 0;
            int pt = 0;

            string data = DATA;
            pf = data.IndexOf("\"full_count\":", si);
            if (pf < 0) return res.ToArray();
            pf += 13;
            pt = data.IndexOf(",", pf);

            int.TryParse(data.Substring(pf, pt - pf), out count);
            if (count == 0) return res.ToArray();

            si = pt;
            pf = data.IndexOf("\"version\":", si);
            pt = data.IndexOf(",", pf);
            si = pt + 1;
            data = data.Remove(0, si);

            while (data.Length > 0)
            {
                count--;

                si = 0;
                pf = data.IndexOf(":[", si);                
                pt = data.IndexOf("]", si);
                if (pf < 0) break; // "STATS";

                string current = data.Substring(0, pt + 1);
                data = data.Remove(0, pt + 1);

                if (data.Length > 10)
                {
                    pt = data.IndexOf("\"");
                    data = data.Remove(0, pt);
                }
                else data = "";

                AirCraft aa = new AirCraft();
                pf = current.IndexOf(":");
                aa.ID1 = current.Substring(0, pf).Replace("\"", "");
                current = current.Remove(0, pf + 2);
                current = current.Remove(current.Length - 1);
                string[] arr = current.Split(new string[] { "," }, StringSplitOptions.None);
                aa.ID2 = arr[0].Replace("\"", "");
                float.TryParse(arr[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out aa.Lat);
                float.TryParse(arr[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out aa.Lon);
                short.TryParse(arr[3], out aa.Hdg);
                ushort.TryParse(arr[4], out aa.Alt);
                ushort.TryParse(arr[5], out aa.Spd);
                aa.AirCraftType = arr[8].Replace("\"", "");
                aa.RegNo = arr[9].Replace("\"", "");
                ulong.TryParse(arr[10], out aa.TimeStamp);
                aa.GoingFrom = arr[11].Replace("\"", "");
                aa.GoingTo = arr[12].Replace("\"", "");
                aa.FlightCS = arr[13].Replace("\"", "");
                aa.FlightNo = arr[16].Replace("\"", "");
                aa.AirLine = arr[18].Replace("\"", "");
                res.Add(aa);
            };

            return res.ToArray();
        }

        public static AirCraft[] FilterShips(AirCraft[] ships, string toSearch)
        {
            return FilterShips(ships, toSearch, false);
        }

        public static AirCraft[] FilterShips(AirCraft[] ships, string toSearch, bool caseIns)
        {
            if (ships == null) return null;
            if (ships.Length == 0) return new AirCraft[0];

            List<AirCraft> res = new List<AirCraft>();
            foreach (AirCraft plane in ships)
                if (plane.StringIN(toSearch, caseIns))
                    res.Add(plane);
            return res.ToArray();
        }
    }

    public class FRGWCFG
    {
        //public string GrabURL = "https://data-live.flightradar24.com/zones/fcgi/feed.js?bounds=75.45,17.39,35.45,57.39&faa=1&satellite=0&mlat=1&flarm=1&adsb=1&gnd=1&air=1&vehicles=0&estimated=1&maxage=14400&gliders=0&stats=0";
        
        public byte   GrabInterval = 15;
        public string GrabURL = "https://data-live.flightradar24.com/zones/fcgi/feed.js?bounds={BOUNDS}&faa=1&satellite=0&mlat=1&flarm=1&adsb=1&gnd=1&air=1&vehicles=0&estimated=1&maxage=14400&gliders=0&stats=0";
        public float  GrabZoneWidth = 15.00f;
        public float  GrabZoneHeight = 15.00f;        
        public float  GrabZoneCenterLat = 55.45f;
        public float  GrabZoneCenterLon = 37.39f;
        public string GrabZone = "F";

        public string UserAgent = "Mozilla/5.0 (Windows NT 5.1; rv:52.0) Gecko/20100101 Firefox/52.0";
        public string Referer = "https://www.flightradar24.com/";

        public static FRGWCFG LoadFile(string file)
        {
            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(FRGWCFG));
            System.IO.StreamReader reader = System.IO.File.OpenText(FRGWCFG.GetCurrentDir() + @"\" + file);
            FRGWCFG c = (FRGWCFG)xs.Deserialize(reader);
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
