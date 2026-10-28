using System.Globalization;
using IoT.Application.Common.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace IoT.Infrastructure.Email;

public sealed class MailKitEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<MailKitEmailService> _logger;

    public MailKitEmailService(IConfiguration configuration, ILogger<MailKitEmailService> logger)
    {
        var section = configuration.GetSection(EmailSettings.SectionName);
        _settings = new EmailSettings
        {
            SmtpServer = section["SmtpServer"] ?? string.Empty,
            Port = int.TryParse(section["Port"] ?? section["SmtpPort"], out var port) ? port : 587,
            SenderEmail = section["SenderEmail"] ?? string.Empty,
            SenderName = section["SenderName"] ?? "IoT Nexus Platform",
            SenderPassword = section["SenderPassword"] ?? section["Password"] ?? string.Empty,
            EnableSsl = !bool.TryParse(section["EnableSsl"], out var ssl) || ssl,
            ClientAppUrl = section["ClientAppUrl"] ?? "http://localhost:3000"
        };
        _logger = logger;
    }

    public async Task<bool> SendTemporaryPasswordEmailAsync(
        string toEmail,
        string username,
        string tempPassword,
        string loginUrl,
        string? companyName = null,
        string? fullName = null,
        CancellationToken cancellationToken = default)
    {
        var subject = "Tus credenciales de acceso — Plataforma IoT Nexus";
        var company = string.IsNullOrWhiteSpace(companyName) ? "IoT Enterprise Solutions" : companyName;
        var name = string.IsNullOrWhiteSpace(fullName) ? username : fullName;
        var effectiveUrl = string.IsNullOrWhiteSpace(loginUrl) ? _settings.ClientAppUrl : loginUrl;

        var bodyHtml = $@"<!DOCTYPE html>
<html lang=""es"">
<head>
<meta charset=""UTF-8"" />
<style>
  body {{ margin:0; padding:0; background-color:#080C16; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; color:#F8FAFC; }}
  .card {{ width:560px; margin:40px auto; background-color:#0F172A; border:1px solid #1E293B; border-radius:16px; overflow:hidden; }}
  .header {{ background:linear-gradient(135deg, #00F2FE 0%, #4FACFE 100%); padding:24px 36px; }}
  .title {{ font-size:20px; font-weight:800; color:#080C16; margin:0; }}
  .content {{ padding:32px 36px; }}
  .box {{ background-color:#1E293B; border:1px solid #334155; border-radius:12px; padding:20px; margin:20px 0; }}
  .field {{ font-size:13px; color:#94A3B8; margin-bottom:4px; text-transform:uppercase; letter-spacing:0.05em; }}
  .val {{ font-size:16px; font-weight:700; color:#00F2FE; font-family:monospace; margin-bottom:12px; }}
  .btn {{ display:inline-block; background:linear-gradient(135deg, #00F2FE 0%, #4FACFE 100%); color:#080C16 !important; font-weight:700; text-decoration:none; padding:14px 32px; border-radius:10px; margin:16px 0; }}
  .footer {{ border-top:1px solid #1E293B; padding:16px 36px; font-size:12px; color:#64748B; text-align:center; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""header"">
      <h1 class=""title"">IoT Nexus Platform — Acceso Corporativo</h1>
    </div>
    <div class=""content"">
      <p style=""font-size:15px; line-height:22px; margin-top:0;"">Hola <strong>{name}</strong>,</p>
      <p style=""font-size:14px; line-height:22px; color:#CBD5E1;"">
        Has sido dado de alta en la plataforma de telemetría y rastreo IoT de <strong>{company}</strong>. A continuación se presentan tus credenciales de acceso inicial:
      </p>
      <div class=""box"">
        <div class=""field"">Usuario / Identificador:</div>
        <div class=""val"">{username}</div>
        <div class=""field"">Contraseña Temporal:</div>
        <div class=""val"" style=""color:#34D399; font-size:18px;"">{tempPassword}</div>
      </div>
      <p style=""font-size:13px; color:#F59E0B; margin:16px 0;"">
        ⚠️ Por políticas de seguridad, el sistema te solicitará cambiar esta contraseña en tu primer inicio de sesión.
      </p>
      <div style=""text-align:center;"">
        <a href=""{effectiveUrl}"" class=""btn"">Iniciar Sesión en el Portal</a>
      </div>
    </div>
    <div class=""footer"">
      IoT Nexus Enterprise &bull; Telemetría Avanzada y GPS Tracking
    </div>
  </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, subject, bodyHtml, cancellationToken);
    }

    public async Task<bool> SendPasswordResetOtpEmailAsync(
        string toEmail,
        string username,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        var subject = $"Código de Recuperación OTP [{otpCode}] — IoT Nexus";

        var bodyHtml = $@"<!DOCTYPE html>
<html lang=""es"">
<head>
<meta charset=""UTF-8"" />
<style>
  body {{ margin:0; padding:0; background-color:#080C16; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; color:#F8FAFC; }}
  .card {{ width:520px; margin:40px auto; background-color:#0F172A; border:1px solid #1E293B; border-radius:16px; overflow:hidden; }}
  .header {{ background:#1E293B; border-bottom:2px solid #00F2FE; padding:20px 32px; }}
  .title {{ font-size:18px; font-weight:700; color:#00F2FE; margin:0; }}
  .content {{ padding:32px; text-align:center; }}
  .otp {{ display:inline-block; font-family:monospace; font-size:36px; font-weight:800; letter-spacing:0.25em; color:#00F2FE; background-color:#080C16; border:2px dashed #00F2FE; padding:16px 28px; border-radius:12px; margin:24px 0; }}
  .footer {{ border-top:1px solid #1E293B; padding:16px; font-size:11px; color:#64748B; text-align:center; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""header"">
      <h1 class=""title"">Recuperación de Contraseña</h1>
    </div>
    <div class=""content"">
      <p style=""font-size:14px; color:#94A3B8; margin-top:0;"">Hola <strong>{username}</strong>,</p>
      <p style=""font-size:14px; line-height:20px; color:#CBD5E1;"">
        Utiliza el siguiente código OTP de verificación para restablecer tu contraseña. Este código expirará en 15 minutos:
      </p>
      <div class=""otp"">{otpCode}</div>
      <p style=""font-size:12px; color:#64748B; margin:0;"">
        Si no solicitaste este cambio, puedes ignorar este correo con total tranquilidad. Tu cuenta permanece protegida.
      </p>
    </div>
    <div class=""footer"">
      Seguridad IoT Nexus Platform &bull; Aislamiento Multi-Tenant
    </div>
  </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, subject, bodyHtml, cancellationToken);
    }

    public async Task<bool> SendPasswordChangedNotificationEmailAsync(
        string toEmail,
        string username,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var subject = "Contraseña Modificada Exitosamente — IoT Nexus";
        var esCulture = new CultureInfo("es-DO");
        var nowText = DateTime.UtcNow.ToString("dd MMM yyyy, hh:mm tt UTC", esCulture);

        var bodyHtml = $@"<!DOCTYPE html>
<html lang=""es"">
<head>
<meta charset=""UTF-8"" />
<style>
  body {{ margin:0; padding:0; background-color:#080C16; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; color:#F8FAFC; }}
  .card {{ width:520px; margin:40px auto; background-color:#0F172A; border:1px solid #1E293B; border-radius:16px; padding:32px; }}
  .badge {{ background-color:#064E3B; color:#34D399; font-weight:700; padding:6px 14px; border-radius:8px; display:inline-block; margin-bottom:16px; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""badge"">✓ Contraseña Actualizada</div>
    <h2 style=""margin:0 0 12px; color:#F8FAFC; font-size:18px;"">Tu contraseña ha sido cambiada</h2>
    <p style=""font-size:14px; color:#94A3B8; line-height:22px;"">
      Estimado/a <strong>{username}</strong>, confirmamos que la contraseña de tu cuenta fue actualizada exitosamente el <strong>{nowText}</strong>.
    </p>
    <div style=""background:#080C16; border:1px solid #1E293B; border-radius:8px; padding:14px; font-size:13px; color:#CBD5E1; margin:16px 0;"">
      <div><strong>Dirección IP:</strong> {ipAddress ?? "Registrada en auditoría"}</div>
      <div><strong>Dispositivo:</strong> {deviceInfo ?? "Navegador Web"}</div>
    </div>
    <p style=""font-size:12px; color:#EF4444; margin:0;"">
      Si no fuiste tú quien realizó esta modificación, contacta de inmediato al administrador de tu empresa.
    </p>
  </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, subject, bodyHtml, cancellationToken);
    }

    public async Task<bool> SendSecurityAlertEmailAsync(
        string toEmail,
        string username,
        string eventName,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var subject = $"Alerta de Seguridad: {eventName} — IoT Nexus";

        var bodyHtml = $@"<!DOCTYPE html>
<html lang=""es"">
<head>
<meta charset=""UTF-8"" />
<style>
  body {{ margin:0; padding:0; background-color:#080C16; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; color:#F8FAFC; }}
  .card {{ width:520px; margin:40px auto; background-color:#0F172A; border:1px solid #EF4444; border-radius:16px; padding:32px; }}
  .alert {{ color:#EF4444; font-size:16px; font-weight:800; margin-bottom:12px; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""alert"">⚠️ Alerta de Seguridad en tu Cuenta</div>
    <p style=""font-size:14px; color:#CBD5E1;"">
      Detectamos actividad inusual en la cuenta de <strong>{username}</strong>: <strong>{eventName}</strong>.
    </p>
    <p style=""font-size:13px; color:#94A3B8;"">
      IP: {ipAddress ?? "No disponible"} &bull; Dispositivo: {deviceInfo ?? "No disponible"}
    </p>
  </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, subject, bodyHtml, cancellationToken);
    }

    private async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.SmtpServer) || string.IsNullOrWhiteSpace(_settings.SenderEmail))
        {
            _logger.LogInformation("SMTP no configurado en appsettings.json. Simulando despacho exitoso de correo a: {ToEmail} | Asunto: {Subject}", toEmail, subject);
            return true;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.SenderName, _settings.SenderEmail));
            message.To.Add(new MailboxAddress(toEmail, toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder { HtmlBody = htmlContent };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            if (_settings.AllowInvalidCertificates)
            {
                _logger.LogWarning("SEGURIDAD SMTP DEGRADADA: AllowInvalidCertificates=true. Se aceptan certificados SMTP no confiables (posible riesgo de MITM). No usar en producción.");
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            }
            else
            {
                client.CheckCertificateRevocation = true;
            }

            var socketOptions = _settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : (_settings.Port == 587 ? SecureSocketOptions.StartTlsWhenAvailable : (_settings.EnableSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None));

            await client.ConnectAsync(_settings.SmtpServer, _settings.Port, socketOptions, cancellationToken);

            if (!string.IsNullOrEmpty(_settings.SenderPassword))
            {
                await client.AuthenticateAsync(_settings.SenderEmail, _settings.SenderPassword, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Correo enviado vía MailKit exitosamente a {ToEmail} con asunto: {Subject}", toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar correo vía MailKit a {ToEmail}: {Message}", toEmail, ex.Message);
            return false;
        }
    }
}
