using System.Data.Common;
using DynamicEndpoints.Sql;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsSqlExtensions
{
    /// <summary>
    /// Registers <see cref="SqlQueryProcessor"/> (<c>sql-query</c>) with <paramref name="connection"/> as the default connection,
    /// e.g. <c>_ =&gt; new NpgsqlConnection(readOnlyConnectionString)</c>. Further connections go to
    /// <see cref="SqlQueryProcessorOptions.Connections"/>. Use a database user that can only read what the endpoints may expose.
    /// </summary>
    public static IDynamicEndpointsBuilder AddSqlQueryProcessor(
        this IDynamicEndpointsBuilder builder,
        Func<IServiceProvider, DbConnection> connection,
        Action<SqlQueryProcessorOptions>? configure = null,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        builder.Services.Configure<SqlQueryProcessorOptions>(o =>
        {
            o.Connections[string.Empty] = connection;
            configure?.Invoke(o);
        });
        return builder.AddProcessor<SqlQueryProcessor>(name);
    }
}
