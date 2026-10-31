using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadEngine.Adapter
{
    /// <summary>One job owns this lease, not one tool call. Shared path/semantics with the legacy application.</summary>
    public sealed class FileCadExecutionBoundary : IDisposable
    {
        private readonly FileStream _lease;
        private readonly string _jobId;
        private readonly string _journalPath;
        public string RecoveryPath { get; }
        public bool IsQuarantined { get; private set; }
        public static string DefaultLeasePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolidWorksCadAgent", "cad-native-control.lock");

        public FileCadExecutionBoundary(string leasePath, string jobId, string journalPath)
        {
            if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
            leasePath = Path.GetFullPath(leasePath);
            RecoveryPath = leasePath + ".recovery-required";
            _jobId = jobId;
            _journalPath = Path.GetFullPath(journalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(leasePath));
            _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Check after acquiring the lock: otherwise a finishing worker could create the marker after this check.
            if (File.Exists(RecoveryPath))
            {
                _lease.Dispose();
                throw new InvalidOperationException("CAD recovery is required. Reconcile the native outcome before clearing " + RecoveryPath);
            }
        }

        internal void BeforeCall(string id, string name, JObject arguments)
        {
            if (IsQuarantined) throw new UncertainOutcomeException("This job requires native reconciliation; replay is forbidden.");
            // Durable intent first. Even a host crash between pipe write and response must block both backends.
            WriteDurable(RecoveryPath, new JObject { ["jobId"] = _jobId, ["requestId"] = id,
                ["tool"] = name, ["state"] = "in-flight", ["utc"] = DateTime.UtcNow.ToString("O") }.ToString(Formatting.None));
            Record("dispatch", new JObject { ["requestId"] = id, ["tool"] = name, ["arguments"] = arguments.DeepClone() });
        }

        internal void CompleteCall(JObject response)
        {
            Record("response", response);
            File.Delete(RecoveryPath);
        }

        internal void Quarantine(string reason, JObject response = null)
        {
            IsQuarantined = true;
            // Preserve original dispatch identity. Also block other backends if an idle worker cannot be stopped.
            if (!File.Exists(RecoveryPath))
                WriteDurable(RecoveryPath, new JObject { ["jobId"] = _jobId, ["state"] = "uncertain", ["reason"] = reason }.ToString(Formatting.None));
            try { Record("uncertain", new JObject { ["reason"] = reason, ["response"] = response?.DeepClone() }); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Record(string kind, JObject data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
            var entry = new JObject { ["event"] = kind, ["jobId"] = _jobId,
                ["utc"] = DateTime.UtcNow.ToString("O"), ["data"] = data.DeepClone() };
            var bytes = new UTF8Encoding(false).GetBytes(entry.ToString(Formatting.None) + "\n");
            using (var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        private static void WriteDurable(string path, string value)
        {
            var bytes = new UTF8Encoding(false).GetBytes(value);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        public void Dispose() { _lease.Dispose(); }
    }
}
