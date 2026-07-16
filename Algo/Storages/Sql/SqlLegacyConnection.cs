namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Connection string resolution for the legacy database (now PostgreSQL). Reads the
/// STOCKSHARP_LEGACY_SQL_CONNECTION environment variable if set, otherwise falls back to a local-dev
/// PostgreSQL/Npgsql default matching Database/README.md's docker-compose instructions. There is no
/// config-file-based override here - reading an app.config connectionStrings section remains out of
/// scope for this brief.
/// </summary>
public static class SqlLegacyConnection
{
	// AAP 0.7.2 decision: the environment-variable NAME is deliberately RETAINED (not renamed) across
	// the SQL Server -> PostgreSQL engine change, to avoid a wider blast radius - the demo and the
	// Database README both reference it by this name. Inside docker-compose the `app` service sets this
	// same variable to the in-container value: Host=db;Port=5432;Database=stocksharp;Username=postgres;Password=postgres
	// (db = the compose service name); the fallback below is for a demo run directly on the host.
	private const string _envVarName = "STOCKSHARP_LEGACY_SQL_CONNECTION";

	private const string _localDevDefault =
		"Host=localhost;Port=5432;Database=stocksharp;Username=postgres;Password=postgres";

	/// <summary>
	/// Resolves the connection string to use, preferring the environment variable.
	/// </summary>
	public static string Resolve()
		=> Environment.GetEnvironmentVariable(_envVarName).IsEmpty(_localDevDefault);
}
