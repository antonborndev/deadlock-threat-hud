using System.Drawing;
using System.Windows.Forms;

internal sealed class DesktopBridgeApplicationContext :
    ApplicationContext
{
    private readonly MainForm _mainForm;

    private readonly BridgeWorkerSupervisor
        _supervisor;

    private Task? _supervisorTask;

    private int _shutdownStarted;
    private bool _disposed;

    public DesktopBridgeApplicationContext(
        string[] args
    )
    {
        _mainForm =
            new MainForm();

        /*
         * ApplicationIcon embeds the icon into the EXE,
         * but WinForms Form.Icon is a separate property.
         * Explicitly copy the icon from the running EXE
         * so the window title bar and taskbar use it too.
         */
        using (
            var applicationIcon =
                Icon.ExtractAssociatedIcon(
                    Application.ExecutablePath
                )
        )
        {
            if (applicationIcon is not null)
            {
                _mainForm.Icon =
                    (Icon)applicationIcon.Clone();
            }
        }

        _supervisor =
            new BridgeWorkerSupervisor(
                args,
                _mainForm
            );

        MainForm =
            _mainForm;

        _mainForm.Shown +=
            OnMainFormShown;

        _mainForm.ShutdownRequested +=
            OnShutdownRequested;

        _mainForm.FormClosed +=
            OnMainFormClosed;

        _mainForm.Show();
    }

    private void OnMainFormShown(
        object? sender,
        EventArgs e
    )
    {
        if (_supervisorTask is not null)
        {
            return;
        }

        _supervisorTask =
            RunSupervisorAsync();
    }

    private async Task RunSupervisorAsync()
    {
        try
        {
            await _supervisor.RunAsync();
        }
        catch (Exception error)
        {
            _mainForm.SetRuntimeState(
                BridgeRuntimeState.Error,
                error.Message
            );

            _mainForm.AppendLog(
                "Unhandled supervisor error:" +
                Environment.NewLine +
                error
            );
        }
    }

    private async void OnShutdownRequested(
        object? sender,
        EventArgs e
    )
    {
        if (
            Interlocked.Exchange(
                ref _shutdownStarted,
                1
            ) != 0
        )
        {
            return;
        }

        _supervisor.RequestStop();

        try
        {
            if (_supervisorTask is not null)
            {
                await _supervisorTask;
            }
        }
        catch (Exception error)
        {
            _mainForm.AppendLog(
                "Supervisor shutdown error:" +
                Environment.NewLine +
                error
            );
        }

        _mainForm.CloseAfterShutdown();
    }

    private void OnMainFormClosed(
        object? sender,
        FormClosedEventArgs e
    )
    {
        ExitThread();
    }

    protected override void Dispose(
        bool disposing
    )
    {
        if (
            disposing &&
            !_disposed
        )
        {
            _disposed =
                true;

            _mainForm.Shown -=
                OnMainFormShown;

            _mainForm.ShutdownRequested -=
                OnShutdownRequested;

            _mainForm.FormClosed -=
                OnMainFormClosed;

            _supervisor.Dispose();
            _mainForm.Dispose();
        }

        base.Dispose(
            disposing
        );
    }
}