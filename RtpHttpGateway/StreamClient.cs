using System;
using System.IO;
using System.Threading;
using Cinegy.TsDecoder.Buffers;

namespace RtpHttpGateway
{
    public class StreamClient
    {
        private readonly RingBuffer _ringBuffer;

        private Thread _workerThread;
        
        public StreamClient(RingBuffer ringBuffer)
        {
            _ringBuffer = ringBuffer;
        }

        private const int RtpHeaderSize = 12;
        
        public int ClientPosition { get; private set; }
       
        public BinaryWriter OutputWriter
        {
            private get;
            set;
        }

        public string ClientAddress { get; set; }

        public void Start()
        {
            var ts = new ThreadStart(delegate
            {
                WebClientConnectionWorker(this);
            });

            _workerThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            _workerThread.Start();
        }

        public void Stop()
        {
            _workerThread?.Abort();
        }
        
        public ThreadState? ThreadState => _workerThread?.ThreadState;

        private void WebClientConnectionWorker(StreamClient client)
        {
            client.ClientPosition = _ringBuffer.NextAddPosition;
            var data = new byte[1500];

            while (true)
            {
                while (client.ClientPosition == _ringBuffer.NextAddPosition)
                {
                    Thread.Sleep(1);
                }

                if (client.ClientPosition > _ringBuffer.BufferSize)
                    client.ClientPosition = 0;

                int dataLen;
                if (_ringBuffer.Peek(client.ClientPosition, ref data, out dataLen) != 0)
                {
                    Console.WriteLine("Resizing buffer");
                    data = new byte[dataLen];
                    _ringBuffer.Peek(client.ClientPosition, ref data, out dataLen);
                }

                try
                {
                    if (dataLen != 0)
                    {
                        client.OutputWriter.Write(data, RtpHeaderSize, dataLen - RtpHeaderSize);
                    }
                }
                catch (Exception)
                {
                    return;
                }
                client.ClientPosition++;
            }
        }
    }
}
