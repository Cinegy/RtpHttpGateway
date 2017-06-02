using CommandLine;

namespace RtpHttpGateway
{
    internal class Options
    {
        [Option('q', "quiet", Required = false, Default = false,
        HelpText = "Don't print anything to the console")]
        public bool SuppressOutput { get; set; }

        [Option('l', "logfile", Required = false,
        HelpText = "Optional file to record events to.")]
        public string LogFile { get; set; }
        
        [Option('d', "descriptortags", Required = false, Default = "",
        HelpText = "Comma separated tag values added to all log entries for instance and machine identification")]
        public string DescriptorTags { get; set; }

        [Option('e', "timeserieslogging", Required = false,
        HelpText = "Record time slice metric data to.")]
        public bool TimeSeriesLogging { get; set; }

        [Option('v', "verboselogging", Required = false,
        HelpText = "Creates event logs for all discontinuities and skips.")]
        public bool VerboseLogging { get; set; }

        [Option('p', "port", Required = false, Default = 8082,
        HelpText = "Port Number to listen for web serving requests (8082 if not set).")]
        public int ListenPort { get; set; }
    }

    // Define a class to receive parsed values
    [Verb("stream", HelpText = "Stream from the network.")]
    internal class StreamOptions : Options
    {
        [Option('a', "adapter", Required = false,
        HelpText = "IP address of the adapter to serve HTTP requests from (if not set, tries first binding adapter).")]
        public string AdapterAddress { get; set; }

        [Option('b', "multicastadapter", Required = false,
        HelpText = "IP address of the adapter to listen for multicast data (if not set, tries first binding adapter).")]
        public string MulticastAdapterAddress { get; set; }

        [Option('m', "multicastaddress", Required = true,
        HelpText = "Multicast address to subscribe this instance to.")]
        public string MulticastAddress { get; set; }

        [Option('g', "multicastgroup", Required = true,
        HelpText = "Multicast group (port number) to subscribe this instance to.")]
        public int MulticastGroup { get; set; }

        [Option('n', "nortpheaders", Required = false, Default = false,
        HelpText = "Optional instruction to skip the expected 12 byte RTP headers (meaning plain MPEGTS inside UDP is expected")]
        public bool NoRtpHeaders { get; set; }

        [Option('s', "buffersize", Required = false, Default = 100000,
        HelpText = "Optional instruction to control the number of TS packets cached within random access window buffer")]
        public int BufferDepth { get; set; }

        [Option('u', "urlidentifier", Required = true,
        HelpText = "Text identifier to append to base URL for identifying this specific stream.")]
        public string UrlIdentifier { get; set; }
    }
}
