-- Least-privilege application login for Kshethra Mithra.
-- Run by an operator (sqlcmd -v AppPassword="..." DbName="Kshethra" -i create-app-login.sql) AFTER migrations.
-- The API connects with this login: data read/write only. It cannot alter schema, manage logins or change server policy.
-- Migrations (schema changes) run separately with an owner/admin connection (`Kshethra.Api --migrate`).

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'kshethra_app')
    CREATE LOGIN [kshethra_app] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DbName)];
ELSE
    ALTER LOGIN [kshethra_app] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
GO

USE [$(DbName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'kshethra_app')
    CREATE USER [kshethra_app] FOR LOGIN [kshethra_app];
GO

ALTER ROLE db_datareader ADD MEMBER [kshethra_app];
ALTER ROLE db_datawriter ADD MEMBER [kshethra_app];
-- No db_owner, no db_ddladmin, no server roles.
DENY ALTER ANY SCHEMA TO [kshethra_app];
DENY VIEW DEFINITION TO [kshethra_app];
GO
