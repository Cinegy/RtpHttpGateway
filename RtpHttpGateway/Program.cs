
/*   Copyright 2017 Cinegy GmbH

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using CommandLine;
using CommandLine.Text;
using static System.String;
using System.Runtime;
using System.Text;
using RtpHttpGateway.Logging;
using Newtonsoft.Json;
using System.Diagnostics;

namespace RtpHttpGateway
{
    /// <summary>
    /// This tool was created to allow testing of the transcription of UDP-based RTP multicast (must have RTP headers due to hard-stripping)
    /// to TCP based HTTP unicast of the plain MPEG2-TS, and released as part of the work by Cinegy within the AMWA Labs.
    /// It has real limitations (like support for a single connection) but is stable and allows testing of the concept.
    /// It should be replaced with something more scalable, possibly using a raw output socket rather than the HttpListner, and should support
    /// multiple clients as well as some resource balancing options.
    /// 
    /// Don't forget this EXE will need inbound firewall traffic allowed inbound - since multicast appears as inbound traffic...
    /// 
    /// Originally created by Lewis, so direct complaints his way.
    /// </summary>
    class Program
    {

        private enum ExitCodes
        {
            NullOutputWriter = 100,
            InvalidContext = 101,
            UrlAccessDenied = 102,
            UnknownError = 2000
        }

        private const string UrlPrefix = "http://{0}:{1}/tsstream/";
        private const int RtpHeaderSize = 12;
        private static bool _receiving;
        private static UdpClient _udpClient;
        private static HttpListener _listener;
        private static BinaryWriter _outputWriter;
        private static bool _packetsStarted;
        private static bool _suppressOutput = false;
        private static bool _pendingExit;
        private static readonly object LogfileWriteLock = new object();
        private static StreamWriter _logFileStream;

        private static readonly StringBuilder ConsoleDisplay = new StringBuilder(1024);

        private static StreamOptions _options;

        static int Main(string[] args)
        {
            //if (args == null || args.Length == 0)
            //{
            //    return RunInteractive();
            //}
            
            var result = Parser.Default.ParseArguments<StreamOptions>(args);

            return result.MapResult(
                (StreamOptions opts) => Run(opts),
                errs => CheckArgumentErrors());
        }

        private static int RunInteractive()
        {
            Console.WriteLine("No arguments supplied - would you like to just run with defaults? [Y/N]");
            var response = Console.ReadKey();

            if (response.Key != ConsoleKey.Y)
            {
                Console.WriteLine("\n\n");
                Parser.Default.ParseArguments<StreamOptions>(new string[] { });
                return CheckArgumentErrors();
            }

            Console.Clear();
  
            //TODO: I don't like that i don't get the default value specified as an attribute - but can't be bothered to figure out right now
            var newOpts = new StreamOptions {ListenPort = 8082};
            
            return Run(newOpts);
        }

        private static int CheckArgumentErrors()
        {
            //will print using library the appropriate help - now pause the console for the viewer
            Console.WriteLine("Hit enter to quit");
            Console.ReadLine();
            return -1;
        }

        ~Program()
        {
            Console.CursorVisible = true;
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Console.CursorVisible = true;
            if (_pendingExit) return; //already trying to exit - allow normal behaviour on subsequent presses
            _pendingExit = true;
            e.Cancel = true;
        }

        private static void PrintToConsole(string message, params object[] arguments)
        {
            if (_options.SuppressOutput) return;

            ConsoleDisplay.AppendLine(Format(message, arguments));
        }

        private static void LogMessage(string message)
        {
            var logRecord = new LogRecord()
            {
                EventCategory = "Info",
                EventKey = "GenericEvent",
                EventTags = _options.DescriptorTags,
                EventMessage = message
            };
            LogMessage(logRecord);
        }

        private static void LogMessage(LogRecord logRecord)
        {
            var formattedMsg = JsonConvert.SerializeObject(logRecord);
            ThreadPool.QueueUserWorkItem(WriteToFile, formattedMsg);
        }

        private static void WriteToFile(object line)
        {
            lock (LogfileWriteLock)
            {
                try
                {
                    if (_logFileStream == null || _logFileStream.BaseStream.CanWrite != true)
                    {
                        if (IsNullOrWhiteSpace(_options.LogFile)) return;

                        var fs = new FileStream(_options.LogFile, FileMode.Append, FileAccess.Write);

                        _logFileStream = new StreamWriter(fs) { AutoFlush = true };
                    }
                    _logFileStream.WriteLine(line);
                }
                catch (Exception)
                {
                    Debug.WriteLine("Concurrency error writing to log file...");
                    _logFileStream?.Close();
                    _logFileStream?.Dispose();
                }
            }
        }

        private static int Run(StreamOptions options)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;
            
            _suppressOutput = options.SuppressOutput;
            
            Console.WriteLine(
               // ReSharper disable once AssignNullToNotNullAttribute
               $"Cinegy RTP to HTTP gateway tool (Built: {File.GetCreationTime(Assembly.GetExecutingAssembly().Location)})\n");
            
            _options = options;

            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            
            RunWebListener();

            PrintToConsole("--Limited to one client in this version--");
            PrintToConsole("\nWeb listener started, waiting for a client...");
            PrintToConsole(
                $"\nTo stream point VLC or WMP to\n{Format(UrlPrefix, _options.AdapterAddress, options.ListenPort)}multicast-ip/portnumber");
            PrintToConsole("Note: Windows MP only supports SPTS!");

            while (!_pendingExit)
                Thread.Sleep(50);

            return 0;
            
        }
        
        private static void StartListeningToNetwork(string multicastAddress, int multicastGroup)
        {
            var listenAddress = IsNullOrEmpty(_options.MulticastAdapterAddress) ? IPAddress.Any : IPAddress.Parse(_options.MulticastAdapterAddress);

            var localEp = new IPEndPoint(listenAddress, multicastGroup);
            
            _udpClient = new UdpClient { ExclusiveAddressUse = false };

            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.ReceiveBufferSize = 1500 * 3000;
            _udpClient.ExclusiveAddressUse = false;
            _udpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            _udpClient.JoinMulticastGroup(parsedMcastAddr, listenAddress);

            var ts = new ThreadStart(delegate
            {
                ReceivingNetworkWorkerThread(_udpClient, localEp);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();

            //TODO: Consider to use same thread model as TSAnalyser - and run a thread to empty NIC buffer plus thread to process queue (may be overkill here)

            //var queueThread = new Thread(ProcessQueueWorkerThread) { Priority = ThreadPriority.AboveNormal };

            //queueThread.Start();
        }

        private static void ReceivingNetworkWorkerThread(UdpClient client, IPEndPoint localEp)
        {
            while (!_pendingExit)
            {
                var data = client.Receive(ref localEp);
                if (data != null)
                {
                    if (!_packetsStarted)
                    {
                        PrintToConsole("Started receiving packets...");
                        _packetsStarted = true;
                    }
                    try
                    {
                        if (_outputWriter != null)
                        {
                            _outputWriter.Write(data, RtpHeaderSize, data.Length - RtpHeaderSize);
                        }
                        else
                        {
                            PrintToConsole("Writing to null output writer...");
                            //Environment.Exit((int)ExitCodes.NullOutputWriter);
                            client.Close();
                            RunWebListener();
                            return;
                        }

                    }
                    catch (HttpListenerException listenerException)
                    {
                        PrintToConsole(Format(@"Writing to client stopped - probably client disconnected...: {0}", listenerException.Message));
                        _outputWriter = null;
                        _receiving = false;
                        //Environment.Exit((int)ExitCodes.InvalidContext);
                        client.Close();
                        RunWebListener();
                        return;
                    }
                    catch (Exception ex)
                    {
                        PrintToConsole(Format(@"Unhandled exception within network receiver: {0}", ex.Message));
                        return;
                    }
                }
            }
        }

        public static void RunWebListener()
        {
            try
            {
                _listener?.Abort();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Abort failed: {ex.Message}");
            }

            _listener = new HttpListener();
            var prefixAddr = "127.0.0.1";

            if (!IsNullOrEmpty(_options.AdapterAddress))
            {
                prefixAddr = _options.AdapterAddress;
            }

            var prefix = Format(UrlPrefix, "+", _options.ListenPort);

            _listener.Prefixes.Add(prefix);

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                PrintToConsole("Exception creating web listener: " + ex.Message);
                PrintToConsole("Probably the URL is not reserved - either reserve, or run as admin!");
                PrintToConsole("For reference, type from elevated command prompt:");
                PrintToConsole($@"netsh http add urlacl url={prefix} user=BUILTIN\users");
                PrintToConsole("");
                PrintToConsole("Hit any key to exit");
                Console.ReadLine();
                Environment.Exit((int)ExitCodes.UrlAccessDenied);
            }

            ThreadPool.QueueUserWorkItem(o =>
            {
                PrintToConsole(Format(("Webserver running...")));
                try
                {
                    while (_listener.IsListening)
                    {
                        ThreadPool.QueueUserWorkItem(c =>
                        {
                            var ctx = c as HttpListenerContext;
                            try
                            {
                                if (ctx == null)
                                {
                                    throw (new InvalidOperationException("Null context - not expected!"));
                                }
                                //ctx.Response.SendChunked = false;

                                ctx.Response.ContentType = "video/mp2t";
                                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

                                if (ctx.Request.RemoteEndPoint != null)
                                    PrintToConsole(Format(@"{0} - Starting output to client: {1}", DateTime.Now.TimeOfDay, ctx.Request.RemoteEndPoint.Address));

                                _outputWriter = new BinaryWriter(ctx.Response.OutputStream);

                                SetupMulticastReceiverForSession(ctx.Request.Url.ToString());

                            }
                            catch (Exception ex)
                            {
                                PrintToConsole(Format("Exception: {0}", ex.Message));
                            }
                        }, _listener.GetContext());
                    }
                }
                catch (Exception ex)
                {
                    PrintToConsole(Format("Exception: {0}", ex.Message));
                }
            });
            
        }

        public static void StopWebListener()
        {
            _listener.Stop();
            _listener.Close();
        }

        private static void SetupMulticastReceiverForSession(string url)
        {
            string multicastAddress;
            var multicastGroup = 1234;

            if (GetStreamSpecificationsFromUrl(url, out multicastAddress, out multicastGroup))
            {
                StartListeningToNetwork(multicastAddress, multicastGroup);
            }
            else
            {
                PrintToConsole("Unsupported URL request format: " + url);
            }

        }

        private static void PrintToConsole(string message)
        {
            if (_suppressOutput)
                return;

            Console.WriteLine(message);
        }

        private static bool GetStreamSpecificationsFromUrl(string url, out string multicastAddress, out int multicastGroup)
        {
            multicastAddress = string.Empty;
            multicastGroup = 0;

            //should be in the form http://address:port/tsstream/address/port
            var locTsstream = url.LastIndexOf("/tsstream/", StringComparison.InvariantCulture);

            if (locTsstream < 1) return false;

            var trimmedString = url.Substring(locTsstream);

            var urlParts = trimmedString.Split('/');

            if (urlParts.Length < 4) return false;

            if (!int.TryParse(urlParts[3], out multicastGroup)) return false;

            multicastAddress = urlParts[2];

            return true;
        }

    }
    
}
