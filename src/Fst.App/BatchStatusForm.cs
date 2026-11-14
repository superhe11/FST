namespace Fst.App;

internal sealed class BatchStatusForm : FstWindow
{
    private readonly TextBox message;
    private readonly Button action;
    private string? jobId;
    private readonly Func<string, bool> cancel;

    public BatchStatusForm(Func<string, bool> cancel) : base("FST", new Size(660, 330))
    {
        this.cancel = cancel;
        message = new TextBox
        {
            Bounds = new Rectangle(20, 60, 620, 200),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Ink,
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiTheme.Font(10),
            TabStop = true
        };
        action = UiTheme.Button("Отменить бандл", new Rectangle(20, 275, 620, 35));
        action.Click += (_, _) =>
        {
            if (jobId == null)
                Close();
            else if (cancel(jobId))
            {
                action.Enabled = false;
                action.Text = L.T("Отмена…");
            }
        };
        Controls.Add(message);
        Controls.Add(action);
        FormClosing += (_, e) =>
        {
            if (jobId != null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }
    public void ShowProgress(BatchStatus status)
    {
        jobId = status.JobId;
        message.Text = status.Job.Message;
        action.Text = L.T(status.Job.CancellationRequested ? "Отмена…" : "Отменить бандл");
        action.Enabled = status.Job.CanCancel;
    }
    public void ShowResult(string text)
    {
        jobId = null;
        message.Text = text.Replace("\n", Environment.NewLine);
        action.Text = "OK";
        action.Enabled = true;
        Show();
        Activate();
    }
}
