using System.Diagnostics;

internal readonly record struct CutsceneUpdate(string Stage, long Done, long Total, string Label)
{
    internal static bool TryRead(string line, out CutsceneUpdate update)
    {
        update = default;
        if (!line.StartsWith("CUTSCENE\t", StringComparison.Ordinal))
            return false;
        string[] fields = line.Split('\t', 5);
        if (fields.Length != 5 || !long.TryParse(fields[2], out long done) || !long.TryParse(fields[3], out long total) ||
            done < 0 || total < 0 || fields[1] is not ("plan" or "separate" or "render"))
            return false;
        update = new CutsceneUpdate(fields[1], done, total, fields[4]);
        return true;
    }
}

internal sealed class CutsceneTiming
{
    private readonly StageTiming separation = new();
    private readonly StageTiming rendering = new();
    private StageTiming tools = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private string device;
    private string stage;
    private long toolTotal;
    internal string Label { get; private set; }
    internal bool HasPlan => device != null;

    internal void Observe(CutsceneUpdate update)
    {
        double now = elapsed.Elapsed.TotalSeconds;
        if (update.Stage == "preparing")
        {
            if (device != update.Label)
                separation.Plan(0, resetRate: true);
            device = update.Label;
            stage = "preparing";
            Label = null;
            tools = new StageTiming();
            toolTotal = 0;
            return;
        }
        if (update.Stage is "tools" or "tool-file" or "tools-progress")
        {
            if (update.Stage == "tools" || toolTotal != update.Total)
            {
                toolTotal = update.Total;
                tools.Plan(toolTotal, resetRate: true);
            }
            if (update.Stage == "tool-file")
                tools.Pause();
            tools.Observe(update.Done, now);
            return;
        }
        if (update.Stage == "plan")
        {
            separation.Plan(update.Done, resetRate: device != update.Label);
            rendering.Plan(update.Total, resetRate: false);
            device = update.Label;
            stage = null;
            Label = null;
            return;
        }
        if (stage != update.Stage)
        {
            if (stage == "separate")
                separation.Pause();
            if (stage == "render")
                rendering.Pause();
        }
        StageTiming active = update.Stage == "separate" ? separation : rendering;
        active.Observe(update.Done, now);
        stage = update.Stage;
        Label = update.Label;
    }

    internal string Status()
    {
        double now = elapsed.Elapsed.TotalSeconds;
        double? audio = separation.Remaining(now, stage == "separate");
        double? video = rendering.Remaining(now, stage == "render");
        string processor = device == "cuda" ? "NVIDIA GPU" : "CPU";
        if (stage == "preparing")
            return $"Cutscenes · Preparing local tools for {processor} audio separation" +
                (toolTotal == 0 ? "" : $" · Tool transfers {tools.Percent}% · {Format(tools.Remaining(now, active: true))}") +
                Environment.NewLine +
                "All selected cutscenes · estimating after tools are ready…";
        return $"1 of 2 · Separate audio on {processor} · {separation.Percent}% · {Format(audio)}" + Environment.NewLine +
            $"2 of 2 · Render and mix cutscenes · {rendering.Percent}% · {Format(video)}" + Environment.NewLine +
            "Total remaining for all selected cutscenes · " + Overall(audio, video);
    }

    internal static string Format(double? seconds) => seconds switch
    {
        null => "estimating…",
        <= 0 => "done",
        <= 60 => "under a minute left",
        _ => FormatDuration(TimeSpan.FromMinutes(Math.Ceiling(seconds.Value / 60)))
    };

    private static string FormatDuration(TimeSpan time) => time.TotalHours < 1
        ? $"{time.Minutes} min left" : $"{(int)time.TotalHours} hr {time.Minutes} min left";

    private static string Overall(double? audio, double? video)
    {
        if (audio.HasValue && video.HasValue)
            return Format(audio + video);
        double measured = audio.GetValueOrDefault() + video.GetValueOrDefault();
        return measured > 0 ? Format(measured) + " for the measured stage · estimating the other stage" : "estimating…";
    }

    internal sealed class StageTiming
    {
        private long total;
        private long done;
        private long initialTotal;
        private double? rate;
        private double? changeTime;
        internal int Percent => initialTotal == 0 ? 100 : (int)Math.Clamp(
            100.0 * (initialTotal - total + done) / initialTotal, 0, 100);

        internal void Plan(long work, bool resetRate)
        {
            total = work;
            initialTotal = Math.Max(initialTotal, work);
            done = 0;
            changeTime = null;
            if (resetRate)
                rate = null;
        }

        internal void Observe(long completed, double now)
        {
            if (changeTime is { } previous && completed > done && now > previous)
            {
                double sample = (now - previous) / (completed - done);
                rate = rate is { } average ? average * .75 + sample * .25 : sample;
            }
            if (changeTime == null || completed > done)
                changeTime = now;
            done = completed;
        }

        internal void Pause() => changeTime = null;

        internal double? Remaining(double now, bool active)
        {
            if (done >= total)
                return 0;
            if (rate is not { } measured)
                return null;
            double waiting = active ? now - changeTime.GetValueOrDefault(now) : 0;
            double estimate = (total - done) * measured - waiting;
            return estimate > 0 ? estimate : null;
        }
    }
}
