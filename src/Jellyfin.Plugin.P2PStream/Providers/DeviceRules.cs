using Jellyfin.Plugin.P2PStream.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Per-device limits for Auto, e.g. "the living room TV can't play 4K": when a matching device plays a title on Auto,
/// the best release within its limits plays instead. Choosing a specific version in the Version menu always wins.
/// </summary>
public static class DeviceRules
{
    /// <summary>The device a request comes from: its name, its custom name (Dashboard &gt; Devices) and the app.</summary>
    public sealed record DeviceIdentity(string? Device, string? CustomName, string? App)
    {
        public IEnumerable<string> Names => new[] { CustomName, Device, App }.Where(n => !string.IsNullOrWhiteSpace(n))!;

        public override string ToString() => string.Join(" / ", Names);
    }

    public static async Task<DeviceIdentity?> IdentifyAsync(HttpContext http, IServiceProvider services)
    {
        var auth = services.GetService<IAuthorizationContext>();
        if (auth is null)
        {
            return null;
        }

        var info = await auth.GetAuthorizationInfo(http).ConfigureAwait(false);
        string? custom = null;
        if (!string.IsNullOrEmpty(info.DeviceId))
        {
            try
            {
                custom = services.GetService<IDeviceManager>()?.GetDeviceOptions(info.DeviceId)?.CustomName;
            }
            catch (Exception)
            {
                // unknown device
            }
        }

        return new DeviceIdentity(info.Device, custom, info.Client);
    }

    /// <summary>The first rule that matches the device, if any.</summary>
    public static DeviceRule? Match(DeviceIdentity? device, IEnumerable<DeviceRule>? rules)
    {
        if (device is null || rules is null)
        {
            return null;
        }

        return rules.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Match) && (r.MaxResolution > 0 || r.MaxSizeGb > 0)
            && device.Names.Any(n => n.Contains(r.Match.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public static async Task<(DeviceRule? Rule, DeviceIdentity? Device)> FindAsync(HttpContext http, IServiceProvider services)
    {
        var rules = Plugin.Instance?.Configuration.DeviceRules;
        if (rules is not { Length: > 0 })
        {
            return (null, null);
        }

        var device = await IdentifyAsync(http, services).ConfigureAwait(false);
        return (Match(device, rules), device);
    }

    /// <summary>True if the release fits the rule. A release whose resolution or size isn't known fits.</summary>
    public static bool Allows(DeviceRule rule, ReleaseCandidate r) =>
        (rule.MaxResolution <= 0 || r.Resolution <= 0 || r.Resolution <= rule.MaxResolution)
        && (rule.MaxSizeGb <= 0 || r.Size <= 0 || r.Size <= rule.MaxSizeGb * (1L << 30));

    /// <summary>
    /// The best release within the rule (the list is ranked best first). Releases that say their resolution are
    /// preferred over ones that don't, since an unlabelled one could be 4K.
    /// </summary>
    public static ReleaseCandidate? BestWithin(DeviceRule rule, IReadOnlyList<ReleaseCandidate> ranked) =>
        ranked.FirstOrDefault(r => r.Resolution > 0 && Allows(rule, r)) ?? ranked.FirstOrDefault(r => Allows(rule, r));

    /// <summary>"up to 1080p", "up to 1080p and 8 GB".</summary>
    public static string Describe(DeviceRule rule)
    {
        var parts = new List<string>();
        if (rule.MaxResolution > 0)
        {
            parts.Add(rule.MaxResolution == 2160 ? "4K" : rule.MaxResolution + "p");
        }

        if (rule.MaxSizeGb > 0)
        {
            parts.Add(rule.MaxSizeGb + " GB");
        }

        return "up to " + string.Join(" and ", parts);
    }
}
