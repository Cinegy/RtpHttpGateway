
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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using CommandLine;
using static System.String;
using System.Runtime;
using Cinegy.TsDecoder.Buffers;

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
    public class Program
    {

        private enum ExitCodes
        {
            UrlAccessDenied = 102,
            UnknownError = 2000
        }

        private const string UrlPrefix = "http://{0}:{1}/";
        private static UdpClient _udpClient;
        private static HttpListener _listener;
        private static bool _packetsStarted;
        private static bool _suppressOutput;
        private static bool _pendingExit;
        private static RingBuffer _ringBuffer;
        private static StreamOptions _options;
        private static readonly List<StreamClient> StreamingClients = new List<StreamClient>();

        private static int Main(string[] args)
        {
            try
            {
                var result = Parser.Default.ParseArguments<StreamOptions>(args);

                return result.MapResult(
                    Run,
                    errs => CheckArgumentErrors());
            }
            catch (Exception ex)
            {
                Environment.ExitCode = (int)ExitCodes.UnknownError;
                PrintToConsole("Unknown error: " + ex.Message);
                throw;
            }
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

        private static int Run(StreamOptions options)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;

            _suppressOutput = options.SuppressOutput;

            Console.WriteLine(
               // ReSharper disable once AssignNullToNotNullAttribute
               $"Cinegy RTP to HTTP gateway tool (Built: {File.GetCreationTime(Assembly.GetExecutingAssembly().Location)})\n");

            _options = options;

            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            StartListeningToNetwork();

            RunWebListener();

            PrintToConsole("\nWeb listener started, waiting for a client...");
            PrintToConsole(
                $"\nTo stream point VLC or WMP to\n{Format(UrlPrefix, _options.AdapterAddress, options.ListenPort) + options.UrlIdentifier}/latest");
            PrintToConsole("Note: Windows MP only supports SPTS!");

            while (!_pendingExit)
            {
                Thread.Sleep(100);
                
                Console.SetCursorPosition(0, 12);
                Console.WriteLine($"Client Connection Count: {StreamingClients?.Count}\t\t");
                Console.WriteLine($"Ring Buffer position: {_ringBuffer.NextAddPosition}\t\t");

                if (StreamingClients == null) continue;

                lock (StreamingClients)
                {
                    foreach (var streamClient in StreamingClients)
                    {
                        if (streamClient.ThreadState == ThreadState.Stopped)
                        {
                            StreamingClients.Remove(streamClient);
                            Console.WriteLine("\t\t\t\t\t\t\t\t\t");
                            break;
                        }

                        Console.WriteLine($"Client {streamClient.ClientAddress} stream position: {streamClient.ClientPosition} (delta: {_ringBuffer.NextAddPosition - streamClient.ClientPosition})\t\t");
                    }
                }

                Console.WriteLine("\t\t\t\t\t\t\t\t\t");
            }

            if (StreamingClients != null)
            {
                lock (StreamingClients)
                {
                    foreach (var streamClient in StreamingClients)
                    {
                        streamClient.Stop();
                    }
                }
            }

            StopWebListener();

            return 0;

        }

        private static void StartListeningToNetwork()
        {
            var listenAddress = IsNullOrEmpty(_options.MulticastAdapterAddress) ? IPAddress.Any : IPAddress.Parse(_options.MulticastAdapterAddress);

            var localEp = new IPEndPoint(listenAddress, _options.MulticastGroup);

            _udpClient = new UdpClient { ExclusiveAddressUse = false };

            _ringBuffer = new RingBuffer(_options.BufferDepth);

            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.ReceiveBufferSize = 1500 * 3000;
            _udpClient.ExclusiveAddressUse = false;
            _udpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(_options.MulticastAddress);
            _udpClient.JoinMulticastGroup(parsedMcastAddr, listenAddress);

            var ts = new ThreadStart(delegate
            {
                ReceivingNetworkWorkerThread(_udpClient, localEp);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();

            PrintToConsole($"Listening for Transport Stream on rtp://@{_options.MulticastAddress}:{_options.MulticastGroup}");
        }

        private static void ReceivingNetworkWorkerThread(UdpClient client, IPEndPoint localEp)
        {
            while (!_pendingExit)
            {
                var data = client.Receive(ref localEp);
                if (data == null) continue;

                if (!_packetsStarted)
                {
                    PrintToConsole("Started receiving multicast packets...");
                    _packetsStarted = true;
                }
                try
                {
                    _ringBuffer.Add(ref data);
                }
                catch (Exception ex)
                {
                    PrintToConsole($@"Unhandled exception within network receiver: {ex.Message}");
                    return;
                }
            }
        }

        private static void RunWebListener()
        {
            _listener = new HttpListener();

            if (!IsNullOrEmpty(_options.AdapterAddress))
            {
            }

            var prefix = Format(UrlPrefix, "+", _options.ListenPort);

            _listener.Prefixes.Add(prefix + _options.UrlIdentifier + "/");

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
                try
                {
                    while (_listener.IsListening)
                    {
                        ThreadPool.QueueUserWorkItem(c =>
                        {
                            var ctx = c as HttpListenerContext;
                            try
                            {
                                if (ctx == null) return;

                                ctx.Response.ContentType = "video/mp2t";
                                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

                                if (ctx.Request.RemoteEndPoint == null) return;

                                var streamClient = new StreamClient(_ringBuffer)
                                {
                                    OutputWriter = new BinaryWriter(ctx.Response.OutputStream),
                                    ClientAddress = ctx.Request.RemoteEndPoint.ToString()

                                };

                                lock (StreamingClients)
                                {
                                    StreamingClients.Add(streamClient);
                                }

                                streamClient.Start();
                            }
                            catch (Exception ex)
                            {
                                PrintToConsole($"Exception: {ex.Message}");
                            }
                        }, _listener.GetContext());
                    }
                }
                catch (Exception ex)
                {
                    PrintToConsole($"Exception: {ex.Message}");
                }
            });

        }

        private static void StopWebListener()
        {
            _listener.Stop();
            _listener.Close();
        }

        private static void PrintToConsole(string message)
        {
            if (_suppressOutput)
                return;

            Console.WriteLine(message);
        }

        //private static bool GetStreamSpecificationsFromUrl(string url, out string multicastAddress, out int multicastGroup)
        //{
        //    multicastAddress = string.Empty;
        //    multicastGroup = 0;

        //    //should be in the form http://address:port/tsstream/address/port
        //    var locTsstream = url.LastIndexOf("/tsstream/", StringComparison.InvariantCulture);

        //    if (locTsstream < 1) return false;

        //    var trimmedString = url.Substring(locTsstream);

        //    var urlParts = trimmedString.Split('/');

        //    if (urlParts.Length < 4) return false;

        //    if (!int.TryParse(urlParts[3], out multicastGroup)) return false;

        //    multicastAddress = urlParts[2];

        //    return true;
        //}

    }

}
