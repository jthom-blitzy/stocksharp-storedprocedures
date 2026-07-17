namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Connection string resolution for the legacy database (now PostgreSQL). Reads the
/// STOCKSHARP_LEGACY_SQL_CONNECTION environment variable if set, otherwise falls back to a local-dev
/// PostgreSQL/Npgsql default aligned with the docker-compose stack (host localhost, port 5432,
/// database stocksharp). There is no
/// config-file-based override here - reading an app.config connectionStrings section remains out of
/// scope for this brief.
/// </summary>
public static class SqlLegacyConnection
{
	// AAP 0.7.2 decision: the environment-variable NAME is deliberately RETAINED (not renamed) across
	// the SQL Server -> PostgreSQL engine change, to avoid a wider blast radius. The two places that use
	// this name are SqlLegacyConnection.Resolve() (below) and the docker-compose `app` service, which
	// sets this same variable to the in-container value (least-privilege app_user, GSS disabled):
	// Host=db;Port=5432;Database=stocksharp;Username=app_user;Password=app_pw;GSS Encryption Mode=Disable
	// (db = the compose service name); the fallback below is for a demo run directly on the host.
	private const string _envVarName = "STOCKSHARP_LEGACY_SQL_CONNECTION";

	// F19: "GSS Encryption Mode=Disable" stops Npgsql 10 (whose GssEncryptionMode
	// default is Prefer) from probing for libgssapi_krb5.so.2 on connect - the probe
	// is absent-Kerberos noise on Linux and can even hang connections. This local,
	// password-authenticated fallback has no use for GSSAPI, so it disables it here
	// exactly as the docker-compose connection string does.
	private const string _localDevDefault =
		"Host=localhost;Port=5432;Database=stocksharp;Username=postgres;Password=postgres;GSS Encryption Mode=Disable";

	/// <summary>
	/// Resolves the connection string to use, preferring the environment variable.
	/// A value that is null, empty, OR whitespace-only falls back to the local-dev default:
	/// a whitespace-only override (e.g. a variable set to spaces by a misconfigured launcher or
	/// compose file) is treated as "unset" rather than being handed to Npgsql verbatim, which would
	/// otherwise surface only as an obscure connect-time failure. A genuine connection string that
	/// merely has surrounding whitespace is preserved as-is.
	/// </summary>
	public static string Resolve()
		=> Environment.GetEnvironmentVariable(_envVarName).IsEmptyOrWhiteSpace(_localDevDefault);
}
