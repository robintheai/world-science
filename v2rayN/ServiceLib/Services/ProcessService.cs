namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private bool _isDisposed;
    private readonly bool _monitorOnly;

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;

    public ProcessService(
        string fileName,
        string arguments,
        string workingDirectory,
        bool displayLog,
        bool redirectInput,
        Dictionary<string, string>? environmentVars,
        Func<bool, string, Task>? updateFunc)
    {
        _updateFunc = updateFunc;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = displayLog,
                RedirectStandardError = displayLog,
                CreateNoWindow = true,
                StandardOutputEncoding = displayLog ? Encoding.UTF8 : null,
                StandardErrorEncoding = displayLog ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };

        if (environmentVars != null)
        {
            foreach (var kv in environmentVars)
            {
                _process.StartInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (displayLog)
        {
            RegisterEventHandlers();
        }
    }

    // A macOS core launched by the OS authorization dialog is monitored without handling a password.
    public ProcessService(int processId, Func<bool, string, Task>? updateFunc, bool monitorOnly = false)
    {
        _monitorOnly = monitorOnly;
        _process = Process.GetProcessById(processId);
        _updateFunc = updateFunc;
    }

    public async Task StartAsync(string pwd = null)
    {
        _process.Start();

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        if (_process.StartInfo.RedirectStandardInput)
        {
            await Task.Delay(10);
            await _process.StandardInput.WriteLineAsync(pwd);
        }
    }

    public async Task StopAsync()
    {
        if (_monitorOnly || _process.HasExited)
        {
            return;
        }

        try
        {
            if (_process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }
            }

            try
            {
                if (Utils.IsNonWindows())
                {
                    await Task.Run(() => _process.Kill(true));
                }
            }
            catch { }

            try
            {
                _process.Kill();
            }
            catch { }

            if (Utils.IsWindows())
            {
                // Kill initiates termination; a TUN engine can still own its listener
                // while Windows tears down the adapter. Do not report it stopped yet.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(shutdown.Token);
            }
            else
                await Task.Delay(100);
        }
        catch (Exception ex)
        {
            await _updateFunc?.Invoke(true, ex.Message);
        }
    }

    private void RegisterEventHandlers()
    {
        void dataHandler(object sender, DataReceivedEventArgs e)
        {
            if (e.Data.IsNotEmpty())
            {
                _ = _updateFunc?.Invoke(false, e.Data + Environment.NewLine);
            }
        }

        _process.OutputDataReceived += dataHandler;
        _process.ErrorDataReceived += dataHandler;

        _process.Exited += (s, e) =>
        {
            try
            {
                _process.OutputDataReceived -= dataHandler;
                _process.ErrorDataReceived -= dataHandler;
            }
            catch
            {
            }
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (!_monitorOnly && !_process.HasExited)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }

                _process.Kill();
            }

            _process.Dispose();
        }
        catch (Exception ex)
        {
            _updateFunc?.Invoke(true, ex.Message);
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
