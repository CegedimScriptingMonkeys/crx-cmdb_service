using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.ServiceProcess;
using System.Xml.Linq;

namespace CMDB_service
{
    class Program
    {
        static void Main(string[] args)
        {
            var service = new CMDBService();

            // Run as console when interactive (easier for debugging) or when --console passed
            if (Environment.UserInteractive || (args != null && args.Length > 0 && args[0].Equals("--console", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Starting CMDB_service in console mode...");
                service.StartAsConsole(args);
            }
            else
            {
                ServiceBase.Run(new ServiceBase[] { service });
            }
        }
    }

    public class CMDBService : ServiceBase
    {
        private CancellationTokenSource _cts;
        private Task _workerTask;
        private readonly object _logLock = new object();

        private string _exePath;
        private string _workingDirectory;
        private string _logFile;
        private int _delayLow;
        private int _delayHigh;

        protected override void OnStart(string[] args)
        {
            // Load configuration
            LoadConfiguration();

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
            Log("Service started");
        }

        protected override void OnStop()
        {
            Log("Service stopping");
            try
            {
                _cts?.Cancel();
                if (_workerTask != null)
                {
                    _workerTask.Wait(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                Log("Error stopping: " + ex);
            }
            Log("Service stopped");
        }

        public void StartAsConsole(string[] args)
        {
            OnStart(args);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                OnStop();
            };
            Console.WriteLine("Press Ctrl+C to exit...");
            // Block until worker finishes
            _workerTask?.Wait();
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_logFile);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    }
                    catch { }

                    File.AppendAllText(_logFile, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);

                    try
                    {
                        const int maxLines = 1000;
                        var lines = File.ReadAllLines(_logFile);
                        if (lines.Length > maxLines)
                        {
                            int skip = lines.Length - maxLines;
                            var keep = new string[maxLines];
                            Array.Copy(lines, skip, keep, 0, maxLines);
                            File.WriteAllLines(_logFile, keep);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                // swallow logging errors
            }
        }

        private void WorkerLoop(CancellationToken token)
        {
            var rand = new Random();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(_exePath))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = _exePath,
                            WorkingDirectory = _workingDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var p = Process.Start(psi))
                        {
                            // Wait in a loop so we can respond to cancellation
                            while (!p.HasExited)
                            {
                                if (token.WaitHandle.WaitOne(1000))
                                {
                                    try { p.Kill(); } catch { }
                                    break;
                                }
                            }
                            p.WaitForExit();
                        }

                        int delay = rand.Next(_delayLow, _delayHigh);
                        Log($"Process exited. Sleeping {delay / 1000} s");
                        token.WaitHandle.WaitOne(delay);
                    }
                    else
                    {
                        Log($"Executable not found: {_exePath}");
                        // Sleep a short time then try again
                        token.WaitHandle.WaitOne(10000);
                    }
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Log("Worker error: " + ex);
                    token.WaitHandle.WaitOne(10000);
                }
            }
        }

        private void LoadConfiguration()
        {
            // Defaults
            string defaultExe = @"C:\Program Files (x86)\CRXServiceAnalytics\CMDB-DataCollector\CRX-CMDB-V2.exe";
            string defaultWork = @"C:\Program Files (x86)\CRXServiceAnalytics\CMDB-DataCollector\";
            string defaultLog = @"C:\ProgramData\CRXServiceAnalytics\CMDB_service.log";
            // Defaults are specified in seconds
            int defaultDelayLow = 360; // 6 minutes
            int defaultDelayHigh = 720; // 12 minutes

            try
            {
                var configPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
                if (File.Exists(configPath))
                {
                    var doc = XDocument.Load(configPath);
                    var addElements = doc.Descendants("add");

                    string exe = null;
                    string work = null;
                    string log = null;
                    int delayLow = defaultDelayLow * 1000;
                    int delayHigh = defaultDelayHigh * 1000;

                    foreach (var el in addElements)
                    {
                        var key = (string)el.Attribute("key");
                        var val = (string)el.Attribute("value");
                        if (string.Equals(key, "ExePath", StringComparison.OrdinalIgnoreCase)) exe = val;
                        else if (string.Equals(key, "WorkingDirectory", StringComparison.OrdinalIgnoreCase)) work = val;
                        else if (string.Equals(key, "LogFile", StringComparison.OrdinalIgnoreCase)) log = val;
                        else if (string.Equals(key, "DelayLow", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsed;
                            if (int.TryParse(val, out parsed)) delayLow = parsed * 1000; // value in seconds -> convert to ms
                        }
                        else if (string.Equals(key, "DelayHigh", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsed;
                            if (int.TryParse(val, out parsed)) delayHigh = parsed * 1000; // value in seconds -> convert to ms
                        }
                    }

                    // Ensure sensible ordering
                    if (delayLow <= 0) delayLow = defaultDelayLow * 1000;
                    if (delayHigh <= 0) delayHigh = defaultDelayHigh * 1000;
                    if (delayLow >= delayHigh)
                    {
                        // swap if misconfigured
                        var t = delayLow; delayLow = delayHigh; delayHigh = t;
                        if (delayLow == delayHigh) { delayLow = defaultDelayLow; delayHigh = defaultDelayHigh; }
                    }

                    _exePath = string.IsNullOrWhiteSpace(exe) ? defaultExe : exe;
                    _workingDirectory = string.IsNullOrWhiteSpace(work) ? defaultWork : work;

                    if (string.IsNullOrWhiteSpace(log))
                    {
                        _logFile = defaultLog;
                    }
                    else
                    {
                        // If relative, place relative to app base
                        if (Path.IsPathRooted(log)) _logFile = log;
                        else _logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, log);
                    }

                    _delayLow = delayLow;
                    _delayHigh = delayHigh;

                    Log("Delaylow: " + delayLow);
                    Log("DelayHigh: " + delayHigh);
                }
                else
                {
                    _exePath = defaultExe;
                    _workingDirectory = defaultWork;
                    _logFile = defaultLog;
                    _delayLow = defaultDelayLow * 1000;
                    _delayHigh = defaultDelayHigh * 1000;
                }
            }
            catch (Exception ex)
            {
                _exePath = defaultExe;
                _workingDirectory = defaultWork;
                _logFile = defaultLog;
                _delayLow = defaultDelayLow * 1000;
                _delayHigh = defaultDelayHigh * 1000;
                try { File.AppendAllText(_logFile, DateTime.Now.ToString("s") + " Error reading config: " + ex + Environment.NewLine); } catch { }
            }
        }
    }
}
