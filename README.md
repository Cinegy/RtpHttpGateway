# RtpHttpGateway

Use this tool to allow any standard HTTP client to pull any multicast data from the network and relay. It's a prototype, and designed to aide in debugging more than as a production-ready tool - part of the AMWA Labs work Cinegy are carrying out.

##How easy is it?

Well, we've added everything you need into a single teeny-tiny EXE again, which just depends on .NET 4.5. And then we gave it all a nice Apache license, so you can tinker and throw the tool wherever you need to on the planet.

Just run the EXE from inside a command-prompt, and the application will just run - although you might want to provide it with a hint to bind to a specific adapter.

##Command line arguments:

Run with a --help argument, and you will get interactive help information like this:

```
RtpHttpGateway 0.0.0.1
Copyright ©Cinegy GmbH 2017

  -a, --adapter              IP address of the adapter to serve HTTP requests from (if not set, tries first binding
                             adapter).

  -n, --nortpheaders         (Default: false) Optional instruction to skip the expected 12 byte RTP headers (meaning
                             plain MPEGTS inside UDP is expected

  -q, --quiet                (Default: false) Don't print anything to the console

  -l, --logfile              Optional file to record events to.

  -d, --descriptortags       (Default: ) Comma separated tag values added to all log entries for instance and machine
                             identification

  -e, --timeserieslogging    Record time slice metric data to.

  -v, --verboselogging       Creates event logs for all discontinuities and skips.

  -p, --port                 (Default: 8082) Port Number to listen for web serving requests (8082 if not set).

  --help                     Display this help screen.

  --version                  Display version information.

Hit enter to quit


```

Just to make your life easier, we auto-build this using AppVeyor - here is how we are doing right now: 

[![Build status](https://ci.appveyor.com/api/projects/status/sm2dhprb2sj27j0u?svg=true)](https://ci.appveyor.com/project/cinegy/rtphttpgateway/branch/master)

You can check out the latest compiled binary from the master or pre-master code here:

[AppVeyor RtpHttpGateway Project Builder](https://ci.appveyor.com/project/cinegy/rtphttpgateway/build/artifacts)