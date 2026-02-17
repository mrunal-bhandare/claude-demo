/*
 * ============================================================
 *  SERVER MONITOR - Console Application
 *  .NET 8  |  SQL Server  |  Task Scheduler friendly
 * ============================================================
 *
 *  HOW IT WORKS:
 *  Run this exe once via Windows Task Scheduler every N minutes.
 *  It checks every server in appsettings.json, writes results
 *  to SQL Server, and sends email alerts when a server is DOWN
 *  or has RECOVERED. Then it exits cleanly.
 *
 *  TASK SCHEDULER SETUP:
 *    Action  : Start a program
 *    Program : C:\ServerMonitor\ServerMonitor.exe
 *    Trigger : Every 5 minutes (or your preferred interval)
 *
 *  PUBLISH AS SINGLE EXE:
 *    dotnet publish -c Release -r win-x64 --self-contained
 * ============================================================
 */

using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
using MimeKit;

internal class Program
{
    static async Task Main(string[] args)
    {
        // ── Load config ───────────────────────────────────────────────────────
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"[ERROR] appsettings.json not found at: {configPath}");
            Environment.Exit(2);
            return;
        }

        AppConfig config;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Config is null after deserialization.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to read appsettings.json: {ex.Message}");
            Environment.Exit(2);
            return;
        }

        // ── Log file setup ────────────────────────────────────────────────────
        var logDir  = Path.Combine(AppContext.BaseDirectory, "Logs");
        var logPath = Path.Combine(logDir, $"servermonitor-{DateTime.Now:yyyy-MM-dd}.log");
        Directory.CreateDirectory(logDir);

        void Log(string level, string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }

        Log("INFO", "========== Server Monitor Run Started ==========");

        // ── Bootstrap DB ──────────────────────────────────────────────────────
        var db = new Database(config.ConnectionString, Log);
        try
        {
            await db.EnsureTablesCreatedAsync();
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Database connection failed: {ex.Message}");
            Log("ERROR", "Check your ConnectionString in appsettings.json");
            Environment.Exit(2);
            return;
        }

        // ── Check each server ─────────────────────────────────────────────────
        var checker  = new ServerChecker(Log);
        var mailer   = new EmailSender(config.Email, Log);
        int exitCode = 0;

        foreach (var server in config.Servers)
        {
            Log("INFO", $"Checking [{server.Name}] {server.Protocol}://{server.Host}:{server.Port}");

            // 1. Perform the check
            var result = await checker.CheckAsync(server);

            Log(result.IsUp ? "INFO" : "WARN",
                $"  Status: {(result.IsUp ? "UP" : "DOWN")} | " +
                $"ResponseTime: {result.ResponseTimeMs}ms" +
                (result.ErrorMessage != null ? $" | Error: {result.ErrorMessage}" : ""));

            // 2. Save result to DB
            await db.SaveCheckResultAsync(server.Name, server.Host, server.Port, server.Protocol, result);

            // 3. Get current server state
            var state = await db.GetServerStateAsync(server.Name, server.Host, server.Port);

            bool shouldAlertDown     = !result.IsUp
                                       && state.ConsecutiveFailures >= server.FailureThreshold
                                       && !state.AlertSentForCurrentOutage;

            bool shouldAlertRecovery = result.IsUp
                                       && state.WasPreviouslyDown
                                       && state.AlertSentForCurrentOutage;

            // 4. Send DOWN alert
            if (shouldAlertDown)
            {
                Log("WARN", $"  Threshold reached ({state.ConsecutiveFailures} failures). Sending DOWN alert...");
                bool sent = await mailer.SendDownAlertAsync(server, result);
                await db.UpdateAlertSentAsync(server.Name, server.Host, server.Port, alertSent: true, recovered: false);
                if (!sent) exitCode = 1;
            }

            // 5. Send RECOVERY alert
            else if (shouldAlertRecovery)
            {
                var downtime = state.OutageStartedAt.HasValue
                    ? DateTime.UtcNow - state.OutageStartedAt.Value
                    : TimeSpan.Zero;

                Log("INFO", $"  Server recovered after {downtime.TotalMinutes:F0} min. Sending RECOVERY alert...");
                bool sent = await mailer.SendRecoveryAlertAsync(server, result, downtime);
                await db.UpdateAlertSentAsync(server.Name, server.Host, server.Port, alertSent: false, recovered: true);
                if (!sent) exitCode = 1;
            }
        }

        Log("INFO", "========== Server Monitor Run Completed ==========");

        // Keep console open when running from Visual Studio (not Task Scheduler)
        if (System.Diagnostics.Debugger.IsAttached)
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        Environment.Exit(exitCode);
    }
}


// =============================================================================
// CONFIG MODELS
// =============================================================================

public class AppConfig
{
    public string ConnectionString    { get; set; } = string.Empty;
    public EmailConfig Email          { get; set; } = new();
    public List<ServerConfig> Servers { get; set; } = new();
}

public class EmailConfig
{
    public string SmtpHost       { get; set; } = string.Empty;
    public int    SmtpPort       { get; set; } = 587;
    public bool   UseSsl         { get; set; } = true;
    public string SenderEmail    { get; set; } = string.Empty;
    public string SenderPassword { get; set; } = string.Empty;
    public string SenderName     { get; set; } = "Server Monitor";
    public List<string> Recipients { get; set; } = new();
}

public class ServerConfig
{
    public string  Name             { get; set; } = string.Empty;
    public string  Host             { get; set; } = string.Empty;
    public int     Port             { get; set; } = 80;
    public string  Protocol         { get; set; } = "HTTP";
    public string? HealthCheckUrl   { get; set; }
    public int     TimeoutSeconds   { get; set; } = 10;
    public int     FailureThreshold { get; set; } = 3;
}

public class CheckResult
{
    public bool    IsUp           { get; set; }
    public int     ResponseTimeMs { get; set; }
    public int?    HttpStatusCode { get; set; }
    public string? ErrorMessage   { get; set; }
    public DateTime CheckedAt    { get; set; } = DateTime.UtcNow;
}

public class ServerState
{
    public int       ConsecutiveFailures       { get; set; }
    public bool      WasPreviouslyDown         { get; set; }
    public bool      AlertSentForCurrentOutage { get; set; }
    public DateTime? OutageStartedAt           { get; set; }
}


// =============================================================================
// SERVER CHECKER  -  HTTP / HTTPS / TCP / ICMP
// =============================================================================

public class ServerChecker
{
    private readonly Action<string, string> _log;

    public ServerChecker(Action<string, string> log) => _log = log;

    public async Task<CheckResult> CheckAsync(ServerConfig server)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return server.Protocol.ToUpperInvariant() switch
            {
                "HTTP"  or "HTTPS" => await CheckHttpAsync(server),
                "TCP"              => await CheckTcpAsync(server),
                "ICMP"             => await CheckIcmpAsync(server),
                _                  => await CheckHttpAsync(server)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult
            {
                IsUp           = false,
                ResponseTimeMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage   = ex.Message
            };
        }
    }

    private async Task<CheckResult> CheckHttpAsync(ServerConfig server)
    {
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var scheme = server.Protocol.ToLower();
        var url    = string.IsNullOrWhiteSpace(server.HealthCheckUrl)
            ? $"{scheme}://{server.Host}:{server.Port}/"
            : $"{scheme}://{server.Host}:{server.Port}{server.HealthCheckUrl}";

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(server.TimeoutSeconds)
        };

        var response = await client.GetAsync(url);
        sw.Stop();

        bool isUp = response.IsSuccessStatusCode;
        return new CheckResult
        {
            IsUp           = isUp,
            ResponseTimeMs = (int)sw.ElapsedMilliseconds,
            HttpStatusCode = (int)response.StatusCode,
            ErrorMessage   = isUp ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };
    }

    private async Task<CheckResult> CheckTcpAsync(ServerConfig server)
    {
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(server.TimeoutSeconds));
        await tcp.ConnectAsync(server.Host, server.Port, cts.Token);
        sw.Stop();
        return new CheckResult { IsUp = true, ResponseTimeMs = (int)sw.ElapsedMilliseconds };
    }

    private async Task<CheckResult> CheckIcmpAsync(ServerConfig server)
    {
        using var ping  = new Ping();
        var reply = await ping.SendPingAsync(server.Host, server.TimeoutSeconds * 1000);
        bool isUp = reply.Status == IPStatus.Success;
        return new CheckResult
        {
            IsUp           = isUp,
            ResponseTimeMs = (int)reply.RoundtripTime,
            ErrorMessage   = isUp ? null : $"Ping failed: {reply.Status}"
        };
    }
}


// =============================================================================
// DATABASE  -  SQL Server plain ADO.NET (no EF Core)
// =============================================================================

public class Database
{
    private readonly string _connStr;
    private readonly Action<string, string> _log;

    public Database(string connectionString, Action<string, string> log)
    {
        _connStr = connectionString;
        _log     = log;
    }

    public async Task EnsureTablesCreatedAsync()
    {
        const string sql = @"
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

            IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='SM_AlertLog' AND xtype='U')
            CREATE TABLE SM_AlertLog (
                Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
                ServerName   NVARCHAR(200)  NOT NULL,
                AlertType    NVARCHAR(20)   NOT NULL,
                Subject      NVARCHAR(500)  NOT NULL,
                Recipients   NVARCHAR(2000) NOT NULL,
                IsSent       BIT            NOT NULL,
                ErrorMessage NVARCHAR(2000) NULL,
                SentAt       DATETIME2      NOT NULL DEFAULT GETUTCDATE()
            );";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        _log("INFO", "Database tables verified.");
    }

    public async Task SaveCheckResultAsync(string name, string host, int port,
        string protocol, CheckResult result)
    {
        const string sql = @"
            INSERT INTO SM_CheckLog
                (ServerName, Host, Port, Protocol, IsUp, ResponseTimeMs, HttpStatusCode, ErrorMessage, CheckedAt)
            VALUES
                (@Name, @Host, @Port, @Protocol, @IsUp, @Ms, @Http, @Err, @At);

            IF EXISTS (SELECT 1 FROM SM_ServerState WHERE ServerName=@Name AND Host=@Host AND Port=@Port)
                UPDATE SM_ServerState SET
                    ConsecutiveFailures       = CASE WHEN @IsUp=1 THEN 0 ELSE ConsecutiveFailures+1 END,
                    AlertSentForCurrentOutage = CASE WHEN @IsUp=1 THEN 0 ELSE AlertSentForCurrentOutage END,
                    OutageStartedAt           = CASE
                                                    WHEN @IsUp=0 AND ConsecutiveFailures=0 THEN @At
                                                    WHEN @IsUp=1 THEN NULL
                                                    ELSE OutageStartedAt
                                                END,
                    LastCheckedAt = @At
                WHERE ServerName=@Name AND Host=@Host AND Port=@Port
            ELSE
                INSERT INTO SM_ServerState
                    (ServerName, Host, Port, ConsecutiveFailures, AlertSentForCurrentOutage, OutageStartedAt, LastCheckedAt)
                VALUES
                    (@Name, @Host, @Port,
                     CASE WHEN @IsUp=1 THEN 0 ELSE 1 END,
                     0,
                     CASE WHEN @IsUp=1 THEN NULL ELSE @At END,
                     @At);";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Name",     name);
        cmd.Parameters.AddWithValue("@Host",     host);
        cmd.Parameters.AddWithValue("@Port",     port);
        cmd.Parameters.AddWithValue("@Protocol", protocol);
        cmd.Parameters.AddWithValue("@IsUp",     result.IsUp ? 1 : 0);
        cmd.Parameters.AddWithValue("@Ms",       result.ResponseTimeMs);
        cmd.Parameters.AddWithValue("@Http",     (object?)result.HttpStatusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Err",      (object?)result.ErrorMessage   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@At",       result.CheckedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<ServerState> GetServerStateAsync(string name, string host, int port)
    {
        const string sql = @"
            SELECT
                s.ConsecutiveFailures,
                s.AlertSentForCurrentOutage,
                s.OutageStartedAt,
                (SELECT TOP 1 IsUp
                 FROM SM_CheckLog
                 WHERE ServerName=@Name AND Host=@Host AND Port=@Port
                 ORDER BY CheckedAt DESC
                 OFFSET 1 ROWS) AS PreviousIsUp
            FROM SM_ServerState s
            WHERE s.ServerName=@Name AND s.Host=@Host AND s.Port=@Port";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Host", host);
        cmd.Parameters.AddWithValue("@Port", port);

        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync())
            return new ServerState();

        bool prevWasDown = !rdr.IsDBNull(3) && rdr.GetBoolean(3) == false;

        return new ServerState
        {
            ConsecutiveFailures       = rdr.GetInt32(0),
            AlertSentForCurrentOutage = rdr.GetBoolean(1),
            OutageStartedAt           = rdr.IsDBNull(2) ? null : rdr.GetDateTime(2),
            WasPreviouslyDown         = prevWasDown
        };
    }

    public async Task UpdateAlertSentAsync(string name, string host, int port,
        bool alertSent, bool recovered)
    {
        const string sql = @"
            UPDATE SM_ServerState SET
                AlertSentForCurrentOutage = @Sent,
                OutageStartedAt           = CASE WHEN @Recovered=1 THEN NULL ELSE OutageStartedAt END
            WHERE ServerName=@Name AND Host=@Host AND Port=@Port";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Name",      name);
        cmd.Parameters.AddWithValue("@Host",      host);
        cmd.Parameters.AddWithValue("@Port",      port);
        cmd.Parameters.AddWithValue("@Sent",      alertSent ? 1 : 0);
        cmd.Parameters.AddWithValue("@Recovered", recovered ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }
}


// =============================================================================
// EMAIL SENDER  -  MailKit / SMTP
// =============================================================================

public class EmailSender
{
    private readonly EmailConfig _cfg;
    private readonly Action<string, string> _log;

    public EmailSender(EmailConfig cfg, Action<string, string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task<bool> SendDownAlertAsync(ServerConfig server, CheckResult result)
    {
        var subject = $"ALERT: {server.Name} is DOWN";
        var body    = BuildDownBody(server, result);
        return await SendAsync(subject, body);
    }

    public async Task<bool> SendRecoveryAlertAsync(ServerConfig server, CheckResult result, TimeSpan downtime)
    {
        var subject = $"RECOVERED: {server.Name} is back UP";
        var body    = BuildRecoveryBody(server, result, downtime);
        return await SendAsync(subject, body);
    }

    private async Task<bool> SendAsync(string subject, string htmlBody)
    {
        if (_cfg.Recipients == null || _cfg.Recipients.Count == 0)
        {
            _log("WARN", "No recipient emails configured. Skipping alert.");
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_cfg.SenderName, _cfg.SenderEmail));
            foreach (var r in _cfg.Recipients)
                message.To.Add(MailboxAddress.Parse(r));

            message.Subject = subject;
            message.Body    = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_cfg.SmtpHost, _cfg.SmtpPort,
                _cfg.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
            await smtp.AuthenticateAsync(_cfg.SenderEmail, _cfg.SenderPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _log("INFO", $"  Email sent: {subject}");
            return true;
        }
        catch (Exception ex)
        {
            _log("ERROR", $"  Failed to send email: {ex.Message}");
            return false;
        }
    }

    private static string BuildDownBody(ServerConfig server, CheckResult result) => $@"
        <!DOCTYPE html><html><head><style>
          body{{font-family:'Segoe UI',Arial,sans-serif;background:#f4f4f4;margin:0;padding:20px}}
          .card{{background:#fff;border-radius:8px;max-width:580px;margin:auto;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.12)}}
          .hdr{{background:#c0392b;color:#fff;padding:24px;text-align:center}}
          .hdr h1{{margin:0;font-size:26px}}
          .body{{padding:24px}}
          table{{width:100%;border-collapse:collapse;margin-top:12px}}
          td{{padding:9px 12px;border-bottom:1px solid #eee;font-size:14px}}
          td:first-child{{font-weight:bold;color:#555;width:38%}}
          .badge{{background:#c0392b;color:#fff;border-radius:4px;padding:3px 9px;font-size:12px}}
          .note{{margin-top:18px;padding:12px;background:#fef9e7;border-left:4px solid #f39c12;border-radius:4px;font-size:13px}}
          .ftr{{background:#f8f8f8;text-align:center;padding:12px;font-size:11px;color:#aaa}}
        </style></head><body>
          <div class='card'>
            <div class='hdr'><h1>Server Down Alert</h1><p style='margin:6px 0 0'>Immediate attention required</p></div>
            <div class='body'>
              <p>The following server has been detected as <strong>DOWN</strong>:</p>
              <table>
                <tr><td>Server Name</td><td>{server.Name}</td></tr>
                <tr><td>Host</td><td>{server.Host}</td></tr>
                <tr><td>Port</td><td>{server.Port}</td></tr>
                <tr><td>Protocol</td><td>{server.Protocol}</td></tr>
                <tr><td>Status</td><td><span class='badge'>DOWN</span></td></tr>
                <tr><td>Error</td><td style='color:#c0392b'>{result.ErrorMessage ?? "No response received"}</td></tr>
                <tr><td>HTTP Code</td><td>{(result.HttpStatusCode.HasValue ? result.HttpStatusCode.ToString() : "N/A")}</td></tr>
                <tr><td>Response Time</td><td>{result.ResponseTimeMs} ms</td></tr>
                <tr><td>Detected At (UTC)</td><td>{result.CheckedAt:yyyy-MM-dd HH:mm:ss}</td></tr>
              </table>
              <div class='note'>Please investigate immediately. Check server logs, network, and running services.</div>
            </div>
            <div class='ftr'>Generated by Server Monitor</div>
          </div>
        </body></html>";

    private static string BuildRecoveryBody(ServerConfig server, CheckResult result, TimeSpan downtime)
    {
        var dtStr = downtime.TotalMinutes < 1 ? $"{downtime.TotalSeconds:F0} seconds"
                  : downtime.TotalHours   < 1 ? $"{downtime.TotalMinutes:F0} minutes"
                  : $"{downtime.TotalHours:F1} hours";

        return $@"
        <!DOCTYPE html><html><head><style>
          body{{font-family:'Segoe UI',Arial,sans-serif;background:#f4f4f4;margin:0;padding:20px}}
          .card{{background:#fff;border-radius:8px;max-width:580px;margin:auto;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.12)}}
          .hdr{{background:#1e8449;color:#fff;padding:24px;text-align:center}}
          .hdr h1{{margin:0;font-size:26px}}
          .body{{padding:24px}}
          table{{width:100%;border-collapse:collapse;margin-top:12px}}
          td{{padding:9px 12px;border-bottom:1px solid #eee;font-size:14px}}
          td:first-child{{font-weight:bold;color:#555;width:38%}}
          .badge{{background:#1e8449;color:#fff;border-radius:4px;padding:3px 9px;font-size:12px}}
          .ftr{{background:#f8f8f8;text-align:center;padding:12px;font-size:11px;color:#aaa}}
        </style></head><body>
          <div class='card'>
            <div class='hdr'><h1>Server Recovered</h1><p style='margin:6px 0 0'>Service has been restored</p></div>
            <div class='body'>
              <p>The following server has <strong>recovered</strong> and is back online:</p>
              <table>
                <tr><td>Server Name</td><td>{server.Name}</td></tr>
                <tr><td>Host</td><td>{server.Host}</td></tr>
                <tr><td>Port</td><td>{server.Port}</td></tr>
                <tr><td>Protocol</td><td>{server.Protocol}</td></tr>
                <tr><td>Status</td><td><span class='badge'>UP</span></td></tr>
                <tr><td>Response Time</td><td>{result.ResponseTimeMs} ms</td></tr>
                <tr><td>Total Downtime</td><td>{dtStr}</td></tr>
                <tr><td>Recovered At (UTC)</td><td>{result.CheckedAt:yyyy-MM-dd HH:mm:ss}</td></tr>
              </table>
            </div>
            <div class='ftr'>Generated by Server Monitor</div>
          </div>
        </body></html>";
    }
}
