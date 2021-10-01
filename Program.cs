using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.ServiceProcess;
using System.Configuration.Install;
using System.ComponentModel;
using System.Xml;
using System.Xml.Serialization;
using System.Runtime.InteropServices;
using System.Reflection;

namespace OruxPals
{
    class Program
    {
        static void Main(string[] args)
        {            
            if (RunConsole(args, OruxPalsServer.serviceName))
            {
                OruxPals.OruxPalsServer ops = new OruxPals.OruxPalsServer();
                ops.Start();
                Console.ReadLine();
                ops.Stop();
                return;
            };
        }

        static bool RunConsole(string[] args, string ServiceName)
        {
            if (!Environment.UserInteractive) // As Service
            {
                using (OruxPalsServerSvc service = new OruxPalsServerSvc()) ServiceBase.Run(service);
                return false;
            };

            if ((args == null) || (args.Length == 0))
                return true;

            switch (args[0])
            {
                case "-i":
                case "/i":
                case "-install":
                case "/install":
                    OruxPalsServerSvc.Install(false, args, ServiceName);
                    return false;
                case "-u":
                case "/u":
                case "-uninstall":
                case "/uninstall":
                    OruxPalsServerSvc.Install(true, args, ServiceName);
                    return false;
                case "-start":
                case "/start":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-stop":
                case "/stop":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-restart":
                case "/restart":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-status":
                case "/status":
                    {
                        ServiceController service = new ServiceController(ServiceName);
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-kml2sql":
                case "/kml2sql":
                    {
                        KML2SQL(args.Length > 1 ? args[1] : null);
                        return false;
                    };
                default:
                    Console.WriteLine(args[0]+":"+Buddie.Hash(args[0].ToUpper()));
                    System.Threading.Thread.Sleep(1000);
                    return false;
            };
        }

        private static void KML2SQL(string filename)
        {
            Console.WriteLine("Import kml file to SQLite \"StaticObjects.db\"");
            if ((filename != null) && (filename != String.Empty) && File.Exists(filename))
                KML2SQLDO(filename);
            else
            {
                string[] files = Directory.GetFiles(PreloadedObjects.GetObjectsDir(), "*.kml", SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    Console.WriteLine("Select file to import:");
                    for (int i = 0; i < files.Length; i++)
                        Console.WriteLine(" {0}:  \"{1}\"", i, Path.GetFileName(files[i]));
                    Console.Write(">>>");
                    string rdl = Console.ReadLine();
                    int selected = -1;
                    if ((int.TryParse(rdl, out selected)) && (selected >= 0) && (selected < files.Length))
                        KML2SQLDO(files[selected]);
                    else
                        Console.WriteLine("Incorrect choice \"" + rdl + "\"");
                }
                else
                    Console.WriteLine("Folder `OBJECTS` is empty");
            };
        }

        private static void KML2SQLDO(string filename)
        {
            Console.WriteLine("Importing file \"{0}\"...", Path.GetFileName(filename));
            string filePrefix = Transliteration.Front(Path.GetFileName(filename).ToUpper().Substring(0, 2));
            System.Data.SQLite.SQLiteConnection sqlc = new System.Data.SQLite.SQLiteConnection(String.Format("Data Source={0};Version=3;", OruxPalsServerConfig.GetCurrentDir() + @"\StaticObjects.db"));
            sqlc.Open();
            {
                System.Data.SQLite.SQLiteCommand sc = new System.Data.SQLite.SQLiteCommand("SELECT MAX(IMPORTNO) FROM OBJECTS", sqlc);
                int import = 1;
                try { string a = sc.ExecuteScalar().ToString(); int.TryParse(a, out import); import++; }
                catch { };
                string importTemplate = "insert into OBJECTS (LAT,LON,SYMBOL,[NAME],COMMENT,IMPORTNO,SOURCE) VALUES ({0},{1},'{2}','{3}','{4}',{5},'{6}')";
                XmlDocument xd = new XmlDocument();
                using (XmlTextReader tr = new XmlTextReader(filename))
                {
                    tr.Namespaces = false;
                    xd.Load(tr);
                };
                string defSymbol = "\\C";
                XmlNode NodeSymbol = xd.SelectSingleNode("/kml/symbol");
                if (NodeSymbol != null) defSymbol = NodeSymbol.ChildNodes[0].Value;
                string defFormat = "R{0:000}-{1}"; // {0} - id; {1} - file prefix; {2} - Placemark Name without spaces
                XmlNode NodeFormat = xd.SelectSingleNode("/kml/format");
                if (NodeFormat != null) defFormat = NodeFormat.ChildNodes[0].Value;
                XmlNodeList nl = xd.GetElementsByTagName("Placemark");
                List<PreloadedObject> fromKML = new List<PreloadedObject>();
                if (nl.Count > 0)
                    for (int i = 0; i < nl.Count; i++)
                    {
                        string pName = System.Security.SecurityElement.Escape(Transliteration.Front(nl[i].SelectSingleNode("name").ChildNodes[0].Value));
                        pName = Regex.Replace(pName, "[\r\n\\(\\)\\[\\]\\{\\}\\^\\$\\&\']+", "");
                        string pName2 = Regex.Replace(pName.ToUpper(), "[^A-Z0-9\\-]+", "");
                        string symbol = defSymbol;
                        if (nl[i].SelectSingleNode("symbol") != null)
                            symbol = nl[i].SelectSingleNode("symbol").ChildNodes[0].Value.Trim();
                        if (nl[i].SelectSingleNode("Point/coordinates") != null)
                        {
                            string pPos = nl[i].SelectSingleNode("Point/coordinates").ChildNodes[0].Value.Trim();
                            string[] xyz = pPos.Split(new char[] { ',' }, 3);
                            sc.CommandText = String.Format(importTemplate, new object[] { 
                                        xyz[1], xyz[0], symbol.Replace("'",@"''"),  
                                        String.Format(defFormat, i + 1, filePrefix, pName2), 
                                        pName, import, Path.GetFileName(filename)});
                            sc.ExecuteScalar();
                        };
                        Console.Write("Import Placemark {0}/{1}", i + 1, nl.Count);
                        Console.SetCursorPosition(0, Console.CursorTop);
                    };
                Console.WriteLine();
                Console.WriteLine("Done");
            };
            sqlc.Close();
        }
    }

    public class OruxPalsServerSvc : ServiceBase
    {
        public OruxPalsServer ops = new OruxPalsServer();

        public OruxPalsServerSvc()
        {
            ServiceName = OruxPalsServer.serviceName;
        }

        protected override void OnStart(string[] args)
        {
            ops.Start();
        }

        protected override void OnStop()
        {
            ops.Stop();
        }

        public static void Install(bool undo, string[] args, string ServiceName)
        {
            try
            {
                Console.WriteLine(undo ? "Uninstalling service {0}..." : "Installing service {0}...", ServiceName);
                using (AssemblyInstaller inst = new AssemblyInstaller(typeof(Program).Assembly, args))
                {
                    IDictionary state = new Hashtable();
                    inst.UseNewContext = true;
                    try
                    {
                        if (undo)
                            inst.Uninstall(state);
                        else
                        {
                            inst.Install(state);
                            inst.Commit(state);
                        }
                    }
                    catch
                    {
                        try
                        {
                            inst.Rollback(state);
                        }
                        catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            };
        }
    }

    [RunInstaller(true)]
    public sealed class MyServiceInstallerProcess : ServiceProcessInstaller
    {
        public MyServiceInstallerProcess()
        {
            this.Account = ServiceAccount.NetworkService;
            this.Username = null;
            this.Password = null;
        }
    }

    [RunInstaller(true)]
    public sealed class MyServiceInstaller : ServiceInstaller
    {
        public MyServiceInstaller()
        {
            this.Description = "OruxPalsServer for Windows (" + OruxPalsServer.softver + ")";
            this.DisplayName = "OruxPalsServer";
            this.ServiceName = OruxPalsServer.serviceName;
            this.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
        }
    }   
}
