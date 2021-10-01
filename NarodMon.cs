using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

using Newtonsoft.Json;

namespace OruxPals
{
    public class NarodMonAPI
    {
        private static string UUID = "ab4ec47e07ba86074d59f6b7d86fa50d"; // MD5("KMZViewer")
        private static string API_KEY = "VvzN9rSBchAA7";
        private static string URL_API = "http://narodmon.ru/api/";

        //http://narodmon.ru/api/sensorsNearby?lat=55.5&lng=37.5&radius=10&types=1,2&uuid=ab4ec47e07ba86074d59f6b7d86fa50d&api_key=VvzN9rSBchAA7&lang=ru
        private static string URL_NearBy = "sensorsNearby?uuid={0}&api_key=" + API_KEY + "&lang=en";

        // SENSORS: 1 - TEMP, 2 - HUMIDITY, 3 - PRESSURE, 4 - WIND SPEED, 5 - WIND DIR, 9 - RAIN, 20 - UV, 20 DEW POINT
        public Resp_sensorsNearby sensorsNearby(string user, double lat, double lon, int radius, string types, int limit, int timeout)
        {
            Resp_sensorsNearby result = null;
            string url = URL_API + String.Format(URL_NearBy, MD5(user + "+OruxPalsServer")) + String.Format(System.Globalization.CultureInfo.InvariantCulture, "&lat={0}&lng={1}&radius={2}&types={3}&limit={4}", lat, lon, radius, types, limit);
            try
            {                
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                if(timeout > 0)
                    wr.Timeout = timeout;
                System.Net.HttpWebResponse wx = (System.Net.HttpWebResponse)wr.GetResponse();
                System.IO.StreamReader ws = new System.IO.StreamReader(wx.GetResponseStream(), Encoding.UTF8);
                string res = ws.ReadToEnd();
                ws.Close();
                wx.Close();
                result = JsonConvert.DeserializeObject<Resp_sensorsNearby>(res);
            }
            catch (Exception exception)
            {
                FileStream fs = new FileStream(OruxPalsServerConfig.GetCurrentDir()+@"\error.log", FileMode.Create, FileAccess.Write);
                StreamWriter sw = new StreamWriter(fs);
                sw.WriteLine(url);
                sw.Write(exception.ToString());
                sw.Close();
                fs.Close();              
            };
            return result;
        }

        public class Device
        {
            public long id;
            public string name;
            public long my;
            public string owner;
            public string mac;
            public long cmd;
            public string location;
            public double distance;
            public ulong time;
            public double lat;
            public double lng;
            public int liked;
            public ulong uptime;

            public Sensor[] sensors;

            public string Callsign
            {
                get
                {
                    return "NM-" + this.id.ToString();
                }
            }

            public string Text
            {
                get
                {
                    string res = "";
                    foreach (Sensor s in sensors)
                        res += (res.Length > 0 ? ", " : "") + s.value.ToString(System.Globalization.CultureInfo.InvariantCulture) + s.unit;
                    return res;
                }
            }

            public bool HasTemp
            {
                get
                {
                    foreach (Sensor s in sensors)
                        if (s.type == 1) return true;
                    return false;
                }
            }

            public bool HasHumidity
            {
                get
                {
                    foreach (Sensor s in sensors)
                        if (s.type == 2) return true;
                    return false;
                }
            }

            public bool HasPressure
            {
                get
                {
                    foreach (Sensor s in sensors)
                        if (s.type == 3) return true;
                    return false;
                }
            }

            public bool HasRain
            {
                get
                {
                    foreach (Sensor s in sensors)
                        if (s.type == 9) return true;
                    return false;
                }
            }

            public ulong sensup
            {
                get
                {
                    ulong res = 0;
                    foreach (Sensor s in sensors)
                        if (s.time > res) res = s.time;
                    return res;
                }
            }
        }

        public class Sensor
        {
            public long id;
            public string mac;
            public int fav;
            public int pub;
            public int type;
            public string name;
            public double value;
            public string unit;
            public ulong time;
            public ulong changed;
            public int trend;
        }

        public class Resp_sensorsNearby
        {
            public Device[] devices;
        }

        public static string MD5(string input)
        {
            // Use input string to calculate MD5 hash
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                // Convert the byte array to hexadecimal string
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
    }
}
