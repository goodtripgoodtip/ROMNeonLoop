using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class InstallHardware
{
    internal const string Cpu = "cpu";
    internal const string Gpu = "cu128";
    private static readonly string[] Supported = { "7.5", "8.0", "8.6", "8.9", "9.0", "10.0", "12.0" };

    internal static string Choose(bool consent, InstallProgress progress, CancellationToken token)
    {
        if (!consent)
        {
            progress("Hardware detection skipped. Cutscene preparation will use CPU.", 0, 0);
            return Cpu;
        }
        progress("Checking only NVIDIA driver version and GPU compute capability. No data is sent.", 0, 0);
        string driver = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(driver))
            driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        try
        {
            if (!File.Exists(driver))
                throw new FileNotFoundException("The NVIDIA driver utility is unavailable.");
            var request = new ProcessStartInfo(driver, "--query-gpu=driver_version,compute_cap --format=csv,noheader,nounits") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process probe = Process.Start(request))
            {
                if (probe == null) throw new InvalidOperationException("The NVIDIA query could not start.");
                using (token.Register(() => Stop(probe)))
                {
                    var output = probe.StandardOutput.ReadToEndAsync();
                    var errors = probe.StandardError.ReadToEndAsync();
                    if (!probe.WaitForExit(5000))
                    {
                        Stop(probe);
                        throw new TimeoutException("The NVIDIA query did not respond in five seconds.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (probe.ExitCode != 0)
                        throw new InvalidOperationException("The NVIDIA driver did not return compatibility information.");
                    errors.GetAwaiter().GetResult();
                    foreach (string row in output.GetAwaiter().GetResult().Split('\n'))
                    {
                        string[] fields = row.Split(',');
                        Version version;
                        if (fields.Length == 2 && Version.TryParse(fields[0].Trim(), out version) &&
                            version >= new Version(571, 96) && Array.IndexOf(Supported, fields[1].Trim()) >= 0)
                        {
                            progress("CUDA 12.8 is eligible. Setup will verify it can run before separating audio.", 0, 0);
                            return Gpu;
                        }
                    }
                }
            }
            progress("A compatible NVIDIA driver and GPU could not be confirmed. Using CPU.", 0, 0);
        }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            progress(error.GetBaseException().Message + " Using CPU.", 0, 0);
        }
        return Cpu;
    }

    private static void Stop(Process probe)
    {
        try { if (!probe.HasExited) probe.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
