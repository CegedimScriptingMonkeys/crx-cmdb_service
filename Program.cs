using System;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace CMDB_service
{
    class Program
    {
        static void Main(string[] args)
        {
            Random Random = new Random();
            int randomNumber = Random.Next(1200000, 2400000);

            while (true)
            {

                string strEXELocation = @"C:\Program Files (x86)\CegedimRXCMDB\CMDB\CMDB.exe";
                string strWorkingDirectory = @"C:\Program Files (x86)\CegedimRXCMDB\CMDB\";

                if (File.Exists(strEXELocation))
                {
                    ProcessStartInfo _processStartInfo = new ProcessStartInfo();
                    _processStartInfo.WorkingDirectory = @"C:\Program Files (x86)\CegedimRXCMDB\CMDB\";
                    _processStartInfo.FileName = @"C:\Program Files (x86)\CegedimRXCMDB\CMDB\CMDB.exe";

                    using (var p = Process.Start(_processStartInfo))
                    {
                        p.WaitForExit();
                        p.Close();
                    }
                    //Thread.Sleep(10000);
                    Thread.Sleep(randomNumber);
                }

                else
                {
                    Console.WriteLine(strEXELocation);
                    Thread.Sleep(10000);
                    Environment.Exit(1);
                }
            }
        }
    }
}
