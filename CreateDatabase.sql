-- ============================================================
-- ServerMonitorDB — Manual Setup Script
-- Run in SSMS or: sqlcmd -S YOUR_SERVER -i CreateDatabase.sql
-- The app also creates these tables automatically on first run.
-- ============================================================

USE master;
GO

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'ServerMonitorDB')
    CREATE DATABASE ServerMonitorDB;
GO

USE ServerMonitorDB;
GO

-- Check log: every check result
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='SM_CheckLog' AND xtype='U')
CREATE TABLE SM_CheckLog (
    Id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    ServerName     NVARCHAR(200)  NOT NULL,
    Host           NVARCHAR(500)  NOT NULL,
    Port           INT            NOT NULL,
    Protocol       NVARCHAR(10)   NOT NULL,
    IsUp           BIT            NOT NULL,
    ResponseTimeMs INT            NOT NULL,
    HttpStatusCode INT            NULL,
    ErrorMessage   NVARCHAR(2000) NULL,
    CheckedAt      DATETIME2      NOT NULL DEFAULT GETUTCDATE()
);
GO

-- Server state: tracks failures and alert-sent flag between runs
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='SM_ServerState' AND xtype='U')
CREATE TABLE SM_ServerState (
    Id                        INT IDENTITY(1,1) PRIMARY KEY,
    ServerName                NVARCHAR(200) NOT NULL,
    Host                      NVARCHAR(500) NOT NULL,
    Port                      INT           NOT NULL,
    ConsecutiveFailures       INT           NOT NULL DEFAULT 0,
    AlertSentForCurrentOutage BIT           NOT NULL DEFAULT 0,
    OutageStartedAt           DATETIME2     NULL,
    LastCheckedAt             DATETIME2     NULL,
    CONSTRAINT UQ_SM_ServerState UNIQUE (ServerName, Host, Port)
);
GO

-- Alert log: history of all alerts sent
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='SM_AlertLog' AND xtype='U')
CREATE TABLE SM_AlertLog (
    Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    ServerName   NVARCHAR(200)  NOT NULL,
    AlertType    NVARCHAR(20)   NOT NULL,   -- DOWN / RECOVERY
    Subject      NVARCHAR(500)  NOT NULL,
    Recipients   NVARCHAR(2000) NOT NULL,
    IsSent       BIT            NOT NULL,
    ErrorMessage NVARCHAR(2000) NULL,
    SentAt       DATETIME2      NOT NULL DEFAULT GETUTCDATE()
);
GO

-- Useful view: current status of each server
CREATE OR ALTER VIEW vw_ServerStatus AS
SELECT
    s.ServerName,
    s.Host,
    s.Port,
    s.ConsecutiveFailures,
    s.AlertSentForCurrentOutage,
    s.OutageStartedAt,
    s.LastCheckedAt,
    c.IsUp,
    c.ResponseTimeMs,
    c.ErrorMessage AS LastError
FROM SM_ServerState s
OUTER APPLY (
    SELECT TOP 1 IsUp, ResponseTimeMs, ErrorMessage
    FROM SM_CheckLog
    WHERE ServerName = s.ServerName AND Host = s.Host AND Port = s.Port
    ORDER BY CheckedAt DESC
) c;
GO

PRINT 'ServerMonitorDB setup complete.';
GO
