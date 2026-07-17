/*
	StockSharpLegacy - least-privilege application role (PostgreSQL 16)
	----------------------------------------
	Runs LAST in the container init sequence
	(001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql -> 005_AppRole.sql),
	as the bootstrap SUPERUSER (POSTGRES_USER), on FIRST initialization only. It
	creates a dedicated, NON-superuser login role that the application (the
	docker-compose `app` service) authenticates as at runtime, so the app NEVER
	connects with cluster-superuser privileges (security finding F17).

	Least privilege: the role is granted ONLY the DML it needs on the seven
	pure-storage tables (SELECT / INSERT / UPDATE / DELETE), plus CONNECT on the
	database and USAGE on the public schema. It gets NO DDL (no CREATE on the
	schema), NO ownership, and NO superuser. The schema, triggers, and seed data
	are all created by the bootstrap superuser via 001/003/004 BEFORE this script
	runs, and the demo performs only data operations - EnsurePortfolio/Security
	upserts, order/trade inserts, position upserts, order-status updates that fire
	the audit trigger, and reads - every one of which is covered by table DML.

	Two things deliberately need NO extra grant:
	  * Identity columns: every PK is GENERATED ALWAYS AS IDENTITY (not serial),
	    so PostgreSQL assigns the value as part of the INSERT itself - the role
	    needs no USAGE/SELECT on any sequence.
	  * The status-audit trigger function: functions are EXECUTEable by PUBLIC by
	    default, and the trigger runs SECURITY INVOKER, so its INSERT into
	    OrderStatusHistory succeeds under app_user's own table grant (above).

	Credentials are LOCAL-DEVELOPMENT ONLY (this compose stack is the local
	stand-in for the AWS model, AAP 0.7.2) and deliberately not secret; the same
	app_user / app_pw values appear in docker-compose.yml's `app` service
	connection string. Guarded with IF NOT EXISTS so a manual re-run is harmless
	(the container only runs init scripts once, on an empty data directory).
*/

-- Non-superuser login role for the application (idempotent create).
DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
		CREATE ROLE app_user LOGIN PASSWORD 'app_pw';
	END IF;
END
$$;

-- Connect to the database + use the public schema. No CREATE on the schema is
-- granted, so app_user cannot issue DDL.
GRANT CONNECT ON DATABASE stocksharp TO app_user;
GRANT USAGE ON SCHEMA public TO app_user;

-- DML only, on the seven existing pure-storage tables (table names are unquoted,
-- so they resolve to the lower-cased identifiers created by 001_Schema.sql).
GRANT SELECT, INSERT, UPDATE, DELETE ON
	Portfolios,
	Securities,
	RiskLimits,
	Orders,
	Trades,
	Positions,
	OrderStatusHistory
	TO app_user;
