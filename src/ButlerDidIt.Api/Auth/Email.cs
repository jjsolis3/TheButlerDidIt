using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ButlerDidIt.Api.Auth;

/// <summary>Outgoing mail (SMTP). Works with any provider: Resend, Mailgun, Postmark, SES, Gmail…</summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>The sender, e.g. "The Butler Did It &lt;butler@example.com&gt;".</summary>
    public string? From { get; set; }
}

public sealed class AppOptions
{
    /// <summary>
    /// The site's public address, e.g. https://mystery.example.com. Links in emails
    /// are built from this, never from the incoming request: an attacker could send
    /// a "forgot password" request with a fake Host header, and the email would then
    /// point the real user at the attacker's site, handing over the reset token.
    /// </summary>
    public string? PublicUrl { get; set; }
}

public interface IEmailSender
{
    /// <summary>True when email can be sent. Without it, reset links come from the admin instead.</summary>
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string text, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> email, IOptions<AppOptions> app, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(email.Value.Host) && !string.IsNullOrWhiteSpace(email.Value.From) && !string.IsNullOrWhiteSpace(app.Value.PublicUrl);

    public async Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        var o = email.Value;
        if (!IsConfigured || o.Host is null || o.From is null) throw new InvalidOperationException("Email is not configured.");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = text };

        using var client = new SmtpClient();
        // Auto: implicit TLS on port 465, STARTTLS when the server offers it (587).
        await client.ConnectAsync(o.Host, o.Port, SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.Username)) await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
        log.LogInformation("Sent \"{Subject}\" email", subject); // never log the address or the link
    }
}

/// <summary>The account emails and links. Tokens are URL-encoded because Identity's tokens contain '+' and '/'.</summary>
public static class AccountLinks
{
    public static string Reset(string baseUrl, string email, string token) =>
        $"{baseUrl.TrimEnd('/')}/reset-password?email={WebUtility.UrlEncode(email)}&token={WebUtility.UrlEncode(token)}";

    public static string Confirm(string baseUrl, string userId, string token) =>
        $"{baseUrl.TrimEnd('/')}/confirm-email?user={WebUtility.UrlEncode(userId)}&token={WebUtility.UrlEncode(token)}";

    public static string ResetEmail(string name, string link) => $"""
        Hello {name},

        Someone (hopefully you) asked to reset the password for your host account on The Butler Did It.
        Choose a new password here:

        {link}

        The link works for 3 hours. If you didn't ask for this, you can ignore this email; your password hasn't changed.
        """;

    public static string ConfirmEmail(string name, string link) => $"""
        Hello {name},

        Welcome to The Butler Did It! Please confirm this is your email address:

        {link}

        If you didn't create an account, you can ignore this email.
        """;
}

public static class AccountTokens
{
    /// <summary>How long reset and confirmation links work. Short, because a reset link is as good as a password.</summary>
    public static readonly TimeSpan Lifespan = TimeSpan.FromHours(3);
}
