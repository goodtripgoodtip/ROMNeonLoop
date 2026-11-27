using System.Diagnostics;

namespace GameInstaller.WinForms;

internal sealed class DownloadPrompt : UserControl
{
    private readonly DownloadQuestion question;
    private readonly string? manualFileDirectory;
    private readonly Action<DownloadDecision> completed;
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(700, 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 10) };
    private readonly Button openLink = MakeButton("Open download link");
    private Control layoutPage;

    internal DownloadPrompt(DownloadQuestion question, string? manualFileDirectory, Action<DownloadDecision> completed)
    {
        this.question = question;
        this.manualFileDirectory = manualFileDirectory;
        this.completed = completed;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty
        };
        layoutPage = layout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var file = new Label
        {
            Text = $"{question.Asset.Id} · {question.Asset.Bytes / 1048576d:0.#} MiB", AutoSize = true,
            UseMnemonic = false, MaximumSize = new Size(700, 0)
        };
        Add(layout, file);
        message.Text = GetMessage();
        Add(layout, message);
        var more = new LinkLabel { Text = "Show file details", AutoSize = true, Margin = new Padding(0, 3, 0, 6) };
        InstallerTheme.Apply(more);
        var facts = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Visible = false };
        Add(layout, more);
        Add(layout, facts);
        Add(facts, ReadOnlyField(question.Asset.FileName, "File name"));
        if (question.Asset.Url is not null)
        {
            Add(facts, ReadOnlyField(question.Asset.Url.AbsoluteUri, "Download address"));
            if (question.CanOpenDownloadLink)
                Add(facts, openLink);
        }
        Add(facts, new Label { Text = "Expected checksum (SHA-256)", AutoSize = true });
        Add(facts, ReadOnlyField(question.Asset.Sha256, "SHA-256 checksum"));
        more.LinkClicked += (_, _) =>
        {
            facts.Visible = !facts.Visible;
            more.Text = facts.Visible ? "Hide file details" : "Show file details";
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 9, 0, 0) };
        var auto = MakeButton("Download");
        var choose = MakeButton("Choose file…");
        var folder = MakeButton("Choose folder…");
        var skip = MakeButton("Skip cutscene setup");
        var stop = MakeButton("Stop setup");
        foreach (Button secondary in new[] { auto, choose, folder, skip, stop, openLink })
            InstallerTheme.Secondary(secondary);
        InstallerTheme.Primary(question.CanDownload ? auto : choose);
        auto.Visible = question.CanDownload;
        skip.Visible = question.CanSkip;
        auto.Click += (_, _) => Finish(new DownloadDecision(DownloadChoice.Download));
        openLink.Click += (_, _) => OpenLink();
        choose.Click += (_, _) => ChooseLocalFile();
        folder.Click += (_, _) => ChooseLocalFolder();
        skip.Click += (_, _) => Finish(new DownloadDecision(DownloadChoice.Skip));
        stop.Click += (_, _) => Finish(new DownloadDecision(DownloadChoice.Cancel));
        buttons.Controls.AddRange([auto, choose, folder]);
        Add(layout, buttons);
        var exit = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 12, 0, 0) };
        exit.Controls.AddRange([skip, stop]);
        Add(layout, exit);
        Controls.Add(layout);
        ClientSizeChanged += (_, _) =>
        {
            file.MaximumSize = message.MaximumSize = new Size(Math.Max(260, ClientSize.Width - 18), 0);
        };
    }

    private string GetMessage()
    {
        string instructions = question.CanDownload ?
                $"Download the selected file or choose a local copy. Attempt {question.Attempt} of {question.MaxAttempts}." :
                "Choose a local copy. If needed, open the download link in your browser.";
        return question.LastError is null ? instructions : question.LastError + Environment.NewLine + instructions;
    }

    private void OpenLink()
    {
        try
        {
            if (question.CanOpenDownloadLink && question.Asset.Url is not null)
                using (Process? browser = Process.Start(new ProcessStartInfo(question.Asset.Url.AbsoluteUri) { UseShellExecute = true })) { }
        }
        catch (Exception error) { message.Text = error.Message; }
    }

    private void ChooseLocalFile()
    {
        ChooseLocal(folders: false);
    }

    private void ChooseLocalFolder()
    {
        ChooseLocal(folders: true);
    }

    private void ChooseLocal(bool folders)
    {
        layoutPage.Hide();
        InstallerPathPicker? picker = null;
        picker = new InstallerPathPicker(manualFileDirectory, folders,
            path => Finish(new DownloadDecision(folders ? DownloadChoice.UseLocalFolder : DownloadChoice.UseLocalFile, path)),
            () => { picker?.Dispose(); layoutPage.Show(); });
        Controls.Add(picker);
    }

    private void Finish(DownloadDecision decision) => completed(decision);

    private static void Add(TableLayoutPanel layout, Control element)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(element, 0, row);
    }

    private static TextBox ReadOnlyField(string value, string name)
    {
        var field = new TextBox { Text = value, AccessibleName = name, ReadOnly = true, Dock = DockStyle.Fill };
        InstallerTheme.Apply(field);
        return field;
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(104, 42),
        Padding = new Padding(10, 4, 10, 4) };
}
