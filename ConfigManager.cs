using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C99
{
    /// <summary>
    /// 应用配置模型
    /// </summary>
    public class AppConfig
    {
        /// <summary>大语言模型搜索路径（模型文件所在根目录，共享给所有引擎）</summary>
        public string LLMSearchPath { get; set; } = string.Empty;

        /// <summary>选中的模型子目录</summary>
        public string SelectedModelSubDir { get; set; } = string.Empty;

        /// <summary>选中的模型子目录全路径</summary>
        public string SelectedModelSubDirFullPath { get; set; } = string.Empty;

        /// <summary>各引擎的启动器工作目录（key为引擎名）</summary>
        public Dictionary<string, string> EngineLauncherDirs { get; set; } = new();

        /// <summary>各引擎的参数快照（key=控件x:Name, value=当前值）</summary>
        public Dictionary<string, string> EngineParams { get; set; } = new();
    }

    /// <summary>
    /// 配置文件管理器（JSON 格式，存储在应用程序目录）
    /// </summary>
    public static class ConfigManager
    {
        private static readonly string ConfigFilePath;

        static ConfigManager()
        {
            // 优先使用程序运行目录，若不可写则回退到用户 AppData
            string baseDir = AppContext.BaseDirectory;
            string testPath = Path.Combine(baseDir, ".write_test");
            try
            {
                File.WriteAllText(testPath, "");
                File.Delete(testPath);
                ConfigFilePath = Path.Combine(baseDir, "config.json");
            }
            catch (Exception)
            {
                // 程序目录不可写，使用 AppData
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "C99");
                Directory.CreateDirectory(appData);
                ConfigFilePath = Path.Combine(appData, "config.json");
            }
        }

        /// <summary>加载配置，若文件不存在则返回默认配置</summary>
        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null) return config;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
            }
            return new AppConfig();
        }

        /// <summary>保存配置到文件</summary>
        public static void Save(AppConfig config)
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        /// <summary>获取配置文件路径（给 UI 显示用）</summary>
        public static string GetConfigFilePath() => ConfigFilePath;
    }
}
