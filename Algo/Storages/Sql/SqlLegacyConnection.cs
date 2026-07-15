namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Connection string resolution for the StockSharpLegacy database. Reads the
/// STOCKSHARP_LEGACY_SQL_CONNECTION environment variable if set, otherwise
/// falls back to a local dev default matching Database/README.md's Docker
/// instructions. There is no config-file-based override here - the real
/// system reads this from an app.config connectionStrings section, but that
/// plumbing was out of scope for this brief.
/// </summary>
public static class SqlLegacyConnection
{
	private const string _envVarName = "STOCKSHARP_LEGACY_SQL_CONNECTION";

	private const string _localDevDefault =
		"Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=DevTest_Passw0rd!;TrustServerCertificate=True;";

	/// <summary>
	/// Resolves the connection string to use, preferring the environment variable.
	/// </summary>
	public static string Resolve()
		=> Environment.GetEnvironmentVariable(_envVarName).IsEmpty(_localDevDefault);
}
