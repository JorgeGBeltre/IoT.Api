namespace IoT.Infrastructure.Email;

public sealed class EmailSettings
{
    public const string SectionName = "Email";

    public string SmtpServer { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderPassword { get; set; } = string.Empty;
    public string SenderName { get; set; } = "IoT Nexus Platform";
    public bool EnableSsl { get; set; } = true;
    public string ClientAppUrl { get; set; } = "http://localhost:3000";
}
