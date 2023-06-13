using OruxPals;
using System.Collections;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml;

internal class Program
{
    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        OruxPals.OruxPalsServer ops = new OruxPals.OruxPalsServer();
        ops.Start();
        while (true)
        {
            Console.WriteLine("enter: /exit to exit");
            Console.WriteLine("enter: /restart to restart");
            string? line = Console.ReadLine();
            if (line == "/exit") break;
            if (line == "/restart") { ops.Stop(); ops = new OruxPals.OruxPalsServer(); ops.Start();  };
        };
        ops.Stop();
        return;
    }
}