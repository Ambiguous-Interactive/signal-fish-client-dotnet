namespace SignalFish.Client.PerfTests
{
    using BenchmarkDotNet.Running;

    /// <summary>Entry point: <c>dotnet run -c Release -- --filter *</c>.</summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkSwitcher.FromAssembly(typeof(CodecBenchmarks).Assembly).Run(args);
        }
    }
}
