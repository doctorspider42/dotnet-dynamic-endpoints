namespace DynamicEndpoints.Cli;

// Not top-level statements: the tests see the CLI's internals and already have a global Program (the sample app).
internal static class CliProgram
{
    public static Task<int> Main(string[] args) => CliApplication.RunAsync(args, new CliEnvironment());
}
