using System;
using System.Diagnostics;
using System.IO;

namespace SolidWorksCadEngine.Adapter
{
    /// <summary>Local process supervision boundary; termination must be confirmed before releasing CAD ownership.</summary>
    public interface IEngineWorker : IDisposable
    {
        TextReader Output { get; }
        TextReader Error { get; }
        TextWriter Input { get; }
        void RequestStop();
        bool WaitForExit(int milliseconds);
    }

    public interface IEngineWorkerFactory
    {
        IEngineWorker Start(ProcessStartInfo startInfo);
    }

    internal sealed class LocalEngineWorkerFactory : IEngineWorkerFactory
    {
        public IEngineWorker Start(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo };
            try { process.Start(); return new LocalEngineWorker(process); }
            catch { process.Dispose(); throw; }
        }
    }

    internal sealed class LocalEngineWorker : IEngineWorker
    {
        private readonly Process _process;
        public LocalEngineWorker(Process process) { _process = process; }
        public TextReader Output => _process.StandardOutput;
        public TextReader Error => _process.StandardError;
        public TextWriter Input => _process.StandardInput;
        public void RequestStop() { if (!_process.HasExited) _process.Kill(); }
        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
        public void Dispose() { _process.Dispose(); }
    }
}
