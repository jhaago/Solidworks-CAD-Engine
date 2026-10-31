using System;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadEngine.Adapter
{
    public sealed class EngineOptions
    {
        public bool Enabled { get; set; }
        public string FileName { get; set; }
        // Trusted local configuration, never model/user supplied shell text. UseShellExecute is always false.
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; }
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    }

    public sealed class McpRpcException : Exception
    {
        public JObject Error { get; }
        public McpRpcException(JObject error) : base(error.ToString()) { Error = (JObject)error.DeepClone(); }
    }

    public sealed class UncertainOutcomeException : Exception
    {
        public UncertainOutcomeException(string message, Exception inner = null) : base(message, inner) { }
    }
}
