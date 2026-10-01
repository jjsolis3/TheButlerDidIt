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

    /// <summary>The link that confirms a new email address (it's sent to that address).</summary>
    public static string ChangeEmail(string baseUrl, string userId, string newEmail, string token) =>
        $"{baseUrl.TrimEnd('/')}/account/confirm-email?user={WebUtility.UrlEncode(userId)}&email={WebUtility.UrlEncode(newEmail)}&token={WebUtility.UrlEncode(token)}";

    public static string ChangeEmailEmail(string name, string link) => $"""
        Hello {name},

        To use this address for your host account on The Butler Did It, confirm it here:

        {link}

        The link works for 3 hours. Until you click it, your account keeps its old address.
        If you didn't ask for this, you can ignore this email.
        """;

    /// <summary>Sent to the old address, so a stolen session can't quietly move an account to another inbox.</summary>
    public static string EmailChangingNotice(string name, string newEmail) => $"""
        Hello {name},

        Someone signed in to your host account on The Butler Did It asked to change its email address to {newEmail}.
        It changes only if that address confirms it.

        If this wasn't you, change your password now, and use "Sign out everywhere else" on your account page.
        """;

    public static string PasswordChangedNotice(string name) => $"""
        Hello {name},

        The password for your host account on The Butler Did It was just changed.

        If this wasn't you, use "Forgot your password?" on the sign-in page to choose a new one, or ask the site's admin.
        """;

    /// <summary>An invite opens the sign-up form with the token filled in.</summary>
    public static string Invite(string baseUrl, string token) =>
        $"{baseUrl.TrimEnd('/')}/login?invite={WebUtility.UrlEncode(token)}";

    public static string InviteEmail(string from, string link, int days) => $"""
        Hello,

        {from} has invited you to host murder mysteries and escape rooms on The Butler Did It.
        Create your host account here:

        {link}

        The link works once, for {days} day{(days == 1 ? "" : "s")}. If you weren't expecting this, you can ignore this email.
        """;

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
