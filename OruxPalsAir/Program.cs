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

namespace OruxPalsAir
{
    class Program
    {
        static void Main(string[] args)
        {
            if (RunConsole(args, OruxPalsAirModem.serviceName))
            {
                OruxPalsAirModem opam = new OruxPalsAirModem();
                opam.Start();
                Console.ReadLine();
                opam.Stop();
            };
        }

        static bool RunConsole(string[] args, string ServiceName)
        {
            if (!Environment.UserInteractive) // As Service
            {
                using (OruxPalsAirModemSvc service = new OruxPalsAirModemSvc()) ServiceBase.Run(service);
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
                    OruxPalsAirModemSvc.Install(false, args, ServiceName);
                    return false;
                case "-u":
                case "/u":
                case "-uninstall":
                case "/uninstall":
                    OruxPalsAirModemSvc.Install(true, args, ServiceName);
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
                case "-listaudio":
                case "/listaudio":
                    {
                        string[] devices = ReadWave.DirectAudioAFSKDemodulator.WaveInDevices();
                        Console.WriteLine("List Audio Devices");
                        Console.WriteLine("Input (readAudioDeviceNo):");
                        if (devices != null)
                            foreach (string device in devices)
                                Console.WriteLine("  {0}",device);
                        devices = ReadWave.DirectAudioAFSKDemodulator.WaveOutDevices();
                        Console.WriteLine("Output (writeAudioDeviceNo):");
                        if (devices != null)
                            foreach (string device in devices)
                                Console.WriteLine("  {0}", device);
                        Console.WriteLine();
                        System.Threading.Thread.Sleep(1000);
                        return false;
                    };
                default:
                    Console.WriteLine("Wrong arguments");
                    System.Threading.Thread.Sleep(1000);
                    return false;
            };
        }
    }

    public class OruxPalsAirModemSvc : ServiceBase
    {
        public OruxPalsAirModem opam = new OruxPalsAirModem();

        public OruxPalsAirModemSvc()
        {
            ServiceName = OruxPalsAirModem.serviceName;
        }

        protected override void OnStart(string[] args)
        {
            opam.Start();
        }

        protected override void OnStop()
        {
            opam.Stop();
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
            this.Description = "OruxPalsAirModem for Windows (" + OruxPalsAirModem.softver + ")";
            this.DisplayName = OruxPalsAirModem.serviceName;
            this.ServiceName = OruxPalsAirModem.serviceName;
            this.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
        }
    }   
}
