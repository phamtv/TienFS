-- =====================================================================================
-- PER-SERVICE DATABASE ISOLATION
-- -------------------------------------------------------------------------------------
-- Creates one SQL login per microservice, each restricted to ONLY its own database.
-- Without this, all three services share the same admin (sa) login, which means any
-- one of them could technically read or write another's database — the databases are
-- logically separate, but nothing at the SQL engine level actually enforces it.
--
-- This script works against BOTH targets:
--   - Local Docker SQL Server (localhost,14333) — run it once after `docker compose up`
--   - Azure SQL (production)  — run it once after the Bicep deployment creates the
--     server and the three (currently empty) databases
--
-- USAGE (local):
--   sqlcmd -S localhost,14333 -U sa -P "Sample_Password123!" -i init-db.sql
--
-- AZURE SQL IS DIFFERENT — READ BEFORE RUNNING THERE:
--   Azure SQL Database (unlike full SQL Server) does NOT support USE to switch
--   databases within one connection/script — each database needs its own separate
--   connection. This script's USE statements below will fail if pointed at Azure SQL
--   as-is. For Azure, run three separate sqlcmd invocations instead, each connecting
--   directly to one database, using CREATE USER ... WITH PASSWORD (a "contained
--   database user" — Azure SQL's equivalent that skips the separate server-level
--   CREATE LOGIN step this script uses for local SQL Server). This script is scoped
--   to local dev for now; ask if you want the three-separate-script Azure version too.
--
-- After running this, update each service's connection string (locally in
-- appsettings.Development.json, in production the Key Vault secret) to use its own
-- new login instead of sa/admin — see the CHANGE CONNECTION STRINGS note at the
-- bottom of this file.
-- =====================================================================================

-- Change these passwords before running against anything beyond local dev — these
-- are placeholder values, not meant to ship anywhere real.
DECLARE @originationPassword NVARCHAR(128) = 'Origination_Pw123!';
DECLARE @fundingPassword     NVARCHAR(128) = 'Funding_Pw123!';
DECLARE @servicingPassword   NVARCHAR(128) = 'Servicing_Pw123!';

-- -------------------------------------------------------------------------------------
-- Server-level logins — one per service. Created at the server scope (master), then
-- mapped to a database user with permissions scoped to only that service's database
-- in the sections below.
-- -------------------------------------------------------------------------------------
USE master;
GO

IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'tienfs_origination_svc')
    CREATE LOGIN tienfs_origination_svc WITH PASSWORD = 'Origination_Pw123!';
GO

IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'tienfs_funding_svc')
    CREATE LOGIN tienfs_funding_svc WITH PASSWORD = 'Funding_Pw123!';
GO

IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'tienfs_servicing_svc')
    CREATE LOGIN tienfs_servicing_svc WITH PASSWORD = 'Servicing_Pw123!';
GO

-- -------------------------------------------------------------------------------------
-- Create the three databases if they don't already exist yet — this script can run
-- BEFORE the applications have ever started (normally EF Core's EnsureCreated() is
-- what creates them, on first app startup). Creating them here too means the order
-- doesn't matter: run this script first and the apps will find their databases
-- already there, or run the apps first and this script's CREATE DATABASE calls
-- become harmless no-ops.
-- -------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'tienfs-origination-dev')
    CREATE DATABASE [tienfs-origination-dev];
GO

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'tienfs-funding-dev')
    CREATE DATABASE [tienfs-funding-dev];
GO

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'tienfs-servicing-dev')
    CREATE DATABASE [tienfs-servicing-dev];
GO

-- -------------------------------------------------------------------------------------
-- Origination database — only tienfs_origination_svc gets a user here, with db_owner
-- (needed since EF Core's EnsureCreated() creates/alters tables at startup — a real
-- migrations-based deployment could scope this down further to just
-- db_datareader + db_datawriter once the schema is created by a separate release step).
-- -------------------------------------------------------------------------------------
-- Local dev database name; swap to the *-origination-db name from Bicep outputs when
-- running this against Azure SQL instead.
USE [tienfs-origination-dev];
GO

IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'tienfs_origination_svc')
    CREATE USER tienfs_origination_svc FOR LOGIN tienfs_origination_svc;
GO

ALTER ROLE db_owner ADD MEMBER tienfs_origination_svc;
GO

-- -------------------------------------------------------------------------------------
-- Funding database — same pattern, only tienfs_funding_svc gets access.
-- -------------------------------------------------------------------------------------
USE [tienfs-funding-dev];
GO

IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'tienfs_funding_svc')
    CREATE USER tienfs_funding_svc FOR LOGIN tienfs_funding_svc;
GO

ALTER ROLE db_owner ADD MEMBER tienfs_funding_svc;
GO

-- -------------------------------------------------------------------------------------
-- Servicing database — same pattern, only tienfs_servicing_svc gets access.
-- -------------------------------------------------------------------------------------
USE [tienfs-servicing-dev];
GO

IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'tienfs_servicing_svc')
    CREATE USER tienfs_servicing_svc FOR LOGIN tienfs_servicing_svc;
GO

ALTER ROLE db_owner ADD MEMBER tienfs_servicing_svc;
GO

-- =====================================================================================
-- CHANGE CONNECTION STRINGS — after running this script, update:
--
-- src/LoanOrigination.Api/appsettings.Development.json:
--   "Sql-Origination-ConnectionString":
--     "Server=localhost,14333;Database=tienfs-origination-dev;
--      User Id=tienfs_origination_svc;Password=Origination_Pw123!;TrustServerCertificate=true;"
--
-- src/LoanFunding.Api/appsettings.Development.json:
--   "Sql-Funding-ConnectionString":
--     "Server=localhost,14333;Database=tienfs-funding-dev;
--      User Id=tienfs_funding_svc;Password=Funding_Pw123!;TrustServerCertificate=true;"
--
-- src/LoanServicing.Api/appsettings.Development.json:
--   "Sql-Servicing-ConnectionString":
--     "Server=localhost,14333;Database=tienfs-servicing-dev;
--      User Id=tienfs_servicing_svc;Password=Servicing_Pw123!;TrustServerCertificate=true;"
--
-- The sa login/password stays in docker-compose.yml (needed to run this script itself,
-- and for ad-hoc troubleshooting), but the applications themselves no longer use it —
-- each connects with its own scoped-down login instead.
-- =====================================================================================
