-- Least-privilege application login for NAS POS Pro.
-- Run by an operator (sqlcmd -v AppPassword="..." DbName="NasPos" -i create-app-login.sql) AFTER migrations.
-- The API connects with this login: data read/write only. It cannot alter schema, manage logins or change server policy.
-- Migrations (schema changes) run separately with an owner/admin connection (`NasPos.Api --migrate`).

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'naspos_app')
    CREATE LOGIN [naspos_app] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DbName)];
ELSE
    ALTER LOGIN [naspos_app] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
GO

USE [$(DbName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'naspos_app')
    CREATE USER [naspos_app] FOR LOGIN [naspos_app];
GO

ALTER ROLE db_datareader ADD MEMBER [naspos_app];
ALTER ROLE db_datawriter ADD MEMBER [naspos_app];
-- No db_owner, no db_ddladmin, no server roles.
DENY ALTER ANY SCHEMA TO [naspos_app];
DENY VIEW DEFINITION TO [naspos_app];
GO
