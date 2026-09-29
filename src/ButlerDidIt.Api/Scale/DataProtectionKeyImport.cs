using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Scale;

/// <summary>
/// Moving from key files to the database (DataProtection:Store=Database) must keep the old keys:
/// they encrypt the hosts' sign-in cookies and, more importantly, the AI API keys saved on the
/// Admin → AI page, which could not be read any more with fresh keys.
///
/// So when the database store is on and DataProtection:KeysPath still points at the old folder,
/// any key file the table doesn't have yet is copied in. Files are left in place, and running
/// this again changes nothing.
/// </summary>
public static class DataProtectionKeyImport
{
    public static async Task RunAsync(IConfiguration config, AppDbContext db, ILogger log)
    {
        if (!string.Equals(config["DataProtection:Store"], "Database", StringComparison.OrdinalIgnoreCase)) return;
        if (config["DataProtection:KeysPath"] is not { Length: > 0 } path || !Directory.Exists(path)) return;

        var known = await db.DataProtectionKeys.Select(k => k.FriendlyName).ToHashSetAsync();
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(path, "key-*.xml"))
        {
            // The file store names each key "key-{id}.xml" and the database store uses the same "key-{id}" name.
            var name = Path.GetFileNameWithoutExtension(file);
            if (known.Contains(name)) continue;
            db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = name, Xml = await File.ReadAllTextAsync(file) });
            added++;
        }
        if (added == 0) return;
        await db.SaveChangesAsync();
        log.LogInformation("Copied {Count} sign-in key(s) from {Path} into the database", added, path);
    }
}
