using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class NetworkPrivacy
{
    internal const string ApplyArgument = "--apply-network-policy";
    internal const string RemoveArgument = "--remove-network-policy";
    internal const string SettingsArgument = "--privacy";
    private const string RulePrefix = "NeonLoop private copy outbound block ";
    private const string RuleGroup = "NeonLoop Network Privacy";
    private static readonly string[] Programs = { "ROM.exe", "UnityCrashHandler64.exe" };

    internal static string BlockSummary => $"Windows Firewall adds two outbound block rules under {RuleGroup}, " +
        $"one for each private game executable ({string.Join(" and ", Programs)}). Both rules apply to all network profiles. " +
        "Windows asks for administrator approval if the rules need to change.";

    internal static string ReadChoice(string effects)
    {
        string file = Path.Combine(effects, "NetworkChoice.txt");
        if (!File.Exists(file))
            return null;
        string choice = File.ReadAllText(file).Trim();
        return choice == "block" || choice == "allow" ? choice : null;
    }

    internal static void Ensure(string effects, string build, string choice)
    {
        string[] paths = PrivatePrograms(effects, build);
        bool block = choice == "block";
        if (Current(paths, block))
            return;
        var start = new ProcessStartInfo(Path.Combine(EffectsLauncher.InstallFolder, ManagedInstall.Launcher)) {
            UseShellExecute = true, Verb = "runas"
        };
        start.ArgumentList.Add(ApplyArgument);
        start.ArgumentList.Add(choice);
        start.ArgumentList.Add(build);
        if (ManagedInstall.IsManaged(EffectsLauncher.InstallFolder))
            start.ArgumentList.Add(effects);
        try
        {
            using (var process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("Windows did not start the network settings helper.");
                process.WaitForExit();
                if (process.ExitCode != 0 || !Current(paths, block))
                    throw new InvalidOperationException("Windows did not apply the selected network rules.");
            }
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode != 1223)
                throw;
            throw new InvalidOperationException("Network settings were cancelled. The game was not started.");
        }
    }

    internal static void Apply(string effects, string build, string choice)
    {
        if (choice != "block" && choice != "allow")
            throw new ArgumentException("Unknown network setting.");
        string[] paths = PrivatePrograms(effects, build);
        dynamic rules = OpenPolicy().Rules;
        foreach (string path in paths)
        {
            string name = RuleName(path);
            dynamic rule = FindRule(rules, name);
            if (choice == "allow")
            {
                if (rule != null)
                    rules.Remove(name);
                continue;
            }
            bool add = rule == null;
            if (add)
                rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            rule.Name = name;
            rule.Grouping = RuleGroup;
            rule.Description = "Outbound block selected for the NeonLoop private game copy.";
            rule.ApplicationName = path;
            rule.Protocol = 256;
            rule.Direction = 2;
            rule.Action = 0;
            rule.Profiles = int.MaxValue;
            rule.Enabled = true;
            if (add)
                rules.Add(rule);
        }
    }

    internal static void RemoveInstalledRules(string installation, string runtime)
    {
        string effects = Path.Combine(runtime, "Effects");
        if (InstalledRules(effects).Length == 0)
            return;
        if (ManagedInstall.RunProcess(Environment.ProcessPath!, true, RemoveArgument, installation, effects) != 0 ||
            InstalledRules(effects).Length != 0)
            throw new IOException("Windows couldn’t remove NeonLoop’s firewall rules. Run Uninstall again.");
    }

    internal static void RemoveRules(string installation, string effects)
    {
        effects = ManagedInstall.CheckFirewallTarget(effects, installation);
        dynamic rules = OpenPolicy().Rules;
        foreach (string name in InstalledRules(effects))
            rules.Remove(name);
    }

    private static string[] InstalledRules(string effects)
    {
        string gameRoot = Path.Combine(effects, "bin", "Game");
        var names = new List<string>();
        foreach (dynamic rule in OpenPolicy().Rules)
        {
            string name = rule.Name;
            if (!name.StartsWith(RulePrefix, StringComparison.Ordinal) ||
                !string.Equals((string)rule.Grouping, RuleGroup, StringComparison.Ordinal))
                continue;
            string path = rule.ApplicationName;
            if (string.IsNullOrEmpty(path) || !Programs.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                continue;
            string build = Path.GetDirectoryName(path);
            if (string.Equals(Path.GetDirectoryName(build), gameRoot, StringComparison.OrdinalIgnoreCase) &&
                name == RuleName(path))
                names.Add(name);
        }
        return names.ToArray();
    }

    private static dynamic OpenPolicy()
    {
        return Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true));
    }

    private static bool Current(string[] paths, bool block)
    {
        dynamic policy = OpenPolicy();
        if (block)
        {
            int profiles = policy.CurrentProfileTypes;
            foreach (int profile in new[] { 1, 2, 4 })
                if ((profiles & profile) != 0 && !policy.FirewallEnabled[profile])
                    throw new InvalidOperationException("Enable Windows Defender Firewall on the active network before using the outbound block.");
        }
        dynamic rules = policy.Rules;
        foreach (string path in paths)
        {
            dynamic rule = FindRule(rules, RuleName(path));
            if (block ? rule == null || !rule.Enabled || rule.Direction != 2 || rule.Action != 0 ||
                rule.Profiles != int.MaxValue || rule.Protocol != 256 ||
                !string.Equals((string)rule.ApplicationName, path, StringComparison.OrdinalIgnoreCase) : rule != null)
                return false;
        }
        return true;
    }

    private static dynamic FindRule(dynamic rules, string name)
    {
        try { return rules.Item(name); }
        catch (FileNotFoundException) { return null; }
        catch (COMException error)
        {
            if (error.ErrorCode != unchecked((int)0x80070002))
                throw;
            return null;
        }
    }

    private static string[] PrivatePrograms(string effects, string build)
    {
        if (build.Length == 0 || build.Length > 12)
            throw new ArgumentException("Invalid game build.");
        foreach (char character in build)
            if (character < '0' || character > '9')
                throw new ArgumentException("Invalid game build.");
        string folder = Path.Combine(effects, "bin", "Game", build);
        var paths = new string[Programs.Length];
        for (int i = 0; i < paths.Length; ++i)
            paths[i] = Path.GetFullPath(Path.Combine(folder, Programs[i]));
        return paths;
    }

    private static string RuleName(string path)
    {
        using (var sha = SHA256.Create())
            return RulePrefix + BitConverter.ToString(sha.ComputeHash(
                Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
    }

    internal const string Details =
        "Steam achievements and statistics\r\n" +
        "The original 2064 game initializes Steamworks and calls Steam to store achievement IDs and progress statistics. " +
        "The Steam client manages its own connections.\r\n\r\n" +
        "Unity Analytics\r\n" +
        "MidBoss's packaged Windows build includes Unity Analytics 6.3.0. Its SDK lists collect.analytics.unity3d.com and " +
        "standard session and device events. If active and consented, these can include event and session IDs, timestamps, install or " +
        "user IDs, platform, build and project IDs, store, app and SDK versions, locale, OS, CPU, GPU, RAM, display size and resolution, and battery or volume values when available. " +
        "Unity also lists IP address. No game-authored analytics calls were found in the base gameplay assembly.\r\n\r\n" +
        "Unity diagnostics\r\n" +
        "The game ships Unity's crash helper. Unity documents installation IDs, crash reports, exception logs, and stability data when " +
        "diagnostics are enabled. The exact live traffic, account consent, and remote settings were not verified.\r\n\r\n" +
        "Who receives it\r\n" +
        "These services ship with the original game. Unity and Valve provide the respective services. " +
        "Good Trip Good Tip adds no telemetry to NeonLoop. The optional block also stops other direct outbound traffic from the two private executables.\r\n\r\n" +
        "Scope and responsibility\r\n" +
        "This summary covers what Good Trip Good Tip found in the reviewed Windows build and its bundled SDKs. " +
        "Undisclosed transfers, server-side settings, and later updates may change what the original game sends. " +
        "To the extent permitted by applicable law, Good Trip Good Tip disclaims responsibility for undisclosed data collection or " +
        "transfers by the original game, its developer, or third-party services.";
}
