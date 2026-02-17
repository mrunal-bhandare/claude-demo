# Server Monitor — Console App
### .NET 8 Console | SQL Server | Task Scheduler

---

## How It Works

Run this `.exe` on a schedule via **Windows Task Scheduler**.
Each run it will:
1. Read `appsettings.json` for server list and email settings
2. Check each server (HTTP/HTTPS/TCP/ICMP)
3. Save every result to SQL Server
4. Send a **DOWN alert** email when a server fails N times in a row
5. Send a **RECOVERY alert** email when it comes back up
6. Write a daily log file to `Logs\servermonitor-YYYY-MM-DD.log`
7. Exit cleanly (exit code 0 = success, 1 = email error, 2 = config error)

---

## Quick Setup

### 1. Configure `appsettings.json`

```json
{
  "ConnectionString": "Server=YOUR_SERVER;Database=ServerMonitorDB;Trusted_Connection=True;TrustServerCertificate=True;",

  "Email": {
    "SmtpHost":       "smtp.gmail.com",
    "SmtpPort":       587,
    "UseSsl":         true,
    "SenderEmail":    "alerts@yourcompany.com",
    "SenderPassword": "your-app-password",
    "SenderName":     "Server Monitor",
    "Recipients":     ["admin@yourcompany.com", "ops@yourcompany.com"]
  },

  "Servers": [
    {
      "Name":             "Primary Web Server",
      "Host":             "192.168.1.100",
      "Port":             80,
      "Protocol":         "HTTP",
      "HealthCheckUrl":   "/health",
      "TimeoutSeconds":   10,
      "FailureThreshold": 3
    }
  ]
}
```

> **Gmail:** Use an [App Password](https://support.google.com/accounts/answer/185833), not your real password.
> Go to: Google Account → Security → 2-Step Verification → App passwords

### 2. Set Up SQL Server Database

Run the included SQL script in SSMS:
```sql
-- Open CreateDatabase.sql in SSMS and execute
-- OR run via command line:
sqlcmd -S YOUR_SERVER -i CreateDatabase.sql
```

The app also auto-creates tables on first run if they don't exist.

### 3. Build & Publish

```bash
# Standard build
dotnet build -c Release

# Publish as single self-contained .exe (no .NET runtime needed on target machine)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

The output `ServerMonitor.exe` + `appsettings.json` is all you need.

---

## Task Scheduler Setup

1. Open **Task Scheduler** → Create Basic Task
2. **Name:** `Server Monitor`
3. **Trigger:** Daily → Repeat every `5 minutes` (or your preferred interval)
4. **Action:** Start a program
   - **Program:** `C:\ServerMonitor\ServerMonitor.exe`
   - **Start in:** `C:\ServerMonitor\`
5. **Settings:**
   - ✅ Run whether user is logged on or not
   - ✅ Run with highest privileges
   - ✅ If the task is already running → Do not start a new instance

> **Tip:** Set the trigger repeat interval to match your monitoring needs.
> Every 5 minutes = near real-time. Every 1 minute = aggressive monitoring.

---

## Protocols Supported

| Protocol | What it checks |
|---|---|
| `HTTP`  | GET request to `http://host:port/healthCheckUrl` — expects 2xx |
| `HTTPS` | GET request to `https://host:port/healthCheckUrl` — expects 2xx |
| `TCP`   | Raw TCP socket connection to host:port |
| `ICMP`  | Ping (requires admin rights for ICMP on some systems) |

---

## Database Tables

| Table | Purpose |
|---|---|
| `SM_CheckLog`    | Every single check result with timestamp |
| `SM_ServerState` | Current failure count & alert-sent flag per server |
| `SM_AlertLog`    | History of all emails sent |
| `vw_ServerStatus`| View: latest status for each server |

---

## Email Providers

| Provider | SmtpHost | Port |
|---|---|---|
| Gmail | smtp.gmail.com | 587 |
| Outlook/Office365 | smtp.office365.com | 587 |
| SendGrid | smtp.sendgrid.net | 587 |
| Amazon SES | email-smtp.us-east-1.amazonaws.com | 587 |

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | All checks completed successfully |
| `1` | Checks ran but an email failed to send |
| `2` | Config error — app could not start |

Task Scheduler can be configured to alert on non-zero exit codes.
