namespace SignalFish.Client.PerfTests
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using BenchmarkDotNet.Attributes;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Codec hot-path benchmarks (PLAN.md M1.6). Decode runs the whole
    /// vendored corpus per operation; encode writes one representative
    /// client session (authenticate, join, ready, start, relay payload,
    /// v3 signal, transport status, leave, ping) per operation into a
    /// reused, pre-sized buffer, so the numbers reflect the library's
    /// steady-state cost — the caller-owned buffer is poolable in real
    /// drivers. Steady-state zero allocations are enforced per direction
    /// by allocation-gate unit tests. Baselines live in
    /// docs/benchmarks.md.
    /// </summary>
    [MemoryDiagnoser]
    public class CodecBenchmarks
    {
        private const string Uuid = "00000000-0000-0000-0000-000000000000";

        private static readonly byte[] GameDataPayload = Encoding.UTF8.GetBytes(
            "{\"seq\": 42, \"pos\": {\"x\": 1.5, \"y\": -2.25, \"z\": 3.0}}"
        );

        private static readonly byte[] SignalPayload = Encoding.UTF8.GetBytes(
            "{\"candidate\": \"candidate:1 1 UDP 1 10.0.0.1 5000 typ host\"}"
        );

        private static readonly string[] Transports = { "relay", "webrtc" };
        private static readonly string[] Topologies = { "mesh" };
        private static readonly string[] Capabilities = { "room_operation_ids" };

        private byte[][] frames = Array.Empty<byte[]>();
        private ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();

        [GlobalSetup]
        public void Setup()
        {
            string goldenDirectory = Path.Combine(AppContext.BaseDirectory, "Golden");
            List<byte[]> corpus = new List<byte[]>();
            foreach (string fileName in Directory.GetFiles(goldenDirectory, "*.jsonl"))
            {
                foreach (string line in File.ReadAllLines(fileName))
                {
                    corpus.Add(Encoding.UTF8.GetBytes(line));
                }
            }

            frames = corpus.ToArray();
            buffer = new ArrayBufferWriter<byte>(4096);
        }

        [Benchmark(Baseline = true)]
        public int DecodeFullCorpus()
        {
            int kinds = 0;
            foreach (byte[] frame in frames)
            {
                kinds += (int)EnvelopeReader.Decode(frame).Kind;
            }

            return kinds;
        }

        [Benchmark]
        public int EncodeRepresentativeSession()
        {
            buffer.Clear();
            int start = buffer.WrittenCount;
            EnvelopeWriter.WriteAuthenticate(
                buffer,
                new AuthenticateMessage(
                    appId: "bench-app",
                    sdkVersion: SignalFishClientInfo.SdkVersion,
                    platform: "bench",
                    protocolVersion: 3,
                    supportedTransports: Transports,
                    supportedTopologies: Topologies,
                    requestedCapabilities: Capabilities
                )
            );
            EnvelopeWriter.WriteJoinRoom(
                buffer,
                new JoinRoomMessage("bench-game", "bencher", "ABCD12", password: "s3cret")
            );
            EnvelopeWriter.WritePlayerReady(buffer);
            EnvelopeWriter.WriteStartGame(buffer);
            EnvelopeWriter.WriteGameData(
                buffer,
                new GameDataMessage(GameDataPayload, GameDataClass.Latest, key: 7)
            );
            EnvelopeWriter.WriteSignal(buffer, new SignalMessage(Uuid, Uuid, SignalPayload));
            EnvelopeWriter.WriteTransportStatus(
                buffer,
                new TransportStatusMessage("webrtc", connected: true)
            );
            EnvelopeWriter.WriteLeaveRoom(buffer);
            EnvelopeWriter.WritePing(buffer);
            return buffer.WrittenCount - start;
        }
    }
}
