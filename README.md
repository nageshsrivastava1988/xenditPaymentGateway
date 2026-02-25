# Payment Gateway Admin Panel
## Deployment Document and User Manual

- **Document Version:** 1.0
- **Prepared On:** February 25, 2026
- **Application:** PaymentGateway (ASP.NET Core MVC, .NET 10)
- **Audience:** Client IT Operations Team, Application Administrators, Support Team

---

## 1. Purpose
This document provides end-to-end instructions to deploy, configure, run, and operate the Payment Gateway Admin Panel in client environments. It also includes a user manual for admin users.

## 2. Application Summary
- **Application Type:** ASP.NET Core MVC Web Application
- **Target Framework:** `.NET 10`
- **Database:** PostgreSQL
- **Key Features:**
  - Admin login and cookie-based authentication
  - First-user bootstrap from Login page (if no users exist)
  - Forgot/Reset password with single-use reset links
  - Change password for logged-in users
  - Payment reports with server-side filtering and pagination

## 3. Environment Prerequisites
### 3.1 Mandatory
- Linux server (recommended: Ubuntu 22.04 LTS or later)
- .NET 10 Runtime (or SDK for build-on-server)
- PostgreSQL 14+ (recommended 15+)
- Nginx (recommended reverse proxy for production)
- Network access to Xendit API endpoints

### 3.2 Recommended
- Dedicated system user for app service
- TLS certificate (Let’s Encrypt or enterprise CA)
- Daily PostgreSQL backups
- Centralized log collection/monitoring

## 4. Configuration Reference
The application reads settings from `appsettings.json` and environment-specific overrides such as `appsettings.Production.json`.

| Key | Description | Example |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string used by app | `Host=localhost;Port=5432;Database=payment_gateway_db;Username=postgres;Password=***;` |
| `Database:Schema` | Target schema for application tables | `public` |
| `Xendit:BaseUrl` | Xendit API base URL | `https://api.xendit.co/` |
| `Xendit:ApiVersion` | Xendit API version header value | `2024-11-11` |
| `Xendit:SecretKey` | Xendit secret key (sensitive) | `***` |
| `Xendit:AppKey` | Callback decryption key (sensitive) | `***` |
| `Xendit:SuccessReturnUrl` | Success redirect URL | `https://your-domain/payment/success` |
| `Xendit:FailureReturnUrl` | Failure redirect URL | `https://your-domain/payment/failed` |
| `Auth:ResetTokenExpiryMinutes` | Password reset link validity | `30` |
| `Email:SmtpHost`, `Email:From`, etc. | SMTP settings for password reset emails | `smtp.company.com` |
| `Logging:File:Path` | Directory for file logs | `logs` |

### Important Security Note
Do not keep production secrets in source-controlled `appsettings.json`. Use `appsettings.Production.json`, environment variables, or a secret manager.

## 5. Deployment Procedure (Production)

### 5.1 Publish Application
```bash
cd /path/to/PaymentGateway/PaymentGateway/PaymentGateway

dotnet restore
dotnet publish -c Release -o /opt/paymentgateway/app
```

### 5.2 Create Production Settings File
Create `/opt/paymentgateway/app/appsettings.Production.json`:

```json
{
  "Database": {
    "Schema": "public"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=127.0.0.1;Port=5432;Database=payment_gateway_db;Username=pg_user;Password=STRONG_PASSWORD;"
  },
  "Xendit": {
    "BaseUrl": "https://api.xendit.co/",
    "ApiVersion": "2024-11-11",
    "SecretKey": "REPLACE_ME",
    "AppKey": "REPLACE_ME",
    "SuccessReturnUrl": "https://client-domain.example/success",
    "FailureReturnUrl": "https://client-domain.example/failed",
    "StatementDescriptor": "Goods and Services"
  },
  "Auth": {
    "ResetTokenExpiryMinutes": 30
  },
  "Email": {
    "From": "no-reply@client-domain.example",
    "SmtpHost": "smtp.client-domain.example",
    "SmtpPort": 587,
    "UseSsl": true,
    "Username": "smtp_user",
    "Password": "smtp_password"
  },
  "Logging": {
    "File": {
      "Path": "/var/log/paymentgateway",
      "MinLevel": "Information"
    }
  }
}
```

### 5.3 Create System User and Permissions
```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin paymentgateway
sudo mkdir -p /opt/paymentgateway/app /var/log/paymentgateway
sudo chown -R paymentgateway:paymentgateway /opt/paymentgateway /var/log/paymentgateway
```

### 5.4 Configure systemd Service
Create `/etc/systemd/system/paymentgateway.service`:

```ini
[Unit]
Description=Payment Gateway Admin Panel
After=network.target

[Service]
WorkingDirectory=/opt/paymentgateway/app
ExecStart=/usr/bin/dotnet /opt/paymentgateway/app/PaymentGateway.dll
User=paymentgateway
Group=paymentgateway
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Enable and start service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable paymentgateway
sudo systemctl start paymentgateway
sudo systemctl status paymentgateway
```

### 5.5 Configure Nginx Reverse Proxy (Unix Socket)
The app listens on Unix socket in Production: `/tmp/paymentgateway.sock`.

```nginx
server {
    listen 80;
    server_name client-domain.example;

    location / {
        proxy_pass http://unix:/tmp/paymentgateway.sock;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Test and reload Nginx:

```bash
sudo nginx -t
sudo systemctl reload nginx
```

### 5.6 HTTPS (Recommended)
Configure TLS certificate and update Nginx server block for HTTPS using your standard certificate process.

## 6. Database Initialization Behavior
- On startup/first DB call, app ensures database exists (if DB user has permission)
- Ensures required schema and tables exist
- Seeds payment channel master data automatically
- User table is not pre-seeded from config

## 7. First Admin User Onboarding
1. Open application URL
2. Login page appears (`/account/login`)
3. If no user exists in DB, first successful login submission creates the first admin user
4. User is signed in and redirected to Report page

## 8. Admin User Manual

### 8.1 Login
- Navigate to application URL
- Enter registered email and password
- Click **Sign In**

### 8.2 Forgot Password / Reset Password
- Click **Forgot password** on Login page
- Enter registered email and submit
- System sends a one-time reset link to configured email
- Open link, enter new password, and submit
- Link is invalid after single use or expiry

### 8.3 Change Password
- After login, open **Security** in left menu
- Enter current password and new password
- Save new password

### 8.4 Report Page
- Access via left menu **Reports**
- Use filters: DateTime range, Status, Reference No
- Use page size: **10, 25, 50, 100, All**
- Use pagination controls: **First, Prev, page numbers, Next, Last**

## 9. Operations Runbook

### 9.1 Service Operations
```bash
sudo systemctl start paymentgateway
sudo systemctl stop paymentgateway
sudo systemctl restart paymentgateway
sudo systemctl status paymentgateway
```

### 9.2 Logs
- Systemd logs: `journalctl -u paymentgateway -f`
- Application file logs: configured via `Logging:File:Path`

### 9.3 Backup
```bash
pg_dump -h 127.0.0.1 -U pg_user -d payment_gateway_db -Fc -f payment_gateway_db_YYYYMMDD.dump
```

### 9.4 Restore
```bash
pg_restore -h 127.0.0.1 -U pg_user -d payment_gateway_db -c payment_gateway_db_YYYYMMDD.dump
```

## 10. Troubleshooting

| Issue | Possible Cause | Resolution |
|---|---|---|
| Blank/404 at root URL | Old process running old build | Restart service and confirm root redirects to `/account/login` |
| Cannot login | No user exists or wrong credentials | For first setup, use login form to create first user; otherwise validate credentials |
| Reset email not received | SMTP not configured or blocked | Verify `Email` settings and mail server connectivity |
| Database errors | Wrong connection string/permissions | Validate PostgreSQL credentials and DB access from app host |
| Nginx 502 | App service down or socket unavailable | Check `systemctl status paymentgateway` and socket path |

## 11. Endpoint Reference (High-Level)
- `/` -> redirects to `/account/login`
- `/account/login` (GET/POST)
- `/account/forgot-password` (GET/POST)
- `/account/reset-password` (GET/POST)
- `/account/change-password` (GET/POST)
- `/report` (GET, authenticated)

## 12. Deployment Handover Checklist
- [ ] Production secrets are not in source files
- [ ] Database connection verified
- [ ] Xendit connectivity verified
- [ ] SMTP tested for reset email
- [ ] First admin login tested
- [ ] Report filters and pagination tested
- [ ] Nginx + TLS tested
- [ ] Backup and restore tested

---
**End of Document**
