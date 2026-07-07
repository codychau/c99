using System;

namespace C99.Models
{
    public class UsageMetrics
    {
        public int TotalReports { get; set; }
        public int TotalAICalls { get; set; }
        public int TotalPipelineSteps { get; set; }
        public long TotalPromptTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public double TotalAIDurationMs { get; set; }
        public double TotalApiCost { get; set; }
        public double TotalLocalTokens { get; set; }
        public double TotalEngineRunSeconds { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        public DateTime FirstRecord { get; set; } = DateTime.Now;
    }
}
