using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

internal static class CutsceneSetup
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    internal static void Run(InstallSession session, string library, string backend, SetupAssets assets,
        InstallProgress progress, CancellationToken token)
    {
        var files = session.ToolsFor(backend).ToDictionary(asset => asset.label, asset => assets.Get(asset, token));
        string tools = session.Tools;
        string scripts = Path.Combine(session.Effects, "CutsceneSetup");
        string uv = Expand(files["uv setup tool"], Path.Combine(tools, "Uv"), "uv.exe", progress, token);
        string standalone = Expand(files["Python 3.12.13"], Path.Combine(tools, "RuntimePython"), "python/python.exe", progress, token);
        string ffmpeg = Expand(files["FFmpeg video tools"], Path.Combine(tools, "FFmpeg"),
            "ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", progress, token);
        string bandit = Expand(Path.Combine(scripts, "bandit-infer.tar.gz"), Path.Combine(tools, "BanditSource"),
            "bandit-infer-main/src/bandit_infer/__init__.py", progress, token);
        string environment = Path.Combine(tools, "Environment-v2-" + backend);
        string python = Path.Combine(environment, "Scripts", "python.exe");
        string ready = Path.Combine(environment, ".ready");
        string nativeRuntime = Path.Combine(scripts, "NativeRuntime");
        string device = backend == InstallHardware.Gpu ? "cuda" : "cpu";
        Directory.CreateDirectory(tools);
        var variables = new Dictionary<string, string>
        {
            ["UV_CACHE_DIR"] = Path.Combine(tools, "PackageCache"),
            ["UV_NO_CONFIG"] = "1", ["UV_NO_PROGRESS"] = "1", ["UV_OFFLINE"] = "1", ["UV_NO_PYTHON_DOWNLOADS"] = "1",
            ["PYTHONNOUSERSITE"] = "1", ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONUTF8"] = "1",
            ["PYTHONHOME"] = null, ["PYTHONPATH"] = Path.GetDirectoryName(Path.GetDirectoryName(bandit)!),
            ["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(ffmpeg), nativeRuntime,
                Environment.GetEnvironmentVariable("PATH"))
        };

        void Start(string command, params string[] arguments) => RunTool(command, arguments, tools, variables, progress, token);
        if (!File.Exists(python) || !File.Exists(ready) || File.ReadAllText(ready).Trim() != backend)
        {
            if (Directory.Exists(environment))
            {
                string partial = environment + ".installing";
                InstallFiles.ClearOwnedFolder(partial);
                Directory.Move(environment, partial);
                InstallFiles.ClearOwnedFolder(partial);
            }
            progress("Preparing the private " + backend + " audio environment", 0, 0);
            Start(uv, "venv", "--offline", "--no-python-downloads", "--python", standalone, environment);
            string[] wheels = session.ToolsFor(backend).Where(asset => asset.group is "wheel" || asset.group == backend)
                .Select(asset => files[asset.label]).ToArray();
            Start(uv, ["pip", "install", "--offline", "--no-index", "--no-deps", "--python", python, .. wheels]);
            Start(uv, "pip", "check", "--offline", "--python", python);
            StageAudioRuntime(nativeRuntime, environment, token);
            Start(python, "-c", "import torch, numpy, soundfile");
            InstallFiles.WriteText(ready, backend);
        }
        else
            StageAudioRuntime(nativeRuntime, environment, token);
        if (device == "cuda")
            Start(python, "-c", "import torch; assert torch.cuda.is_available(); torch.zeros(1, device='cuda').sum().item(); torch.cuda.synchronize()");
        try
        {
            Start(python, "-u", Path.Combine(scripts, "GenerateCutscenes.py"), session.Steam.Installed,
                session.Pack.Root, session.Cutscenes, library, files["Bandit cutscene model"], "--device", device);
        }
        finally { session.RemoveMoviePartials(library, backend); }
    }

    private static void StageAudioRuntime(string source, string environment, CancellationToken token)
    {
        string libraries = Path.Combine(environment, "Lib", "site-packages", "torch", "lib");
        if (!Directory.Exists(source) || !Directory.Exists(libraries))
            throw new IOException("The private audio runtime is missing. Install from a complete NeonLoop package.");
        foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles("*.dll"))
            InstallFiles.Copy(file, Path.Combine(libraries, file.Name), null, token);
    }

    private static string Expand(string archive, string folder, string required, InstallProgress progress, CancellationToken token)
    {
        string stamp = Path.Combine(folder, ".archive");
        string signature = GameInstaller.InstallFiles.HashAsync(archive, token).GetAwaiter().GetResult();
        string output = Path.Combine(folder, required);
        if (File.Exists(output) && File.Exists(stamp) && File.ReadAllText(stamp) == signature)
            return output;
        string staging = folder + ".installing";
        InstallFiles.ClearOwnedFolder(staging);
        Directory.CreateDirectory(staging);
        try
        {
            progress("Extracting " + Path.GetFileName(archive), 0, 0);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                GameInstaller.InstallFiles.ExtractAsync(archive, staging, token).GetAwaiter().GetResult();
            else
            {
                using var file = File.OpenRead(archive);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false, token).GetAwaiter().GetResult();
            }
            if (!File.Exists(Path.Combine(staging, required)))
                throw new InvalidDataException("The verified archive is missing " + required + ".");
            InstallFiles.WriteText(Path.Combine(staging, ".archive"), signature);
            InstallFiles.PublishFolder(staging, folder);
            return output;
        }
        finally { InstallFiles.ClearOwnedFolder(staging); }
    }

    private static void RunTool(string executable, string[] arguments, string directory,
        Dictionary<string, string> variables, InstallProgress progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var variable in variables)
            start.Environment[variable.Key] = variable.Value;
        using var process = Process.Start(start) ?? throw new IOException("A private cutscene tool couldn’t start.");
        using var stop = token.Register(() => Stop(process));
        using var idle = new CancellationTokenSource(IdleLimit);
        using var timeout = idle.Token.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                idle.CancelAfter(IdleLimit);
                token.ThrowIfCancellationRequested();
                string[] fields = line.Split('\t', 4);
                if (fields.Length == 4 && fields[0] == "PROGRESS" &&
                    long.TryParse(fields[1], out long done) && long.TryParse(fields[2], out long total))
                    progress(fields[3], done, total);
                else if (line.Length != 0)
                    progress(line, -1, -1);
            }
            process.WaitForExit();
            token.ThrowIfCancellationRequested();
            if (idle.IsCancellationRequested)
                throw new TimeoutException("Cutscene preparation stopped responding. Completed files were kept.");
            string error = errors.GetAwaiter().GetResult().Trim();
            if (error.Length > 0)
                progress(error, -1, -1);
            if (process.ExitCode != 0)
                throw new IOException(Path.GetFileName(executable) + " exited with code " + process.ExitCode +
                    (error.Length == 0 ? "." : ". " + error.Split('\n').Last().Trim()));
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
