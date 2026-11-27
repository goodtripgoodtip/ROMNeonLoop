using System.Diagnostics;

namespace GameInstaller;

public static class WindowsSetupProbes
{
    public static async Task<bool> NvidiaMeetsRequirementsAsync(Version minimumDriver, Version minimumCompute, CancellationToken cancellationToken)
    {
        string system = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        string driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        string? utility = new[] { system, driver }.FirstOrDefault(File.Exists);
        if (utility is null)
            return false;
        InstallFiles.CheckNoLinks(utility);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(utility)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--query-gpu=driver_version,compute_cap");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        using Process probe = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the GPU compatibility query.");
        Task<string> stdout = probe.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = probe.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await probe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!probe.HasExited)
                probe.Kill();
            await probe.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        if (probe.ExitCode != 0)
            return false;
        return stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            line.Split(',', StringSplitOptions.TrimEntries) is [string driverValue, string computeValue] &&
            Version.TryParse(driverValue, out Version? foundDriver) && foundDriver >= minimumDriver &&
            Version.TryParse(computeValue, out Version? foundCompute) && foundCompute >= minimumCompute);
    }

    public static async Task EnsureSteamClientAsync(string? configuredClientDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (SteamRunning())
            return;
        string directory = SteamDiscovery.FindClientDirectory(configuredClientDirectory) ??
            throw new InvalidOperationException("Open the Steam desktop app, then try Play Now again.");
        string executable = InstallFiles.Under(directory, "steam.exe");
        InstallFiles.CheckNoLinks(executable);
        using Process client = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory }) ??
            throw new InvalidOperationException("Windows could not open Steam.");
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            if (SteamRunning(requireWindow: true))
                return;
        }
        throw new InvalidOperationException("Open Steam and finish signing in, then try Play Now again.");
    }

    private static bool SteamRunning(bool requireWindow = false)
    {
        Process[] running = Process.GetProcessesByName("steam");
        try { return requireWindow ? running.Any(process => !process.HasExited && process.MainWindowHandle != IntPtr.Zero) : running.Length != 0; }
        finally { foreach (Process process in running) process.Dispose(); }
    }
}
