using C99.Models;
using System;
using System.IO;
using System.Text.Json;

namespace C99.Services
{
    public class MetricsService
    {
        private UsageMetrics _metrics = new();
        private readonly string _filePath;
        private readonly object _lock = new();

        public MetricsService()
        {
            string baseDir;
            string testPath = Path.Combine(AppContext.BaseDirectory, ".write_test");
            try
            {
                File.WriteAllText(testPath, "");
                File.Delete(testPath);
                baseDir = AppContext.BaseDirectory;
            }
            catch
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "C99");
                Directory.CreateDirectory(baseDir);
            }
            _filePath = Path.Combine(baseDir, "ai_metrics.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var m = JsonSerializer.Deserialize<UsageMetrics>(json);
                    if (m != null) _metrics = m;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                lock (_lock)
                {
                    _metrics.LastUpdated = DateTime.Now;
                    var json = JsonSerializer.Serialize(_metrics, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    File.WriteAllText(_filePath, json);
                }
            }
            catch { }
        }

        public void RecordAICall(int promptTokens, int completionTokens, double durationMs, double cost, bool isLocal)
        {
            lock (_lock)
            {
                _metrics.TotalAICalls++;
                _metrics.TotalPromptTokens += promptTokens;
                _metrics.TotalCompletionTokens += completionTokens;
                _metrics.TotalAIDurationMs += durationMs;
                if (isLocal)
                    _metrics.TotalLocalTokens += promptTokens + completionTokens;
                else
                    _metrics.TotalApiCost += cost;
            }
            Save();
        }

        public void RecordReport()
        {
            lock (_lock) { _metrics.TotalReports++; }
            Save();
        }

        public void RecordPipelineSteps(int count)
        {
            lock (_lock) { _metrics.TotalPipelineSteps += count; }
            Save();
        }

        public void AddEngineRunTime(double seconds)
        {
            lock (_lock) { _metrics.TotalEngineRunSeconds += seconds; }
            Save();
        }

        public UsageMetrics GetCurrent()
        {
            lock (_lock) { return new UsageMetrics
            {
                TotalReports = _metrics.TotalReports,
                TotalAICalls = _metrics.TotalAICalls,
                TotalPipelineSteps = _metrics.TotalPipelineSteps,
                TotalPromptTokens = _metrics.TotalPromptTokens,
                TotalCompletionTokens = _metrics.TotalCompletionTokens,
                TotalAIDurationMs = _metrics.TotalAIDurationMs,
                TotalApiCost = _metrics.TotalApiCost,
                TotalLocalTokens = _metrics.TotalLocalTokens,
                TotalEngineRunSeconds = _metrics.TotalEngineRunSeconds,
                LastUpdated = _metrics.LastUpdated,
                FirstRecord = _metrics.FirstRecord,
            }; }
        }

        public void Reset()
        {
            lock (_lock) { _metrics = new UsageMetrics(); }
            Save();
        }
    }
}
