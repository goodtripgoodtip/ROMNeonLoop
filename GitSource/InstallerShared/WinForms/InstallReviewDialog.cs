using System.Text;

namespace GameInstaller.WinForms;

internal sealed class InstallReviewDialog : Form
{
    internal InstallReviewDialog(InstallReview review, bool easy, IReadOnlyList<string> extras)
        : this(easy ? "Ready to install" : "Review your choices",
            extras.Count == 0 ? "Base installation only." : easy
                ? $"Includes {extras.Count} recommended {(extras.Count == 1 ? "extra" : "extras")}."
                : "Selected extras: " + string.Join(", ", extras) + ".",
            NetworkSummary(review), () => BuildText(review), review.Notice)
    {
    }

    internal InstallReviewDialog(string title, string selection, string networkSummary, Func<string> readDetails, InstallerNotice? notice, bool playerMode = false)
    {
        Text = title;
        Font = new Font("Segoe UI", playerMode ? 13 : 11);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        int compactHeight = playerMode ? 420 : notice is null ? 380 : 445;
        ClientSize = new Size(playerMode ? 920 : 700, compactHeight);
        MinimumSize = new Size(575, playerMode ? 380 : 350);
        if (playerMode)
            InstallerWindow.FitWorkArea(this, 0.47, 0.32);
        InstallerTheme.Apply(this);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(28) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = !playerMode };
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        var heading = LabelFor(title);
        heading.Font = new Font(Font.FontFamily, playerMode ? 28 : 21, FontStyle.Bold);
        heading.ForeColor = InstallerTheme.Heading(null);
        layout.Controls.Add(heading, 0, 0);
        var choices = LabelFor(selection);
        var network = LabelFor(networkSummary);
        Add(body, choices);
        if (!string.IsNullOrWhiteSpace(networkSummary))
            Add(body, network);
        string showDetails = playerMode ? "Show setup details" : "Show technical details";
        string hideDetails = playerMode ? "Hide setup details" : "Hide technical details";
        var details = new LinkLabel { Text = showDetails, AutoSize = true, Margin = new Padding(0, 3, 0, 10) };
        InstallerTheme.Apply(details);
        Add(body, details);
        var drawer = new TextBox
        {
            Dock = DockStyle.Top, Height = 235, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
            AccessibleName = "Install actions, downloads, and permissions", Margin = new Padding(0, 0, 0, 10)
        };
        InstallerTheme.Apply(drawer);
        Add(body, drawer);
        details.LinkClicked += (_, _) =>
        {
            bool expand = !drawer.Visible;
            drawer.Text = expand ? readDetails().ReplaceLineEndings(Environment.NewLine) : "";
            drawer.Visible = expand;
            details.Text = expand ? hideDetails : showDetails;
            if (!playerMode)
            {
                int height = expand ? Math.Max(compactHeight, Math.Min(compactHeight + 225, Screen.FromControl(this).WorkingArea.Height - 90)) : compactHeight;
                ClientSize = new Size(ClientSize.Width, height);
            }
        };
        scroll.Controls.Add(body);
        layout.Controls.Add(scroll, 0, 1);

        var accept = new Button { Text = "Install", AutoSize = true, MinimumSize = new Size(112, 44), DialogResult = DialogResult.OK };
        var back = new Button { Text = playerMode || notice is null ? "Back" : "Decline", AutoSize = true, MinimumSize = new Size(112, 44), DialogResult = DialogResult.Cancel };
        if (notice is not null)
        {
            accept.Enabled = false;
            var terms = new LinkLabel { Text = playerMode ? "Read the terms" : $"Read {notice.Title}", AutoSize = true, Margin = new Padding(0, 0, 0, 7) };
            InstallerTheme.Apply(terms);
            terms.LinkClicked += (_, _) =>
            {
                using var reader = new InstallNoticeDialog(notice);
                reader.ShowDialog(this);
            };
            var agree = new CheckBox { Text = playerMode ? "I agree to the terms" : $"I agree to the {notice.Title}.", AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 10) };
            InstallerTheme.Apply(agree);
            agree.CheckedChanged += (_, _) =>
            {
                accept.Enabled = agree.Checked;
                AcceptedNoticeVersion = agree.Checked ? notice.Version : null;
                AcceptButton = agree.Checked ? accept : null;
            };
            var consent = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 9, 0, 0) };
            Add(consent, terms);
            Add(consent, agree);
            layout.Controls.Add(consent, 0, 2);
        }
        else
            AcceptButton = accept;
        InstallerTheme.Primary(accept);
        InstallerTheme.Secondary(back);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        buttons.Controls.AddRange([accept, back]);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = back;
        ActiveControl = back;
        void FitLabels()
        {
            choices.MaximumSize = network.MaximumSize = new Size(Math.Max(260, ClientSize.Width - 92), 0);
            if (playerMode)
                drawer.Height = Math.Max(100, scroll.ClientSize.Height - (body.PreferredSize.Height - (drawer.Visible ? drawer.Height : 0)) - 18);
        }
        scroll.SizeChanged += (_, _) => FitLabels();
        Resize += (_, _) => FitLabels();
        if (playerMode)
            InstallerWindow.ScaleText(this, new Size(920, 420), heading, layout: _ => FitLabels());
        FitLabels();
    }

    internal string? AcceptedNoticeVersion { get; private set; }

    private static Label LabelFor(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 9) };

    private static void Add(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private static string NetworkSummary(InstallReview review)
    {
        int online = review.Assets.Count(asset => asset.Url is not null);
        return review.DownloadPolicy switch
        {
            InstallDownloadPolicy.LocalOnly => "Setup uses local files and asks where to find any that are missing.",
            _ when online == 0 && review.Assets.Count > 0 => "Setup checks for local files and asks where to find any that are missing.",
            _ when online == 0 => "This installation needs no downloads.",
            InstallDownloadPolicy.ApproveReviewed => $"Setup may download up to {online} listed {(online == 1 ? "file" : "files")}, with up to five attempts per file. Open technical details to see the list.",
            _ => "Setup asks before each download and can use matching local files."
        };
    }

    private static string BuildText(InstallReview review)
    {
        var text = new StringBuilder();
        text.AppendLine($"Destination: {review.ProductDirectory}");
        if (review.Source is { } source)
            text.AppendLine($"Read-only game source: {source.Directory}" + (source.BuildId is null ? "" : $" (build {source.BuildId})"));
        if (review.FreeBytes is long free)
            text.AppendLine($"Free space on the destination volume: {free:N0} bytes");
        text.AppendLine();
        for (int index = 0; index < review.Actions.Count; index++)
        {
            InstallReviewAction action = review.Actions[index];
            text.AppendLine($"{index + 1}. {action.Name}");
            text.AppendLine(action.Description);
            text.AppendLine();
        }

        text.AppendLine(review.DownloadPolicy switch
        {
            InstallDownloadPolicy.ApproveReviewed when review.Assets.Any(asset => asset.Url is not null) =>
                "Install approves the listed online files and up to five attempts per file. Setup checks local candidates first and offers manual selection if needed.",
            InstallDownloadPolicy.ApproveReviewed => "Setup checks the listed local inputs. No online files are listed.",
            InstallDownloadPolicy.LocalOnly => "Use local files only. Setup verifies cache entries, supplied files, and files you select.",
            _ => "Setup asks before each network transfer or retry. You can supply the matching file manually."
        });
        if (!string.IsNullOrWhiteSpace(review.NetworkDisclosure))
            text.AppendLine(review.NetworkDisclosure);
        if (review.Assets.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Files setup may acquire");
            foreach (InstallReviewAsset item in review.Assets)
            {
                text.AppendLine($"{item.FileName} ({item.Bytes:N0} bytes) · SHA-256 {item.Sha256}");
                if (item.Url is not null && review.DownloadPolicy != InstallDownloadPolicy.LocalOnly)
                    text.AppendLine($"{item.Url.AbsoluteUri} · hosts: {string.Join(", ", item.Hosts)}");
                foreach (string path in item.LocalFiles)
                    text.AppendLine($"Check local candidate: {path}");
                if (item.HasCachedCandidate)
                    text.AppendLine("A cache candidate is present and will be verified before use.");
                text.AppendLine();
            }
        }
        return text.ToString();
    }
}
