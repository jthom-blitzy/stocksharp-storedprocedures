# =============================================================================
# Dockerfile — containerized build & run of the StockSharp "Legacy SQL" console
# demo (Samples/08_Misc/03_LegacySqlDemo, OutputType=Exe, .NET 10).
#
# Part of the SQL Server -> PostgreSQL migration + containerization effort. This
# image is launched by the repository-root docker-compose.yml
#   build: { context: ., dockerfile: Dockerfile }
# alongside a postgres:16 service, so the whole stack starts with a single
# `docker compose up`.
#
# Design (Agent Action Plan 0.3.2 / 0.5.1):
#   * Multi-stage build: .NET 10 SDK builder -> lightweight .NET 10 runtime.
#   * Build context MUST be the repository ROOT. The demo's transitive
#     project/props graph — Algo, BusinessEntities, Messages, Reporting,
#     Configuration, Charting.Interfaces and the shared common_*.props chain —
#     is resolved by MSBuild through real repo-root-relative paths, so every
#     referenced project/props file has to be present in the context. The
#     companion .dockerignore trims non-build artifacts (bin/obj/.git/.vs/QA and
#     the container files) while preserving all sources (*.csproj/*.props/*.cs/
#     *.sql). Publishing only the demo project builds just its dependency
#     subgraph, so copying the whole (trimmed) context is correct and safe.
#   * The PLAIN `runtime` image is used, NOT `aspnet`: the demo is a console
#     executable whose runtimeconfig requires Microsoft.NETCore.App (not
#     Microsoft.AspNetCore.App).
#   * Image tags drop the patch number (10.0) so they roll forward to the latest
#     .NET 10 patch automatically.
#   * There is no global.json / NuGet.config at the repo root, so the SDK image's
#     bundled .NET 10 SDK and the default nuget.org feed are used for restore.
# =============================================================================

# -----------------------------------------------------------------------------
# Stage 1 — build: restore + publish the console demo with the .NET 10 SDK.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# All COPY/publish paths below are relative to the build context (the repository
# root), mirroring the on-disk layout so the transitive .csproj/.props imports
# resolve exactly as they do for a local `dotnet publish`.
WORKDIR /src

# Copy the whole (.dockerignore-trimmed) build context. Copying everything is
# required because `dotnet publish` on the demo drives MSBuild to walk the demo's
# transitive project references and the shared common_*.props files by their real
# relative paths; a partial copy would break the restore/publish graph.
COPY . .

# Publish the demo (OutputType=Exe) in Release to a fixed, stage-local directory.
#
# `-f net10.0` is REQUIRED. The demo declares <TargetFrameworks> (plural, via
# common_target_net_new.props) even though it resolves to the single TFM
# net10.0; MSBuild treats any <TargetFrameworks> project as cross-targeting, so
# `dotnet publish` without an explicit framework fails with NETSDK1129
# ("The 'Publish' target is not supported without specifying a target
# framework"). Pinning `-f net10.0` selects the one TFM and publishes cleanly.
#
# `dotnet publish` restores by default, so no separate `dotnet restore` step is
# needed — a single publish command keeps the build minimal and robust.
RUN dotnet publish "Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj" \
    -c Release \
    -f net10.0 \
    -o /app/publish

# -----------------------------------------------------------------------------
# Stage 2 — runtime: run the published console DLL on the .NET 10 runtime.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

WORKDIR /app

# Bring in only the framework-dependent publish output from the build stage (the
# app DLL plus its managed dependencies, including Npgsql.dll). The SDK, the
# sources, and the NuGet caches stay behind in the build stage, keeping the
# runtime image small.
#
# --chown=app:app makes the copied files owned by the non-root `app` user we drop
# to below (finding F18), so they stay readable/executable after the privilege
# drop. (Publish output is world-readable by default; the explicit chown makes the
# ownership unambiguous rather than relying on that default.)
COPY --from=build --chown=app:app /app/publish .

# F18 — run as the built-in NON-root `app` user (uid/gid 1654) that the official
# mcr.microsoft.com/dotnet/runtime image ships, instead of the default root. The
# demo is an outbound PostgreSQL client that writes nothing to the image
# filesystem, so it needs no elevated privileges; dropping to `app` follows
# least-privilege container practice. Declared AFTER the COPY so the copy runs as
# root and the files are chowned to `app` for the runtime user.
USER app

# F19 — no Kerberos/GSSAPI library is installed in this runtime image ON PURPOSE.
# Npgsql 10 defaults GssEncryptionMode to Prefer and would otherwise try to load
# libgssapi_krb5.so.2 on every connection (absent from the Kerberos-less .NET Linux
# images), emitting a harmless-but-noisy stderr error and risking connection hangs.
# The connection string supplied by docker-compose.yml — and the SqlLegacyConnection
# host fallback — set "GSS Encryption Mode=Disable", which stops the probe at the
# source, so adding a Kerberos runtime dependency here is unnecessary.

# Run the published console assembly via the portable host (`dotnet <dll>`), so
# the entry point is independent of the native apphost.
#
# The assembly name is NOT the project file name: common_samples.props (imported
# last in the props chain) sets AssemblyName = StockSharp.Samples.$(SampleProjectName),
# and SampleProjectName strips the numeric prefix from "03_Misc.LegacySqlDemo" to
# "Misc.LegacySqlDemo", yielding StockSharp.Samples.Misc.LegacySqlDemo.dll.
#
# The demo reads its connection string from the STOCKSHARP_LEGACY_SQL_CONNECTION
# environment variable (supplied by docker-compose.yml) and otherwise falls back
# to a local PostgreSQL string, so no connection string is baked in here. No port
# is EXPOSEd — the demo is an outbound PostgreSQL client that runs its walkthrough
# once and exits.
ENTRYPOINT ["dotnet", "StockSharp.Samples.Misc.LegacySqlDemo.dll"]
