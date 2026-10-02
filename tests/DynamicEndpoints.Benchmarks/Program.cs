using BenchmarkDotNet.Running;

// Benchmarks of dynamic endpoints against equivalent hand-written minimal APIs, plus the cost of a routing table swap.
// Everything runs in-process on TestServer (no ports, logging off, in-memory store).
//
// Run (always Release):
//   dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter *
//   dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter * --job short
//   dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter *RoutingSwap* --job short --warmupCount 3 --iterationCount 5
// Results land in BenchmarkDotNet.Artifacts/ (gitignored).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
