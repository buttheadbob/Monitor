using BenchmarkDotNet.Running;

namespace SenX.Monitor.Benchmarks;

/// <summary>
/// Windows, both runtimes:
///   dotnet run -c Release -f net10.0 --project bench/SenX.Monitor.Benchmarks -- --filter "*" --runtimes net48 net10.0 --memory
/// Linux:
///   dotnet run -c Release -f net10.0 --project bench/SenX.Monitor.Benchmarks -- --filter "*" --memory
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
