using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Sql;

/// <summary>What a query returns.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SqlQueryResult>))]
public enum SqlQueryResult
{
    /// <summary>A JSON array with one object per row (at most <see cref="SqlQueryConfig.MaxRows"/>).</summary>
    Rows,
    /// <summary>The first row as a JSON object, or <c>404</c> when there is none.</summary>
    Row,
    /// <summary>The first column of the first row as a JSON value, or <c>404</c> when there is no row.</summary>
    Value,
}

/// <summary>Configuration of the <c>sql-query</c> processor.</summary>
public sealed class SqlQueryConfig
{
    /// <summary>
    /// A single read-only statement (<c>SELECT</c>, <c>WITH … SELECT</c>, <c>VALUES</c>). Request parameters are referenced as
    /// placeholders – <c>@customerId</c> – and always sent as database parameters, never spliced into the text.
    /// </summary>
    public string? Query { get; set; }

    /// <summary>Name of a connection registered in <see cref="SqlQueryProcessorOptions.Connections"/>. Default: the default connection.</summary>
    public string? Connection { get; set; }

    public SqlQueryResult Result { get; set; } = SqlQueryResult.Rows;

    [Range(1, 10_000)]
    public int MaxRows { get; set; } = 100;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Settings of the <c>sql-query</c> processor, set by the application.</summary>
public sealed class SqlQueryProcessorOptions
{
    /// <summary>Connection factories by name; <c>""</c> is the default connection. Connections are opened and disposed per request.</summary>
    public IDictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; } =
        new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tenants that may use a connection, by connection name (multi-tenancy). Endpoints of a tenant can only use connections
    /// assigned to it – unassigned ones, the default connection included, are for shared endpoints only, so a tenant's admin can't
    /// query the application's or another tenant's database. Checked on save and before every run. Use <see cref="AllowTenants"/>.
    /// </summary>
    public IDictionary<string, ISet<string>> ConnectionTenants { get; } = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lets the endpoints of <paramref name="tenants"/> use the connection <paramref name="connection"/> (<c>""</c>: the default one).</summary>
    public SqlQueryProcessorOptions AllowTenants(string connection, params string[] tenants)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ConnectionTenants.TryGetValue(connection, out var allowed))
        {
            ConnectionTenants[connection] = allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        allowed.UnionWith(tenants);
        return this;
    }

    /// <summary>Shared endpoints (no tenant) may use every connection; a tenant's endpoints only those assigned to it.</summary>
    internal bool IsAvailable(string connection, string? tenant) =>
        tenant is null || (ConnectionTenants.TryGetValue(connection, out var allowed) && allowed.Contains(tenant));

    /// <summary>Placeholder prefix of the provider: <c>@</c> (default – SQL Server, SQLite, PostgreSQL/Npgsql, MySQL) or <c>:</c> (Oracle).</summary>
    public char ParameterPrefix { get; set; } = '@';

    /// <summary>
    /// Runs every query in a transaction that is always rolled back, so a statement with side effects that slipped through the
    /// checks is undone (where the database supports it). Default <c>true</c>.
    /// </summary>
    public bool RollBackTransaction { get; set; } = true;
}

/// <summary>
/// Runs a parameterized, read-only SQL query and returns the rows as JSON. Register with <c>AddSqlQueryProcessor(…)</c>.
/// Whoever can edit definitions can read whatever the connection's database user can read – use a dedicated read-only user.
/// </summary>
[DynamicProcessor("sql-query",
    Description = "Runs a read-only, parameterized SQL query and returns the rows as JSON.",
    ConfigurationExample = """{ "query": "SELECT id, name FROM customers WHERE country = @country", "result": "Rows", "maxRows": 100 }""")]
public sealed class SqlQueryProcessor(IOptions<SqlQueryProcessorOptions> options) : DynamicEndpointProcessor<SqlQueryConfig>
{
    protected override IEnumerable<string> Validate(SqlQueryConfig config) => Check(config, tenant: null);

    protected override IEnumerable<string> Validate(SqlQueryConfig config, DynamicEndpointDefinition definition) => Check(config, definition.Tenant);

    private IEnumerable<string> Check(SqlQueryConfig config, string? tenant)
    {
        if (string.IsNullOrWhiteSpace(config.Query))
        {
            yield return "'query' is required.";
            yield break;
        }

        foreach (var error in SqlStatementGuard.Check(config.Query, options.Value.ParameterPrefix).Errors)
        {
            yield return error;
        }

        // A tenant learns only about the connections it may use.
        var settings = options.Value;
        var connection = config.Connection ?? string.Empty;
        var available = settings.Connections.Keys.Where(k => settings.IsAvailable(k, tenant)).Select(k => k.Length == 0 ? "(default)" : k).ToList();
        if (!settings.Connections.ContainsKey(connection) || (tenant is not null && !settings.IsAvailable(connection, tenant)))
        {
            var list = available.Count == 0 ? "none" : string.Join(", ", available);
            yield return tenant is not null && settings.Connections.ContainsKey(connection)
                ? $"Connection '{(connection.Length == 0 ? "(default)" : connection)}' isn't available to tenant '{tenant}'. Available: {list}."
                : config.Connection is null
                    ? "No default connection is registered."
                    : $"Unknown connection '{config.Connection}'. Available: {list}.";
        }
    }

    protected override async Task<IResult> ProcessAsync(DynamicRequest request, SqlQueryConfig config)
    {
        var settings = options.Value;
        var statement = SqlStatementGuard.Check(config.Query!, settings.ParameterPrefix);
        if (statement.Errors.Count > 0)
        {
            // Saved before the checks existed or tightened – never run it.
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The query of this endpoint is not allowed.");
        }

        if (!settings.IsAvailable(config.Connection ?? string.Empty, request.Endpoint.Tenant))
        {
            // The connection was taken away from the endpoint's tenant after it was saved.
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The connection of this endpoint is not allowed.");
        }

        var cancellationToken = request.RequestAborted;
        await using var connection = settings.Connections[config.Connection ?? string.Empty](request.Services);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = settings.RollBackTransaction ? await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement.Statement;
            command.CommandTimeout = config.TimeoutSeconds;
            foreach (var name in statement.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = settings.ParameterPrefix + name;
                parameter.Value = ToDatabase(Find(request.Parameters, name));
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
            switch (config.Result)
            {
                case SqlQueryResult.Value:
                    return await reader.ReadAsync(cancellationToken) ? Results.Json(ToJson(reader.GetValue(0))) : Results.NotFound();
                case SqlQueryResult.Row:
                    return await reader.ReadAsync(cancellationToken) ? Results.Json(Row(reader)) : Results.NotFound();
                default:
                    var rows = new JsonArray();
                    while (rows.Count < config.MaxRows && await reader.ReadAsync(cancellationToken))
                    {
                        rows.Add(Row(reader));
                    }

                    return Results.Json(rows);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private static JsonNode? Find(JsonObject parameters, string name) =>
        parameters.TryGetPropertyValue(name, out var value)
            ? value
            : parameters.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static object ToDatabase(JsonNode? value) => value switch
    {
        null => DBNull.Value,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            JsonValueKind.Number => decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => DBNull.Value,
        },
        // Arrays and objects are passed as JSON text, e.g. for json_each / OPENJSON / jsonb functions.
        _ => value.ToJsonString(),
    };

    private static JsonObject Row(DbDataReader reader)
    {
        var row = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = ToJson(reader.GetValue(i));
        }

        return row;
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dateTime => JsonValue.Create(dateTime.ToString("O", CultureInfo.InvariantCulture)),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), DynamicEndpointsJson.SerializerOptions),
    };
}
