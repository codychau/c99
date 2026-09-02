using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using C99.Services;
using C99.Models;
using C99.Helpers;
using Windows.UI.Notifications;

namespace C99
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string prop = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        // ========== 导航 ==========
        private bool _isNavExpanded = true;
        private const double ExpandedWidth = 160;
        private const double CollapsedWidth = 48;
        private const double AnimDurationMs = 500;
        private DispatcherTimer? _animTimer;
        private double _animFrom, _animTo;
        private DateTime _animStart;

        // ========== 运行状态 ==========
        private Process? _runningProcess;
        private AppConfig _config = new();

        // ========== 指标 & Dashboard ==========
        private MetricsService? _metricsService;
        private DispatcherTimer? _dashboardTimer;
        private DateTime _engineStartTime;
        private bool _dashboardBuilt;
        private List<TextBlock> _dashboardValueTexts = new();

        // ========== 参数自动保存（防抖） ==========
        private bool _paramsDirty;
        private DispatcherTimer? _saveTimer;

        // ========== 知识库板块 ==========
        private IVectorStore? _kbStore;
        private VectorEmbeddingService _kbEmbedding = new();
        private KnowledgeBaseConfig _kbConfig = new();
        private bool _kbInitialized;
        private bool _kbAddDirectoryBusy;
        private string? _currentDocId;
        private List<KnowledgeChunk> _kbAllChunks = new();
        private CancellationTokenSource? _kbScanCts;
        private CancellationTokenSource? _kbSkipFileCts;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "C99";
            this.SystemBackdrop = new DesktopAcrylicBackdrop();

            // 加载配置并应用到 UI
            LoadConfigAndApply();

            // 指标服务
            _metricsService = new MetricsService();

            this.SizeChanged += (s, e) =>
            {
                if (AIGeneralStoreContent.Visibility == Visibility.Visible)
                    RebuildAIToolsGrid();
            };

            // 参数防抖保存定时器
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer?.Stop();
                if (_paramsDirty)
                {
                    _paramsDirty = false;
                    SaveAllParams();
                }
            };

            // 窗口关闭时终止子进程并保存参数
            this.Closed += (s, e) =>
            {
                _isClosing = true;
                if (_runningProcess != null && !_runningProcess.HasExited)
                {
                    try { _runningProcess.Kill(); } catch { }
                    try { _runningProcess.WaitForExit(3000); } catch { }
                    _runningProcess = null;
                }
                SaveAllParams();
                SaveDreamFactoryConfig();
                _dreamFactoryService?.Dispose();
                _trayHelper?.Dispose();
                _kbEmbedding.Dispose();
            };

            // 初始化 AI梦工厂
            LoadDreamFactoryConfig();
            if (_dreamConfig.AutoStart)
            {
                StartDreamFactoryService();
            }

            ShowHome();
        }

        // ==================== 导航功能 ====================

        private void OnToggleNavClick(object sender, RoutedEventArgs e)
        {
            _animTimer?.Stop();
            _isNavExpanded = !_isNavExpanded;
            _animFrom = NavColumn.ActualWidth;
            _animTo = _isNavExpanded ? ExpandedWidth : CollapsedWidth;
            _animStart = DateTime.UtcNow;
            UpdateNavUI();
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _animTimer.Tick += OnAnimTick;
            _animTimer.Start();
        }

        private void OnAnimTick(object? sender, object e)
        {
            double elapsed = (DateTime.UtcNow - _animStart).TotalMilliseconds;
            double t = Math.Clamp(elapsed / AnimDurationMs, 0.0, 1.0);
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            NavColumn.Width = new GridLength(_animFrom + (_animTo - _animFrom) * t);
            if (elapsed >= AnimDurationMs) { _animTimer?.Stop(); _animTimer = null; }
        }

        private void UpdateNavUI()
        {
            var v = _isNavExpanded ? Visibility.Visible : Visibility.Collapsed;
            var p = _isNavExpanded ? new Thickness(12, 0, 12, 0) : new Thickness(0);
            ToggleText.Text = _isNavExpanded ? " 收起" : "";
            BtnHomeText.Visibility = BtnAIDreamFactoryText.Visibility = BtnAIGeneralStoreText.Visibility = v;
            BtnSettingsText.Visibility = BtnAboutText.Visibility = BtnAIBaseText.Visibility = v;
            BtnHome.Padding = BtnAIDreamFactory.Padding = BtnAIGeneralStore.Padding = p;
            BtnSettings.Padding = BtnAbout.Padding = BtnAIBase.Padding = p;
        }

        private void HideAllContents()
        {
            _dashboardTimer?.Stop();
            HomeContent.Visibility = Visibility.Collapsed;
            AIDreamFactoryContent.Visibility = Visibility.Collapsed;
            AIGeneralStoreContent.Visibility = Visibility.Collapsed;
            KnowledgeBaseContent.Visibility = Visibility.Collapsed;
            SettingsContent.Visibility = Visibility.Collapsed;
            AboutContent.Visibility = Visibility.Collapsed;
            AIBaseContent.Visibility = Visibility.Collapsed;
        }

        private void ShowHome()
        {
            HideAllContents();
            HomeContent.Visibility = Visibility.Visible;
            if (!_dashboardBuilt) BuildDashboardLayout();
            UpdateDashboardValues();
            if (_dashboardTimer == null)
            {
                _dashboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _dashboardTimer.Tick += (s, e) => UpdateDashboardValues();
            }
            _dashboardTimer.Start();
        }
        private void ShowAIDreamFactory() { HideAllContents(); AIDreamFactoryContent.Visibility = Visibility.Visible; }
        private void ShowAIGeneralStore() { HideAllContents(); AIGeneralStoreContent.Visibility = Visibility.Visible; _toolsPage = 0; RebuildAIToolsGrid(); }
        private void ShowKnowledgeBase()
        {
            HideAllContents();
            KnowledgeBaseContent.Visibility = Visibility.Visible;
            EnsureKnowledgeBaseLoaded();
            // 自动连接并加载集合列表，避免重启后下拉菜单为空（用户须手动点"连接"）
            _ = EnsureKbStoreConnected();
        }
        private void ShowSettings() { HideAllContents(); SettingsContent.Visibility = Visibility.Visible; LoadSettingsExternalLLMConfig(); }
        private void ShowAbout() { HideAllContents(); AboutContent.Visibility = Visibility.Visible; }
        private void ShowAIBase() { HideAllContents(); AIBaseContent.Visibility = Visibility.Visible; TryAutoProbeVisibleGPUs(); }

        private void OnHomeClick(object sender, RoutedEventArgs e) => ShowHome();
        private void OnAIDreamFactoryClick(object sender, RoutedEventArgs e) => ShowAIDreamFactory();
        private void OnAIGeneralStoreClick(object sender, RoutedEventArgs e) => ShowAIGeneralStore();
        private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
        private void OnAboutClick(object sender, RoutedEventArgs e) => ShowAbout();
        private void OnAIBaseClick(object sender, RoutedEventArgs e) => ShowAIBase();

        // ==================== 配置管理 ====================

        /// <summary>加载配置并应用到 UI 控件</summary>
        private void LoadConfigAndApply()
        {
            try
            {
                _config = ConfigManager.Load();

                // 应用 LLM 搜索路径
                if (!string.IsNullOrEmpty(_config.LLMSearchPath))
                {
                    LLMSearchPath.Text = _config.LLMSearchPath;
                    RefreshModelSubDirs(_config.LLMSearchPath, _config.SelectedModelSubDir);
                }

                // 应用各引擎的启动器目录
                if (_config.EngineLauncherDirs.TryGetValue("llama.cpp", out var llDir))
                    LLamaLauncherDir.Text = llDir;
                if (_config.EngineLauncherDirs.TryGetValue("vllm", out var vllDir))
                    VLLMLauncherDir.Text = vllDir;
                if (_config.EngineLauncherDirs.TryGetValue("lmstudio", out var lmDir))
                    LMStudioLauncherDir.Text = lmDir;
                if (_config.EngineLauncherDirs.TryGetValue("ollama", out var olDir))
                    OllamaLauncherDir.Text = olDir;

                // 恢复所有引擎参数（滑块、文本框、下拉框等）
                LoadAllParams();

                // 同步后清除脏标记
                _paramsDirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
            }
        }

        /// <summary>保存当前 UI 状态到配置文件</summary>
        private void SaveConfig()
        {
            try
            {
                _config.LLMSearchPath = LLMSearchPath.Text.Trim();

                // 保存各引擎的启动器目录
                _config.EngineLauncherDirs["llama.cpp"] = LLamaLauncherDir.Text.Trim();
                _config.EngineLauncherDirs["vllm"] = VLLMLauncherDir.Text.Trim();
                _config.EngineLauncherDirs["lmstudio"] = LMStudioLauncherDir.Text.Trim();
                _config.EngineLauncherDirs["ollama"] = OllamaLauncherDir.Text.Trim();

                ConfigManager.Save(_config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        /// <summary>保存所有引擎参数到配置</summary>
        private void SaveAllParams()
        {
            try
            {
                var dict = _config.EngineParams;
                dict["LLamaGPULayers"] = ((int)LLamaGPULayers.Value).ToString();
                dict["LLamaContextSize"] = ((int)LLamaContextSize.Value).ToString();
                dict["LLamaNPredict"] = ((int)LLamaNPredict.Value).ToString();
                dict["LLamaThreads"] = ((int)LLamaThreads.Value).ToString();
                dict["LLamaBatchSize"] = ((int)LLamaBatchSize.Value).ToString();
                dict["LLamaUBatchSize"] = ((int)LLamaUBatchSize.Value).ToString();
                dict["LLamaParallel"] = ((int)LLamaParallel.Value).ToString();
                dict["LLamaMLock"] = LLamaMLock.IsChecked == true ? "true" : "false";
                dict["LLamaMMap"] = LLamaMMap.SelectedIndex.ToString();
                dict["LLamaFlashAttn"] = LLamaFlashAttn.SelectedIndex.ToString();
                dict["LLamaNuma"] = LLamaNuma.SelectedIndex.ToString();
                dict["LLamaCacheTypeK"] = LLamaCacheTypeK.SelectedIndex.ToString();
                dict["LLamaCacheTypeV"] = LLamaCacheTypeV.SelectedIndex.ToString();
                dict["LLamaSplitMode"] = LLamaSplitMode.SelectedIndex.ToString();
                dict["LLamaMainGPU"] = LLamaMainGPU.Text;
                dict["LLamaDevice"] = LLamaDevice.Text;
                dict["LLamaTensorSplit"] = LLamaTensorSplit.Text;
                // 下拉尚未探测填充时，保留上次保存值，避免误清空
                string visibleTag = (LLamaVisibleGPU.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                dict["LLamaVisibleGPU"] = _visibleGpuUILoaded ? visibleTag : GetSavedVisibleGPU();
                dict["LLamaTemperature"] = LLamaTemperature.Text;
                dict["LLamaTopK"] = LLamaTopK.Text;
                dict["LLamaTopP"] = LLamaTopP.Text;
                dict["LLamaMinP"] = LLamaMinP.Text;
                dict["LLamaRepeatPenalty"] = LLamaRepeatPenalty.Text;
                dict["LLamaPresencePenalty"] = LLamaPresencePenalty.Text;
                dict["LLamaFrequencyPenalty"] = LLamaFrequencyPenalty.Text;
                dict["LLamaMirostat"] = LLamaMirostat.SelectedIndex.ToString();
                dict["LLamaMirostatLR"] = LLamaMirostatLR.Text;
                dict["LLamaMirostatEnt"] = LLamaMirostatEnt.Text;
                dict["LLamaSeed"] = LLamaSeed.Text;
                dict["LLamaSpecType"] = LLamaSpecType.SelectedIndex.ToString();
                dict["LLamaHost"] = LLamaHost.Text;
                dict["LLamaPort"] = LLamaPort.Text;
                dict["LLamaExtraArgs"] = LLamaExtraArgs.Text;
                ConfigManager.Save(_config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存参数失败: {ex.Message}");
            }
        }

        /// <summary>标记参数已改动，启动防抖定时器自动保存</summary>
        private void MarkParamsDirty()
        {
            _paramsDirty = true;
            _saveTimer?.Start();
        }

        /// <summary>从配置恢复所有引擎参数</summary>
        private void LoadAllParams()
        {
            try
            {
                var dict = _config.EngineParams;
                if (dict.Count == 0) return;

                if (dict.TryGetValue("LLamaGPULayers", out var v) && int.TryParse(v, out var vi)) LLamaGPULayers.Value = vi;
                if (dict.TryGetValue("LLamaContextSize", out v) && int.TryParse(v, out vi)) LLamaContextSize.Value = vi;
                if (dict.TryGetValue("LLamaNPredict", out v) && int.TryParse(v, out vi)) LLamaNPredict.Value = vi;
                if (dict.TryGetValue("LLamaThreads", out v) && int.TryParse(v, out vi)) LLamaThreads.Value = vi;
                if (dict.TryGetValue("LLamaBatchSize", out v) && int.TryParse(v, out vi)) LLamaBatchSize.Value = vi;
                if (dict.TryGetValue("LLamaUBatchSize", out v) && int.TryParse(v, out vi)) LLamaUBatchSize.Value = vi;
                if (dict.TryGetValue("LLamaParallel", out v) && int.TryParse(v, out vi)) LLamaParallel.Value = vi;
                if (dict.TryGetValue("LLamaMLock", out v)) LLamaMLock.IsChecked = v == "true";
                if (dict.TryGetValue("LLamaMMap", out v) && int.TryParse(v, out vi) && vi >= 0 && vi < LLamaMMap.Items.Count) LLamaMMap.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaFlashAttn", out v) && int.TryParse(v, out vi)) LLamaFlashAttn.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaNuma", out v) && int.TryParse(v, out vi)) LLamaNuma.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaCacheTypeK", out v) && int.TryParse(v, out vi)) LLamaCacheTypeK.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaCacheTypeV", out v) && int.TryParse(v, out vi)) LLamaCacheTypeV.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaSplitMode", out v) && int.TryParse(v, out vi)) LLamaSplitMode.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaMainGPU", out v)) LLamaMainGPU.Text = v;
                if (dict.TryGetValue("LLamaDevice", out v)) LLamaDevice.Text = v;
                if (dict.TryGetValue("LLamaTensorSplit", out v)) LLamaTensorSplit.Text = v;
                if (dict.TryGetValue("LLamaTemperature", out v)) LLamaTemperature.Text = v;
                if (dict.TryGetValue("LLamaTopK", out v)) LLamaTopK.Text = v;
                if (dict.TryGetValue("LLamaTopP", out v)) LLamaTopP.Text = v;
                if (dict.TryGetValue("LLamaMinP", out v)) LLamaMinP.Text = v;
                if (dict.TryGetValue("LLamaRepeatPenalty", out v)) LLamaRepeatPenalty.Text = v;
                if (dict.TryGetValue("LLamaPresencePenalty", out v)) LLamaPresencePenalty.Text = v;
                if (dict.TryGetValue("LLamaFrequencyPenalty", out v)) LLamaFrequencyPenalty.Text = v;
                if (dict.TryGetValue("LLamaMirostat", out v) && int.TryParse(v, out vi)) LLamaMirostat.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaMirostatLR", out v)) LLamaMirostatLR.Text = v;
                if (dict.TryGetValue("LLamaMirostatEnt", out v)) LLamaMirostatEnt.Text = v;
                if (dict.TryGetValue("LLamaSeed", out v)) LLamaSeed.Text = v;
                if (dict.TryGetValue("LLamaSpecType", out v) && int.TryParse(v, out vi)) LLamaSpecType.SelectedIndex = vi;
                if (dict.TryGetValue("LLamaHost", out v)) LLamaHost.Text = v;
                if (dict.TryGetValue("LLamaPort", out v)) LLamaPort.Text = v;
                if (dict.TryGetValue("LLamaExtraArgs", out v)) LLamaExtraArgs.Text = v;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载参数失败: {ex.Message}");
            }
        }

        // ==================== 各引擎启动器目录浏览 ====================

        private async void OnBrowseLLamaLauncherDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) { LLamaLauncherDir.Text = folder.Path; SaveConfig(); }
        }

        private async void OnBrowseVLLMLauncherDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) { VLLMLauncherDir.Text = folder.Path; SaveConfig(); }
        }

        private async void OnBrowseLMStudioLauncherDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) { LMStudioLauncherDir.Text = folder.Path; SaveConfig(); }
        }

        private async void OnBrowseOllamaLauncherDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) { OllamaLauncherDir.Text = folder.Path; SaveConfig(); }
        }

        // ==================== 大语言模型搜索路径 ====================

        private async void OnBrowseLLMSearchPath(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                LLMSearchPath.Text = folder.Path;
                RefreshModelSubDirs(folder.Path, null);
                SaveConfig();
            }
        }

        private void OnLLMSearchPathChanged(object sender, TextChangedEventArgs e)
        {
            string path = LLMSearchPath.Text.Trim();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                RefreshModelSubDirs(path, null);
            }
            SaveConfig();
        }

        /// <summary>刷新模型子目录下拉列表</summary>
        private void RefreshModelSubDirs(string rootPath, string? selectedSubDir)
        {
            try
            {
                ModelSubDirSelector.Items.Clear();

                if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                {
                    ModelSubDirSelector.PlaceholderText = "路径无效或无子目录";
                    return;
                }

                var subDirs = Directory.GetDirectories(rootPath);
                if (subDirs.Length == 0)
                {
                    ModelSubDirSelector.PlaceholderText = "未找到子目录";
                    // 添加根目录本身作为选项
                    ModelSubDirSelector.Items.Add(new ComboBoxItem
                    {
                        Content = Path.GetFileName(rootPath),
                        Tag = rootPath
                    });
                    if (!string.IsNullOrEmpty(selectedSubDir) &&
                        string.Equals(selectedSubDir, rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ModelSubDirSelector.SelectedIndex = 0;
                    }
                    return;
                }

                int selectedIdx = -1;
                for (int i = 0; i < subDirs.Length; i++)
                {
                    string dirName = Path.GetFileName(subDirs[i]);
                    var item = new ComboBoxItem
                    {
                        Content = dirName,
                        Tag = subDirs[i]
                    };
                    ModelSubDirSelector.Items.Add(item);

                    if (!string.IsNullOrEmpty(selectedSubDir) &&
                        string.Equals(subDirs[i], selectedSubDir, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = i;
                    }
                }

                if (selectedIdx >= 0)
                    ModelSubDirSelector.SelectedIndex = selectedIdx;
                else
                    ModelSubDirSelector.PlaceholderText = $"共 {subDirs.Length} 个子目录，请选择...";
            }
            catch (Exception ex)
            {
                ModelSubDirSelector.PlaceholderText = $"扫描失败: {ex.Message}";
            }
        }

        private async void OnModelSubDirSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ModelSubDirSelector.SelectedItem is ComboBoxItem item && item.Tag is string dirPath)
            {
                _config.SelectedModelSubDir = Path.GetFileName(dirPath);
                _config.SelectedModelSubDirFullPath = dirPath;
                SaveConfig();

                // 自动搜索该目录下的模型文件（.gguf）
                await AutoFindModelFilesAsync(dirPath);

                // 检测到 mmproj 且已选中主模型 → 询问用户是否启用多模态
                if (_mmprojFilePath != null && _currentModelFilePath != null)
                {
                    string modelName = Path.GetFileName(_currentModelFilePath);
                    string mmprojName = Path.GetFileName(_mmprojFilePath);
                    string nl = System.Environment.NewLine;
                    string msg = "模型目录中发现多模态视觉投影文件:" + nl
                        + mmprojName + nl + nl
                        + "已选中主模型: " + modelName + nl + nl
                        + "是否启用多模态视觉功能?" + nl
                        + "(启用后会自动添加 --mmproj 参数)";
                    bool? result = await ShowYesNoDialogAsync("检测到多模态投影文件", msg);
                    _multimodalEnabled = result == true;
                }
            }
        }

        // 当前选中的模型文件路径（由统一模型目录选择器自动设定）
        private string? _currentModelFilePath;

        // 多模态投影文件路径（检测到 mmproj 时设置）
        private string? _mmprojFilePath;

        // 是否已启用多模态（用户确认后为 true）
        private bool _multimodalEnabled;

        /// <summary>在子目录的 .gguf 文件中过滤出 mmproj 文件（视觉投影），返回主模型文件列表</summary>
        private string[] GetModelGgufFiles(string directory)
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();

            var all = Directory.GetFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly);
            if (all.Length == 0) return all;

            // 过滤掉 mmproj（视觉投影）文件，优先选主模型
            var models = new System.Collections.Generic.List<string>(all.Length);
            foreach (var f in all)
            {
                if (!Path.GetFileName(f).StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase))
                    models.Add(f);
            }
            // 如果没有非 mmproj 文件，才回退使用全部
            return models.Count > 0 ? models.ToArray() : all;
        }

        /// <summary>在指定目录中自动查找 GGUF 模型文件，返回是否找到 mmproj（由调用者决定是否弹窗询问多模态）</summary>
        private Task AutoFindModelFilesAsync(string directory)
        {
            _currentModelFilePath = null;
            _mmprojFilePath = null;
            _multimodalEnabled = false;

            try
            {
                if (!Directory.Exists(directory)) return Task.CompletedTask;

                var all = Directory.GetFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly);
                var modelFiles = GetModelGgufFiles(directory);

                if (modelFiles.Length == 1)
                {
                    _currentModelFilePath = modelFiles[0];
                    System.Diagnostics.Debug.WriteLine($"自动选中模型: {_currentModelFilePath}");
                }

                // 检测多模态投影文件 (mmproj)，返回给调用者处理弹窗
                foreach (var f in all)
                {
                    if (Path.GetFileName(f).StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase))
                    {
                        _mmprojFilePath = f;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _currentModelFilePath = null;
                _mmprojFilePath = null;
                System.Diagnostics.Debug.WriteLine($"自动查找模型文件失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>获取当前选中的模型文件路径（从统一模型目录中获取）</summary>
        private string? GetCurrentModelPath()
        {
            // 如果有已选中的模型文件路径，直接返回
            if (!string.IsNullOrEmpty(_currentModelFilePath) && File.Exists(_currentModelFilePath))
                return _currentModelFilePath;

            // 否则从模型子目录选择器中获取（过滤 mmproj）
            if (ModelSubDirSelector.SelectedItem is ComboBoxItem item && item.Tag is string dirPath)
            {
                var models = GetModelGgufFiles(dirPath);
                if (models.Length > 0) return models[0];
            }

            return null;
        }

        /// <summary>WinUI3 文件夹选择器</summary>
        private async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            return await picker.PickSingleFolderAsync();
        }

        // ==================== 搜索 & 杂货铺 ====================

        private async void OnSearchClick(object sender, RoutedEventArgs e)
        {
            string keyword = SearchTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                await ShowDialogAsync("搜索", $"正在搜索: {keyword}");
            }
        }

        private void OpenToolEditor(AIToolItem tool, string title, Action<AIToolItem> onSave)
        {
            var win = new ToolEditorWindow(tool, title, onSave, async (t, ctx) =>
            {
                if (_dreamFactoryService == null) return "AI 梦工厂服务未启动";
                return await _dreamFactoryService.DebugToolAsync(t, ctx);
            });
            win.Activate();
        }

        private void OnGridButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string label)
            {
                if (label == "__new__")
                {
                    var tool = new AIToolItem { Name = "新工具", Icon = "🛠️" };
                    OpenToolEditor(tool, "新工具", edited =>
                    {
                        _dreamConfig.AITools.Add(edited);
                        _toolsPage = 0;
                        SaveDreamFactoryConfig();
                        RebuildAIToolsGrid();
                    });
                    return;
                }

                var existing = _dreamConfig.AITools.FirstOrDefault(t => t.Name == label);
                if (existing != null)
                {
                    // 知识库工具进入特殊板块
                    if (existing.Category == "知识库" || existing.Name == "知识库")
                    {
                        ShowKnowledgeBase();
                        return;
                    }

                    OpenToolEditor(existing, label, edited =>
                    {
                        SaveDreamFactoryConfig();
                        RebuildAIToolsGrid();
                    });
                }
            }
        }

        private void BuildDashboardLayout()
        {
            if (DashboardGrid == null) return;
            DashboardGrid.Children.Clear();
            DashboardGrid.RowDefinitions.Clear();
            DashboardGrid.ColumnDefinitions.Clear();
            _dashboardValueTexts.Clear();

            int rows = 3;
            for (int i = 0; i < rows; i++)
                DashboardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var divColor = Microsoft.UI.ColorHelper.FromArgb(0x1A, 0x00, 0x00, 0x00);
            var divBrush = new SolidColorBrush(divColor);

            for (int i = 0; i < 2; i++)
            {
                var v = new Border { Width = 1, Background = divBrush, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
                Grid.SetRowSpan(v, rows); Grid.SetColumn(v, i + 1);
                DashboardGrid.Children.Add(v);
            }
            for (int i = 0; i < rows - 1; i++)
            {
                var h = new Border { Height = 1, Background = divBrush, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                Grid.SetColumnSpan(h, 3); Grid.SetRow(h, i + 1);
                DashboardGrid.Children.Add(h);
            }

            var hoverColor = Microsoft.UI.ColorHelper.FromArgb(0x0C, 0x00, 0x00, 0x00);

            var cardDefs = new (string Icon, string Label)[]
            {
                ("🤖", "AI调用次数"),
                ("⚡", "Token总用量"),
                ("💰", "API费用"),
                ("🏭", "梦工厂调用"),
                ("🔧", "流水线步骤"),
                ("💵", "AI底座费用"),
                ("⏱️", "AI底座运行"),
                ("💹", "总费用"),
                ("📊", "预估月费用"),
            };

            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    if (idx >= cardDefs.Length) break;
                    var (icon, label) = cardDefs[idx++];

                    var bg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    var block = new Border { Background = bg, Padding = new Thickness(16) };
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    stack.Children.Add(new TextBlock { Text = icon, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center });
                    var valueTb = new TextBlock { Text = "", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) };
                    _dashboardValueTexts.Add(valueTb);
                    stack.Children.Add(valueTb);
                    stack.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x7B, 0x8B)), HorizontalAlignment = HorizontalAlignment.Center });
                    block.Child = stack;

                    Grid.SetRow(block, r); Grid.SetColumn(block, c);
                    DashboardGrid.Children.Add(block);

                    AttachHover(block, bg, hoverColor);
                }
            }

            _dashboardBuilt = true;
        }

        private void UpdateDashboardValues()
        {
            if (DashboardGrid == null || _metricsService == null || !_dashboardBuilt) return;

            var m = _metricsService.GetCurrent();
            long totalTokens = m.TotalPromptTokens + m.TotalCompletionTokens;
            double apiCost = m.TotalApiCost;
            double localCost = m.TotalLocalTokens * (Math.Max(0, _dreamConfig.LocalPricePerMillion) / 1_000_000.0);
            double totalCost = apiCost + localCost;

            var values = new string[]
            {
                $"{m.TotalAICalls:N0} 次",
                $"{totalTokens:N0}",
                $"¥ {apiCost:F2}",
                $"{m.TotalReports:N0} 次",
                $"{m.TotalPipelineSteps:N0} 次",
                $"¥ {localCost:F2}",
                FormatDuration(m.TotalEngineRunSeconds),
                $"¥ {totalCost:F2}",
                ProjectedMonthly(totalCost, m.FirstRecord),
            };

            for (int i = 0; i < _dashboardValueTexts.Count && i < values.Length; i++)
                _dashboardValueTexts[i].Text = values[i];
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 60) return $"{seconds:F0}s";
            if (seconds < 3600) return $"{seconds / 60:F0}m {seconds % 60:F0}s";
            return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m";
        }

        private static string ProjectedMonthly(double totalCost, DateTime firstRecord)
        {
            var days = Math.Max(1, (DateTime.Now - firstRecord).TotalDays);
            double monthly = totalCost / days * 30;
            return $"¥ {monthly:F2}";
        }

        private void RebuildAIToolsGrid()
        {
            if (AIToolsGrid == null) return;
            AIToolsGrid.Children.Clear();
            AIToolsGrid.RowDefinitions.Clear();

            // compute available rows based on window height
            int rows = ComputeToolRows();
            int toolsPerPage = rows * 3 - 1; // -1 for "创建工具"

            var items = _dreamConfig.AITools;
            int pageOffset = _toolsPage * toolsPerPage;
            var pageItems = items.Skip(pageOffset).Take(toolsPerPage).ToList();

            // set Grid height to fill available space
            AIToolsGrid.Height = rows * 80;

            for (int i = 0; i < rows; i++)
                AIToolsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var dividerColor = Microsoft.UI.ColorHelper.FromArgb(0x1A, 0x00, 0x00, 0x00);
            var dividerBrush = new SolidColorBrush(dividerColor);

            for (int i = 0; i < 2; i++)
            {
                var vLine = new Border { Width = 1, Background = dividerBrush, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
                Grid.SetRowSpan(vLine, rows); Grid.SetColumn(vLine, i + 1);
                AIToolsGrid.Children.Add(vLine);
            }

            for (int i = 0; i < rows - 1; i++)
            {
                var hLine = new Border { Height = 1, Background = dividerBrush, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                Grid.SetColumnSpan(hLine, 3); Grid.SetRow(hLine, i + 1);
                AIToolsGrid.Children.Add(hLine);
            }

            var hoverColor = Microsoft.UI.ColorHelper.FromArgb(0x0C, 0x00, 0x00, 0x00);

            // "创建工具" — always at row=0, col=0
            var newBg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var newBlock = new Border { Tag = "__new__", Background = newBg, Padding = new Thickness(8) };
            var newStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            newStack.Children.Add(new TextBlock { Text = "＋", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x94, 0x94, 0x94)) });
            newStack.Children.Add(new TextBlock { Text = "创建工具", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x94, 0x94, 0x94)) });
            newBlock.Child = newStack;
            newBlock.Tapped += OnGridButtonClick;
            AttachHover(newBlock, newBg, hoverColor);
            Grid.SetRow(newBlock, 0); Grid.SetColumn(newBlock, 0);
            AIToolsGrid.Children.Add(newBlock);

            // page items
            int idx = 0;
            int startRow = 0, startCol = 1;
            for (int r = startRow; r < rows; r++)
            {
                int cc = (r == startRow) ? startCol : 0;
                for (; cc < 3; cc++)
                {
                    if (idx >= pageItems.Count) break;
                    var tool = pageItems[idx++];

                    var bgBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    var block = new Border { Tag = tool.Name, Background = bgBrush, Padding = new Thickness(8) };
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    stack.Children.Add(new TextBlock { Text = tool.Icon, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center });
                    stack.Children.Add(new TextBlock { Text = tool.Name, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                    block.Child = stack;
                    block.Tapped += OnGridButtonClick;
                    AttachHover(block, bgBrush, hoverColor);
                    Grid.SetRow(block, r); Grid.SetColumn(block, cc);
                    AIToolsGrid.Children.Add(block);
                }
            }

            RebuildPagination();
        }

        private int ComputeToolRows()
        {
            double windowH = this.Bounds.Height;
            double used = 60 + 40 + 60 + 20 + 40 + 20 + 40;
            double available = windowH - used;
            int rows = Math.Max(1, (int)(available / 80));
            return Math.Min(rows, 12);
        }

        private int GetToolsPerPage() => ComputeToolRows() * 3 - 1;

        private void RebuildPagination()
        {
            if (PaginationPanel == null) return;
            PaginationPanel.Children.Clear();

            int toolsPerPage = GetToolsPerPage();
            int total = _dreamConfig.AITools.Count;
            int totalPages = Math.Max(1, (total + toolsPerPage - 1) / toolsPerPage);
            int cur = _toolsPage;

            // prev
            var prevBtn = new Button { Content = "◀", FontSize = 16, Width = 48, Margin = new Thickness(4), Tag = "prev" };
            prevBtn.IsEnabled = cur > 0;
            prevBtn.Click += OnPageClick;
            PaginationPanel.Children.Add(prevBtn);

            // page numbers
            int maxVisible = 5;
            int half = maxVisible / 2;
            int start = Math.Max(0, cur - half);
            int end = Math.Min(totalPages - 1, start + maxVisible - 1);
            if (end - start < maxVisible - 1) start = Math.Max(0, end - maxVisible + 1);

            if (start > 0)
            {
                var firstBtn = new Button { Content = "1", FontSize = 16, Width = 48, Margin = new Thickness(4) };
                firstBtn.Click += OnPageClick;
                PaginationPanel.Children.Add(firstBtn);
                if (start > 1)
                    PaginationPanel.Children.Add(new TextBlock { Text = "...", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
            }

            for (int i = start; i <= end; i++)
            {
                var pageBtn = new Button { Content = (i + 1).ToString(), FontSize = 16, Width = 48, Margin = new Thickness(4) };
                if (i == cur)
                {
                    pageBtn.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
                    pageBtn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                }
                pageBtn.Click += OnPageClick;
                PaginationPanel.Children.Add(pageBtn);
            }

            if (end < totalPages - 1)
            {
                if (end < totalPages - 2)
                    PaginationPanel.Children.Add(new TextBlock { Text = "...", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
                var lastBtn = new Button { Content = totalPages.ToString(), FontSize = 16, Width = 48, Margin = new Thickness(4) };
                lastBtn.Click += OnPageClick;
                PaginationPanel.Children.Add(lastBtn);
            }

            // next
            var nextBtn = new Button { Content = "▶", FontSize = 16, Width = 48, Margin = new Thickness(4), Tag = "next" };
            nextBtn.IsEnabled = cur < totalPages - 1;
            nextBtn.Click += OnPageClick;
            PaginationPanel.Children.Add(nextBtn);
        }

        private void AttachHover(Border block, SolidColorBrush brush, Windows.UI.Color targetColor)
        {
            block.PointerEntered += (s, e) =>
            {
                var anim = new ColorAnimation { To = targetColor, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
                Storyboard.SetTarget(anim, brush);
                Storyboard.SetTargetProperty(anim, "Color");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            };
            block.PointerExited += (s, e) =>
            {
                var anim = new ColorAnimation { To = Microsoft.UI.Colors.Transparent, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
                Storyboard.SetTarget(anim, brush);
                Storyboard.SetTargetProperty(anim, "Color");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            };
        }

        private void OnPageClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                int toolsPerPage = GetToolsPerPage();
                int totalPages = Math.Max(1, (_dreamConfig.AITools.Count + toolsPerPage - 1) / toolsPerPage);
                string text = btn.Content?.ToString() ?? "";

                if (btn.Tag?.ToString() == "prev")
                    _toolsPage = Math.Max(0, _toolsPage - 1);
                else if (btn.Tag?.ToString() == "next")
                    _toolsPage = Math.Min(totalPages - 1, _toolsPage + 1);
                else if (int.TryParse(text, out int p))
                    _toolsPage = Math.Clamp(p - 1, 0, totalPages - 1);
                else
                    return;

                RebuildAIToolsGrid();
            }
        }

        // ==================== 知识库特殊板块 ====================

        private void OnKbBackToStore(object sender, RoutedEventArgs e) => ShowAIGeneralStore();

        private void OnKbTabChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切换到召回页时初始化集合下拉（初始化阶段 UI 未加载完成时跳过）
            if (KbTabView != null && KnowledgeBaseContent != null &&
                KnowledgeBaseContent.Visibility == Visibility.Visible)
            {
                var item = KbTabView.SelectedItem as TabViewItem;
                if (item?.Header?.ToString() == "召回")
                    _ = EnsureKbStoreConnected();
            }
        }

        /// <summary>确保知识库 UI/配置已初始化</summary>
        private void EnsureKnowledgeBaseLoaded()
        {
            if (_kbInitialized) return;
            _kbConfig = _config.KnowledgeBase ?? new KnowledgeBaseConfig();

            // 向量模型下拉（仅保留：自定义 / 本地启动）
            KbVectorModel.Items.Clear();
            KbVectorModel.Items.Add(new ComboBoxItem { Content = "自定义", Tag = "custom" });
            KbVectorModel.Items.Add(new ComboBoxItem { Content = "本地启动", Tag = "local" });
            SelectComboByTag(KbVectorModel, _kbConfig.VectorModel);
            UpdateKbVectorPanels();

            KbModelApiUrl.Text = _kbConfig.VectorModelApiUrl;
            KbModelApiKey.Text = _kbConfig.VectorModelApiKey;
            KbDimension.Text = _kbConfig.Dimension.ToString();

            // 本地启动配置
            KbLlamaCppDir.Text = _kbConfig.LlamaCppDir;
            KbLocalModelFile.Text = _kbConfig.LocalModelFile;
            KbLocalEmbeddingPort.Text = _kbConfig.LocalEmbeddingPort.ToString();

            // 数据库类型
            if (_kbConfig.DbType == VectorDbType.Milvus || _kbConfig.DbType == VectorDbType.PgVector)
            {
                KbDbExternal.IsChecked = true;
                KbExternalType.SelectedIndex = _kbConfig.DbType == VectorDbType.Milvus ? 0 : 1;
            }
            else
            {
                KbDbBuiltIn.IsChecked = true;
            }
            KbBuiltInDataDir.Text = _kbConfig.BuiltInDataDir;
            KbExternalHost.Text = _kbConfig.ExternalHost;
            KbExternalPort.Text = _kbConfig.ExternalPort.ToString();
            KbExternalUser.Text = _kbConfig.ExternalUsername;
            KbExternalPassword.Text = _kbConfig.ExternalPassword;
            KbExternalDatabase.Text = _kbConfig.ExternalDatabase;
            KbNewCollectionName.Text = _kbConfig.CollectionName;
            KbTopKSlider.Value = _kbConfig.TopK;
            KbTopKText.Text = _kbConfig.TopK.ToString();

            _kbInitialized = true;
            UpdateKbActionButtonStates();
        }

        /// <summary>内置向量库数据保存目录变化时，刷新连接/添加目录按钮的可用状态</summary>
        private void OnKbBuiltInDataDirChanged(object sender, TextChangedEventArgs e)
        {
            UpdateKbActionButtonStates();
        }

        /// <summary>
        /// 按当前知识库配置刷新操作按钮可用性：
        /// - 内置向量库且未设置数据保存目录 → 置灰「连接」「添加目录」，提示先选择目录；
        /// - 其余情况正常可用。
        /// </summary>
        private void UpdateKbActionButtonStates()
        {
            if (KbConnectBtn == null || KbAddDirectoryBtn == null || KbDbBuiltIn == null) return;

            bool builtIn = KbDbBuiltIn.IsChecked == true;
            bool hasDataDir = builtIn
                ? !string.IsNullOrWhiteSpace(KbBuiltInDataDir?.Text)
                : true;

            KbConnectBtn.IsEnabled = hasDataDir;
            KbAddDirectoryBtn.IsEnabled = hasDataDir;

            if (builtIn && !hasDataDir)
            {
                if (KbDbStatus != null)
                    KbDbStatus.Text = "⚠ 请先设置数据保存目录";
            }
        }

        private static void SelectComboByTag(Microsoft.UI.Xaml.Controls.ComboBox combo, string tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void OnKbVectorModelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KbVectorModel == null) return;
            UpdateKbVectorPanels();
        }

        /// <summary>根据向量模型来源切换面板（自定义 / 本地启动）</summary>
        private void UpdateKbVectorPanels()
        {
            if (KbVectorCustomPanel == null || KbVectorLocalPanel == null) return;
            bool isLocal = (KbVectorModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "local";
            KbVectorCustomPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
            KbVectorLocalPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnKbBrowseLlamaCppDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                KbLlamaCppDir.Text = folder.Path;
                SaveKbConfig();
            }
        }

        private async void OnKbBrowseLocalModelFile(object sender, RoutedEventArgs e)
        {
            var file = await PickFileAsync("选择向量模型", ".gguf");
            if (file != null)
            {
                KbLocalModelFile.Text = file.Path;
                SaveKbConfig();
            }
        }

        private void OnKbDbTypeChanged(object sender, RoutedEventArgs e)
        {
            if (KbBuiltInPanel == null || KbExternalPanel == null || KbDbBuiltIn == null) return;
            bool builtIn = KbDbBuiltIn.IsChecked == true;
            KbBuiltInPanel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
            KbExternalPanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
            UpdateKbActionButtonStates();
        }

        private void OnKbExternalTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isMilvus = KbExternalType.SelectedIndex == 0;
            KbExternalPort.Text = isMilvus ? "19530" : "5432";
            KbExternalDatabase.PlaceholderText = isMilvus ? "数据库名 (可选)" : "数据库名 (pgvector 必填)";
        }

        private async void OnKbBrowseDataDir(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                KbBuiltInDataDir.Text = folder.Path;
                UpdateKbActionButtonStates();
            }
        }

        private async Task<bool> EnsureKbStoreConnected()
        {
            SyncKbConfigFromUI();
            _kbConfig = _config.KnowledgeBase;

            // 内置向量库必须指定数据保存目录，否则不会静默回退到默认目录
            if (_kbConfig.DbType == VectorDbType.BuiltIn && string.IsNullOrWhiteSpace(_kbConfig.BuiltInDataDir))
            {
                KbDbStatus.Text = "⚠ 内置向量库需要设置数据保存目录";
                UpdateKbActionButtonStates();
                return false;
            }

            _kbStore ??= VectorStoreFactory.Create(_kbConfig);

            if (_kbStore.IsConnected)
            {
                // 内置库构造时即已连接，但集合下拉在重启后为空，需主动刷新
                try
                {
                    await RefreshKbCollectionSelectAsync();
                    await RefreshKbDocsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"加载集合列表失败: {ex.Message}");
                }
                return true;
            }

            KbDbStatus.Text = "连接中...";
            try
            {
                bool ok = await _kbStore.ConnectAsync();
                KbDbStatus.Text = ok ? "✅ 已连接：" + _kbStore.GetConfigSummary() : "❌ 连接失败";
                if (ok)
                {
                    await RefreshKbCollectionSelectAsync();
                    await RefreshKbDocsAsync();
                }
                SaveKbConfig();
                return ok;
            }
            catch (Exception ex)
            {
                KbDbStatus.Text = "❌ 连接失败: " + ex.Message;
                SaveKbConfig();
                return false;
            }
        }

        private async void OnKbConnect(object sender, RoutedEventArgs e)
        {
            KbConnectBtn.IsEnabled = false;
            try
            {
                SyncKbConfigFromUI();
                await (_kbStore?.DisconnectAsync() ?? Task.CompletedTask);
                _kbStore = null;
                bool ok = await EnsureKbStoreConnected();
                if (ok)
                {
                    // EnsureKbStoreConnected 在“创建即已连接”时会提前返回且未刷新状态，这里统一给出反馈
                    KbDbStatus.Text = "✅ 已连接：" + _kbStore!.GetConfigSummary();
                    await RefreshKbCollectionSelectAsync();
                }
            }
            catch (Exception ex)
            {
                KbDbStatus.Text = "❌ 连接失败: " + ex.Message;
            }
            finally
            {
                KbConnectBtn.IsEnabled = true;
            }
        }

        private async void OnKbDisconnect(object sender, RoutedEventArgs e)
        {
            await (_kbStore?.DisconnectAsync() ?? Task.CompletedTask);
            _kbStore = null;
            KbDbStatus.Text = "未连接";
            KbCollectionSelect.Items.Clear();
            KbDocsList.Items.Clear();
            KbDocCount.Text = "文档列表";
            SaveKbConfig();
        }

        private void SyncKbConfigFromUI()
        {
            _config.KnowledgeBase = _kbConfig;
            _kbConfig.VectorModel = (KbVectorModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "custom";
            _kbConfig.VectorModelApiUrl = KbModelApiUrl.Text.Trim();
            _kbConfig.VectorModelApiKey = KbModelApiKey.Text.Trim();
            if (int.TryParse(KbDimension.Text, out var dim) && dim > 0) _kbConfig.Dimension = dim;
            _kbConfig.LlamaCppDir = KbLlamaCppDir.Text.Trim();
            _kbConfig.LocalModelFile = KbLocalModelFile.Text.Trim();
            if (int.TryParse(KbLocalEmbeddingPort.Text, out var localPort) && localPort > 0 && localPort < 65536)
                _kbConfig.LocalEmbeddingPort = localPort;
            _kbConfig.DbType = KbDbExternal.IsChecked == true
                ? (KbExternalType.SelectedIndex == 0 ? VectorDbType.Milvus : VectorDbType.PgVector)
                : VectorDbType.BuiltIn;
            _kbConfig.BuiltInDataDir = KbBuiltInDataDir.Text.Trim();
            _kbConfig.ExternalHost = KbExternalHost.Text.Trim();
            if (int.TryParse(KbExternalPort.Text, out var port)) _kbConfig.ExternalPort = port;
            _kbConfig.ExternalUsername = KbExternalUser.Text.Trim();
            _kbConfig.ExternalPassword = KbExternalPassword.Text.Trim();
            _kbConfig.ExternalDatabase = KbExternalDatabase.Text.Trim();
            if (!string.IsNullOrWhiteSpace(KbNewCollectionName.Text))
                _kbConfig.CollectionName = KbNewCollectionName.Text.Trim();
            _kbConfig.TopK = (int)KbTopKSlider.Value;
        }

        private void SaveKbConfig()
        {
            SyncKbConfigFromUI();
            ConfigManager.Save(_config);
        }

        private async Task RefreshKbCollectionSelectAsync()
        {
            if (_kbStore == null || !_kbStore.IsConnected) return;
            var collections = await _kbStore.ListCollectionsAsync();
            int prevIdx = KbCollectionSelect.SelectedIndex;
            string prevName = KbCollectionSelect.SelectedItem is ComboBoxItem prevItem ? prevItem.Content?.ToString() ?? "" : "";

            KbCollectionSelect.Items.Clear();
            foreach (var c in collections)
                KbCollectionSelect.Items.Add(new ComboBoxItem { Content = c, Tag = c });

            // 优先通过集合名称精确匹配恢复（支持中文、空格等所有字符）
            if (!string.IsNullOrEmpty(prevName) && collections.Contains(prevName))
            {
                int matchIdx = collections.IndexOf(prevName);
                KbCollectionSelect.SelectedIndex = matchIdx;
                _kbConfig.CollectionName = prevName;
                SaveKbConfig();
            }
            // 次选：通过配置中的集合名称恢复
            else if (!string.IsNullOrEmpty(_kbConfig.CollectionName) && collections.Contains(_kbConfig.CollectionName))
            {
                int matchIdx = collections.IndexOf(_kbConfig.CollectionName);
                KbCollectionSelect.SelectedIndex = matchIdx;
            }
            // 兜底：通过索引恢复（仅当名称无法匹配时）
            else if (prevIdx >= 0 && prevIdx < collections.Count)
            {
                KbCollectionSelect.SelectedIndex = prevIdx;
            }
            // 最后，如果仍无选中项则选择第一个
            else if (KbCollectionSelect.Items.Count > 0)
            {
                KbCollectionSelect.SelectedIndex = 0;
            }
        }

        private async void OnKbCollectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KbCollectionSelect?.SelectedItem is ComboBoxItem item && item.Tag is string name)
                await RefreshKbDocsAsync(name);
        }

        private string GetCurrentCollectionName()
        {
            if (KbCollectionSelect.SelectedItem is ComboBoxItem item && item.Tag is string name)
                return name;
            return KbNewCollectionName.Text.Trim();
        }

        private async void OnKbCreateCollection(object sender, RoutedEventArgs e)
        {
            string name = KbNewCollectionName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                await ShowDialogAsync("提示", "请先输入集合名称");
                return;
            }
            bool ok = await EnsureKbStoreConnected();
            if (!ok) return;
            bool created = await _kbStore!.CreateCollectionAsync(name, _kbConfig.Dimension);
            if (created)
            {
                SelectComboByTag(KbCollectionSelect, name);
                await RefreshKbCollectionSelectAsync();
                KbDbStatus.Text = $"✅ 集合 {name} 创建成功";
            }
            else
            {
                await ShowDialogAsync("错误", "创建集合失败，请检查数据库连接与配置");
            }
            SaveKbConfig();
        }

        private async void OnKbDropCollection(object sender, RoutedEventArgs e)
        {
            string name = GetCurrentCollectionName();
            if (string.IsNullOrEmpty(name))
            {
                await ShowDialogAsync("提示", "请先选择要删除的集合");
                return;
            }
            bool? result = await ShowYesNoDialogAsync("删除集合", $"确定删除集合「{name}」？此操作不可恢复。");
            if (result != true) return;
            bool ok = await EnsureKbStoreConnected();
            if (!ok) return;
            await _kbStore!.DropCollectionAsync(name);
            await RefreshKbCollectionSelectAsync();
            KbCollectionSelect.SelectedItem = null;
            await RefreshKbDocsAsync();
            KbDbStatus.Text = $"🗑 集合 {name} 已删除";
            SaveKbConfig();
        }

        /// <summary>文本文件扩展名（添加目录时扫描）</summary>
        private static readonly HashSet<string> KbTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".html", ".htm",
            ".css", ".js", ".ts", ".py", ".cs", ".java", ".yaml", ".yml", ".toml",
            ".ini", ".cfg", ".conf", ".log", ".sql", ".sh", ".bat", ".ps1",
            ".r", ".rb", ".php", ".swift", ".kt", ".lua", ".pl", ".rs", ".go",
            ".c", ".cpp", ".h", ".hpp"
        };

        /// <summary>根据切分模式启用/禁用段落分隔符输入</summary>
        private void OnKbChunkModeChanged(object sender, RoutedEventArgs e)
        {
            bool enabled = KbChunkByParagraph.IsChecked == true;
            KbChunkSeparatorLabel.Opacity = enabled ? 1.0 : 0.5;
            KbChunkSeparatorBox.IsEnabled = enabled;
        }

        /// <summary>添加目录：扫描目录下所有文本文件，切分并向量化入库（后台线程执行，避免卡 UI）</summary>
        private async void OnKbAddDirectory(object sender, RoutedEventArgs e)
        {
            if (_kbAddDirectoryBusy)
                return;

            var folder = await PickFolderAsync();
            if (folder == null) return;

            string dir = folder.Path;
            string collection = GetCurrentCollectionName();
            if (string.IsNullOrEmpty(collection))
                collection = _kbConfig.CollectionName;
            int chunkSize = int.TryParse(KbChunkSizeBox.Text, out var cs) && cs > 0 ? cs : 500;
            bool splitByParagraph = KbChunkByParagraph.IsChecked == true;
            string paragraphSeparator = ParseParagraphSeparator(KbChunkSeparatorBox.Text);
            int dimension = _kbConfig.Dimension;

            // 先确保已连接（连接本身可能走网络/IO），同时把集合创建也放到后台
            if (!await EnsureKbStoreConnected())
            {
                await ShowDialogAsync("错误", "向量数据库未连接，请检查数据库配置");
                return;
            }
            var store = _kbStore!;
            if (!await store.CollectionExistsAsync(collection))
                await store.CreateCollectionAsync(collection, dimension);

            // 获取已入库的源文件集合，供增量导入跳过（仅内置库支持）
            var processedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var existing = await ListKbSourceFilesAsync(collection);
                if (existing != null)
                    processedSources.UnionWith(existing);
            }
            catch { }

            // 进入后台线程执行扫描/切分/向量化/入库
            KbAddDirectoryProgress.Visibility = Visibility.Visible;
            KbAddDirectoryProgress.Value = 0;
            KbSkipFileBtn.Visibility = Visibility.Visible;
            KbCancelScanBtn.Visibility = Visibility.Visible;
            KbDbStatus.Text = "正在扫描目录...";
            _kbAddDirectoryBusy = true;
            using var scanCts = new CancellationTokenSource();
            _kbScanCts = scanCts;
            try
            {
                var result = await Task.Run(async () =>
                {
                    int totalChunks = 0;
                    int totalFiles = 0;
                    int skippedFiles = 0;
                    List<string> files;
                    try
                    {
                        files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Where(f => KbTextExtensions.Contains(Path.GetExtension(f)))
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() => KbDbStatus.Text = "❌ 扫描失败: " + ex.Message);
                        return new KbAddDirectoryResult(0, 0, 0);
                    }

                    if (files.Count == 0)
                    {
                        DispatcherQueue.TryEnqueue(() => KbDbStatus.Text = "该目录下未找到可构建索引的文本文件");
                        return new KbAddDirectoryResult(0, 0, 0);
                    }

                    // 跳过已处理文件（增量导入）
                    var pending = files
                        .Where(f => !processedSources.Contains(Path.GetFileName(f)))
                        .ToList();
                    skippedFiles = files.Count - pending.Count;
                    files = pending;
                    if (files.Count == 0)
                    {
                        DispatcherQueue.TryEnqueue(() => KbDbStatus.Text = $"本次无新增文件（或全部已入库，待处理 0）");
                        return new KbAddDirectoryResult(0, 0, skippedFiles);
                    }

                    // 按文件大小估算工作量权重（大文件占更大进度段，视觉更平滑；分类失败按 1 兜底）
                    var fileWeights = new long[files.Count];
                    long totalWeight = 0;
                    for (int i = 0; i < files.Count; i++)
                    {
                        try { fileWeights[i] = new FileInfo(files[i]).Length; }
                        catch { fileWeights[i] = 1; }
                        if (fileWeights[i] < 1) fileWeights[i] = 1;
                        totalWeight += fileWeights[i];
                    }
                    long doneWeight = 0;

                    for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
                    {
                        scanCts.Token.ThrowIfCancellationRequested();

                        var file = files[fileIndex];
                        string fileName = Path.GetFileName(file);
                        double baseProgress = (double)doneWeight / totalWeight;
                        int shownIndex = fileIndex + 1;
                        int shownCount = files.Count;
                        string shownFile = fileName;
                        int shownSkipped = skippedFiles;
                        double shownBase = baseProgress;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            KbAddDirectoryProgress.Value = shownBase;
                            SetKbDbStatus($"正在扫描 {shownIndex}/{shownCount}：{shownFile}（已跳过 {shownSkipped} 个）");
                        });

                        string text;
                        try { text = File.ReadAllText(file); }
                        catch { continue; } // 跳过无法读取的文件（如二进制误匹配）
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        var sw = Stopwatch.StartNew();
                        var chunkModels = new List<KnowledgeChunk>();
                        var rawChunks = SplitChunks(text, chunkSize, splitByParagraph, paragraphSeparator);
                        for (int chunkIndex = 0; chunkIndex < rawChunks.Count; chunkIndex++)
                        {
                            chunkModels.Add(new KnowledgeChunk
                            {
                                CollectionName = collection,
                                Content = rawChunks[chunkIndex],
                                Metadata = new Dictionary<string, string>
                                {
                                    ["source"] = "file",
                                    ["path"] = file,
                                    ["source_file"] = fileName,
                                    ["chunk_index"] = chunkIndex.ToString()
                                }
                            });
                        }

                        // 本文件的「跳过当前文件」令牌（取消它只影响本文件）
                        using var skipFileCts = CancellationTokenSource.CreateLinkedTokenSource(scanCts.Token);
                        _kbSkipFileCts = skipFileCts;
                        bool skipThisFile = false;

                        // 分批向量化：一次请求含多条，大幅减少 HTTP 往返
                        const int embedBatchSize = 32;
                        int embedded = 0;
                        int chunkCount = chunkModels.Count;
                        double fileWeightRatio = (double)fileWeights[fileIndex] / totalWeight;
                        // 文件内进度分段：向量化占 90%，入库占 10%
                        // 批次令牌：同时响应「全局取消」与「跳过当前文件」的取消请求
                        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(scanCts.Token, skipFileCts.Token);
                        while (embedded < chunkCount)
                        {
                            if (skipFileCts.IsCancellationRequested)
                            {
                                skipThisFile = true;
                                break;
                            }
                            scanCts.Token.ThrowIfCancellationRequested();

                            int take = Math.Min(embedBatchSize, chunkCount - embedded);
                            var batch = new List<string>(take);
                            for (int i = 0; i < take; i++)
                                batch.Add(chunkModels[embedded + i].Content);

                            List<float[]>? vecs;
                            try
                            {
                                vecs = await _kbEmbedding.EmbedBatchAsync(batch, _kbConfig, batchCts.Token);
                            }
                            catch (OperationCanceledException) when (skipFileCts.IsCancellationRequested)
                            {
                                // 仅跳过当前文件：中断向量化，剩余切片不入库
                                skipThisFile = true;
                                break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch
                            {
                                // 批调用失败时降级为逐条向量化（单条仍失败则用本地哈希向量兜底）
                                vecs = new List<float[]>(take);
                                for (int i = 0; i < take; i++)
                                {
                                    float[] v;
                                    try { v = await _kbEmbedding.EmbedAsync(batch[i], _kbConfig); }
                                    catch { v = FallbackHashEmbedding(batch[i], _kbConfig.Dimension); }
                                    vecs.Add(v);

                                    // 降级逐条时也实时反馈进度，避免长时间无提示
                                    int shownDone = embedded + i + 1;
                                    double shownP = baseProgress + (double)shownDone / chunkCount * fileWeightRatio * 0.9;
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        KbAddDirectoryProgress.Value = shownP;
                                        KbDbStatus.Text = $"正在向量化 {shownIndex}/{shownCount}：{shownFile}（逐条 {shownDone}/{chunkCount}）";
                                    });
                                }
                            }

                            for (int i = 0; i < take && i < vecs.Count; i++)
                            {
                                var v = vecs[i];
                                chunkModels[embedded + i].Embedding = (v == null || v.Length == 0)
                                    ? FallbackHashEmbedding(batch[i], _kbConfig.Dimension)
                                    : v;
                            }
                            embedded += take;

                            double shownProgress = baseProgress + (double)embedded / chunkCount * fileWeightRatio * 0.9;
                            int shownEmbedded = embedded;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                KbAddDirectoryProgress.Value = shownProgress;
                                KbDbStatus.Text = $"正在向量化 {shownIndex}/{shownCount}：{shownFile}（切块 {shownEmbedded}/{chunkCount}）";
                            });
                        }

                        if (skipThisFile)
                        {
                            skippedFiles++;
                            doneWeight += fileWeights[fileIndex];
                            DispatcherQueue.TryEnqueue(() => KbDbStatus.Text = $"已跳过文件 {fileName}");
                            _kbSkipFileCts = null;
                            continue;
                        }

                        // 分批入库并实时反馈写入进度（大文件一次全量写入期间无提示，会误以为卡死）
                        bool addOk = true;
                        string? addError = null;
                        const int addBatchSize = 64;
                        int addedCount = 0;
                        while (addedCount < chunkModels.Count)
                        {
                            if (skipFileCts.IsCancellationRequested)
                            {
                                // 跳过请求发生在入库阶段：停止剩余切片，已写入批次保留
                                skipThisFile = true;
                                break;
                            }
                            scanCts.Token.ThrowIfCancellationRequested();
                            int take = Math.Min(addBatchSize, chunkModels.Count - addedCount);
                            var sub = chunkModels.GetRange(addedCount, take);
                            var addResult = await store.AddAsync(collection, sub);
                            if (!addResult.Success)
                            {
                                addOk = false;
                                addError = addResult.Error ?? "未知原因";
                                break;
                            }
                            addedCount += take;
                            int shownAdded = addedCount;
                            double addProgress = baseProgress + fileWeightRatio * (0.9 + 0.1 * (double)shownAdded / chunkCount);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                KbAddDirectoryProgress.Value = addProgress;
                                KbDbStatus.Text = $"正在入库 {shownIndex}/{shownCount}：{shownFile}（{shownAdded}/{chunkCount}）";
                            });
                        }
                        _kbSkipFileCts = null;
                        if (skipThisFile)
                        {
                            skippedFiles++;
                            doneWeight += fileWeights[fileIndex];
                            DispatcherQueue.TryEnqueue(() => KbDbStatus.Text = $"已跳过文件 {fileName}");
                            continue;
                        }
                        if (!addOk)
                        {
                            DispatcherQueue.TryEnqueue(() => SetKbDbStatus($"⚠ 文件 {fileName} 入库失败，已跳过\n失败原因：{addError}", isError: true));
                            doneWeight += fileWeights[fileIndex];
                            continue;
                        }
                        sw.Stop();
                        Debug.WriteLine($"[KB] 文件 {fileName}：{text.Length / 1024.0:F1}KB / {chunkCount} 切块，向量化+入库耗时 {sw.Elapsed.TotalSeconds:F1}s");
                        doneWeight += fileWeights[fileIndex];
                        totalChunks += chunkCount;
                        totalFiles++;
                    }
                    return new KbAddDirectoryResult(totalFiles, totalChunks, skippedFiles);
                });

                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (result.Files == 0)
                    {
                        HideKbAddDirectoryProgress(result.Skipped == 0
                            ? "📂 未新增文档"
                            : $"📂 无新增文档（已处理 {result.Skipped} 个文件被跳过）");
                    }
                    else
                    {
                        HideKbAddDirectoryProgress($"✅ 已扫描并入库 {result.Files} 个文件，共 {result.Chunks} 个片段" +
                            (result.Skipped > 0 ? $"（跳过 {result.Skipped} 个文件）" : ""));
                        await RefreshKbDocsAsync(collection);
                    }
                    SaveKbConfig();
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() => HideKbAddDirectoryProgress("⏹ 已取消扫描"));
                SaveKbConfig();
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => HideKbAddDirectoryProgress("❌ 建立索引失败: " + ex.Message));
                SaveKbConfig();
            }
            finally
            {
                _kbAddDirectoryBusy = false;
                _kbScanCts = null;
                _kbSkipFileCts = null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    KbAddDirectoryProgress.Visibility = Visibility.Collapsed;
                    KbSkipFileBtn.Visibility = Visibility.Collapsed;
                    KbCancelScanBtn.Visibility = Visibility.Collapsed;
                });
            }
        }

        /// <summary>扫描过程中点击「跳过当前文件」</summary>
        private void OnKbSkipCurrentFile(object sender, RoutedEventArgs e)
        {
            _kbSkipFileCts?.Cancel();
        }

        /// <summary>扫描过程中点击「取消」</summary>
        private void OnKbCancelScan(object sender, RoutedEventArgs e)
        {
            _kbScanCts?.Cancel();
        }

        /// <summary>获取内置库已入库的源文件列表；非内置存储返回 null。</summary>
        private async Task<HashSet<string>?> ListKbSourceFilesAsync(string collection)
        {
            if (_kbStore is BuiltInVectorDbService builtIn)
            {
                var list = await builtIn.ListSourceFilesAsync(collection);
                return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
            return null;
        }

        /// <summary>「添加目录」后台任务的返回结果</summary>
        private sealed class KbAddDirectoryResult
        {
            public int Files { get; }
            public int Chunks { get; }
            public int Skipped { get; }
            public KbAddDirectoryResult(int files, int chunks, int skipped) { Files = files; Chunks = chunks; Skipped = skipped; }
        }

        /// <summary>
        /// 按段落或固定长度切分文本为片段。
        /// 段落模式下先按分隔符分段，再对超长段落按 chunkSize 二次切分，保证每个片段不超过限制。
        /// </summary>
        private static List<string> SplitChunks(string text, int chunkSize, bool byParagraph, string separator)
        {
            var result = new List<string>();
            string cleaned = text.Replace("\r\n", "\n");

            if (byParagraph && !string.IsNullOrEmpty(separator))
            {
                string sep = separator.Replace("\\r\\n", "\n").Replace("\\r", "\n").Replace("\\n", "\n");
                foreach (var para in cleaned.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string rest = para;
                    while (rest.Length > chunkSize)
                    {
                        result.Add(rest.Substring(0, chunkSize));
                        rest = rest.Substring(chunkSize);
                    }
                    if (rest.Length > 0)
                        result.Add(rest);
                }
                return result;
            }

            while (cleaned.Length > 0)
            {
                int len = Math.Min(chunkSize, cleaned.Length);
                string chunk = cleaned.Substring(0, len);
                result.Add(chunk);
                cleaned = cleaned.Substring(len);
            }
            return result;
        }

        /// <summary>
        /// 解析用户输入的段落分隔符：支持字面 "\r\n"、"\n"、"\r"、"\t" 转义，空输入回退为单个换行符。
        /// </summary>
        private static string ParseParagraphSeparator(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "\n";
            return raw.Replace("\\r\\n", "\r\n").Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        /// <summary>无向量模型 API 时的本地回退：字符哈希向量（演示用）</summary>
        private static float[] FallbackHashEmbedding(string text, int dim)
        {
            var vec = new float[dim];
            var bytes = System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant());
            for (int i = 0; i < bytes.Length; i++)
            {
                int idx = (bytes[i] * 31 + i) % dim;
                vec[idx] += 1f;
            }
            double norm = 0;
            foreach (var v in vec) norm += v * v;
            norm = Math.Sqrt(norm);
            if (norm > 1e-12)
                for (int i = 0; i < vec.Length; i++) vec[i] = (float)(vec[i] / norm);
            return vec;
        }

        /// <summary>设置知识库状态文本；isError 时用醒目的警示色突出显示。</summary>
        private void SetKbDbStatus(string text, bool isError = false)
        {
            KbDbStatus.Text = text;
            KbDbStatus.Foreground = new SolidColorBrush(isError
                ? Microsoft.UI.Colors.OrangeRed
                : Microsoft.UI.Colors.Gray);
        }

        /// <summary>隐藏"添加目录"进度条</summary>
        private void HideKbAddDirectoryProgress(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (KbAddDirectoryProgress != null && KbAddDirectoryProgress.Visibility == Visibility.Visible)
                {
                    KbAddDirectoryProgress.Value = 1.0;
                    KbDbStatus.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                    KbDbStatus.Text = message;
                    KbAddDirectoryProgress.Visibility = Visibility.Collapsed;
                }
            });
        }

        private async Task RefreshKbDocsAsync(string? collectionName = null)
        {
            if (_kbStore == null || !_kbStore.IsConnected)
            {
                KbDocsList.Items.Clear();
                KbDocCount.Text = "文档列表";
                ResetKbPreview();
                return;
            }
            string name = collectionName ?? GetCurrentCollectionName();
            if (string.IsNullOrEmpty(name)) return;

            var all = await _kbStore.GetAllAsync(name);
            _kbAllChunks = all;

            // 旧数据（未记录 chunk_index）按同源文件分组后的顺序编号，作为段号兜底
            var grpIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in all.GroupBy(GetKbSourceFile, StringComparer.OrdinalIgnoreCase))
            {
                var idxMap = new Dictionary<string, int>();
                int i = 0;
                foreach (var ch in grp.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
                    idxMap[ch.Id] = i++;
                grpIndex[grp.Key] = idxMap;
            }

            KbDocsList.Items.Clear();
            int filteredOut = 0;
            foreach (var c in all)
            {
                // 排除无内容的无效条目（空 content 无法提供任何可读信息）
                if (string.IsNullOrWhiteSpace(c.Content) || string.IsNullOrEmpty(c.Id))
                {
                    filteredOut++;
                    continue;
                }

                string fileName = GetKbSourceFile(c);
                int chunkIndex = 0;
                if (c.Metadata.TryGetValue("chunk_index", out var ci) && int.TryParse(ci, out var idx) && idx >= 0)
                    chunkIndex = idx;
                else if (grpIndex.TryGetValue(fileName, out var im) && im.TryGetValue(c.Id, out var gi))
                    chunkIndex = gi;

                const int maxTitle = 30;
                string title = c.Content.Length <= maxTitle ? c.Content : c.Content[..maxTitle] + "…";

                KbDocsList.Items.Add(new
                {
                    Id = c.Id,
                    Title = title,
                    Preview = $"{fileName} · 第 {chunkIndex + 1} 段 · {c.Content.Length} 字符",
                    FullContent = c.Content,
                    SourceFile = c.SourceFile,
                    ChunkIndex = chunkIndex,
                    FileName = fileName
                });
            }
            long count = await _kbStore.CountAsync(name);
            string filterSuffix = filteredOut > 0 ? $"（已排除 {filteredOut} 条无内容记录）" : "";
            KbDocCount.Text = $"文档列表（{count} 条 · 集合 {name}）{filterSuffix}";
            ResetKbPreview();
        }

        /// <summary>
        /// 从切片元数据中解析源文件名，兼容多种 metadata 键名
        /// </summary>
        private static string GetKbSourceFile(KnowledgeChunk c)
        {
            if (c.Metadata.TryGetValue("source_file", out var sf) && !string.IsNullOrWhiteSpace(sf))
                return sf;
            if (c.Metadata.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p))
                return Path.GetFileName(p);
            if (!string.IsNullOrWhiteSpace(c.SourceFile))
                return c.SourceFile;
            return "(未知来源)";
        }

        /// <summary>重置切片预览为初始状态</summary>
        private void ResetKbPreview()
        {
            _currentDocId = null;
            KbPreviewTitle.Text = "请选择文档以预览切片";
            KbPreviewText.Text = "";
            KbPreviewSource.Text = "—";
            KbPreviewChunkCount.Text = "—";
            KbDeleteCurrentChunk.IsEnabled = false;
        }

        private async void OnKbDocsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KbDocsList.SelectedItem == null)
            {
                ResetKbPreview();
                return;
            }
            var sel = (dynamic)KbDocsList.SelectedItem;
            string id = sel.Id;
            string name = GetCurrentCollectionName();
            if (string.IsNullOrEmpty(name)) return;
            if (_kbStore == null || !_kbStore.IsConnected) return;

            string content, source;
            try
            {
                content = await _kbStore.GetContentAsync(name, id);
                source = await _kbStore.GetSourceFileAsync(name, id);
            }
            catch
            {
                content = (string)sel.FullContent;
                source = (string)(sel.SourceFile ?? "");
            }
            _currentDocId = id;
            KbPreviewTitle.Text = (string)sel.Title;
            KbPreviewText.Text = content;
            KbPreviewSource.Text = string.IsNullOrEmpty(source) ? "—" : source;

            // 同源文件的切片计数：与当前切片共享同一来源 metadata 的条目数
            string? srcKey = null;
            var match = _kbAllChunks.FirstOrDefault(c => c.Id == id);
            if (match != null)
            {
                match.Metadata.TryGetValue("path", out var p1);
                match.Metadata.TryGetValue("source", out var p2);
                srcKey = string.IsNullOrEmpty(p1) ? p2 : p1;
            }
            int total = 1;
            if (!string.IsNullOrEmpty(srcKey))
                total = _kbAllChunks.Count(c =>
                    (((c.Metadata.TryGetValue("path", out var sp) && !string.IsNullOrEmpty(sp)) ? sp.ToLowerInvariant() : null)
                        ?? (c.Metadata.TryGetValue("source", out var ss) ? ss.ToLowerInvariant() : null)) == srcKey.ToLowerInvariant());
            KbPreviewChunkCount.Text = total.ToString();
            KbDeleteCurrentChunk.IsEnabled = true;
        }

        private async void OnKbDeleteCurrentChunk(object sender, RoutedEventArgs e)
        {
            if (_currentDocId == null || KbDocsList.SelectedItem == null)
            {
                await ShowDialogAsync("提示", "请先在列表中选择要删除的切片");
                return;
            }
            string id = _currentDocId;
            string name = GetCurrentCollectionName();
            bool ok = await _kbStore!.DeleteAsync(name, id);
            KbDbStatus.Text = ok ? $"✅ 已删除切片 {id}" : "❌ 删除切片失败";
            KbDocsList.SelectedItem = null;
            await RefreshKbDocsAsync(name);
            SaveKbConfig();
        }

        private async void OnKbDeleteDoc(object sender, RoutedEventArgs e)
        {
            if (KbDocsList.SelectedItem == null)
            {
                await ShowDialogAsync("提示", "请先在列表中选择要删除的文档");
                return;
            }
            var sel = (dynamic)KbDocsList.SelectedItem;
            string id = sel.Id;
            KbDocsList.SelectedItem = null;
            string name = GetCurrentCollectionName();
            bool ok = await _kbStore!.DeleteAsync(name, id);
            KbDbStatus.Text = ok ? $"✅ 已删除文档 {id}" : "❌ 删除失败";
            await RefreshKbDocsAsync(name);
            SaveKbConfig();
        }

        private void OnKbTopKChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (KbTopKText != null) KbTopKText.Text = ((int)e.NewValue).ToString();
        }

        private void OnKbQueryChanged(object sender, TextChangedEventArgs e)
        {
            // 回车时在 OnKbSearch 里处理；这里仅清空结果
        }

        private async void OnKbSearch(object sender, RoutedEventArgs e)
        {
            string query = KbQueryBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                await ShowDialogAsync("提示", "请输入查询内容");
                return;
            }
            bool ok = await EnsureKbStoreConnected();
            if (!ok)
            {
                await ShowDialogAsync("错误", "向量数据库未连接");
                return;
            }
            string collection = GetCurrentCollectionName();
            if (string.IsNullOrEmpty(collection))
            {
                await ShowDialogAsync("提示", "请先选择集合");
                return;
            }

            int topK = (int)KbTopKSlider.Value;
            KbResultSummary.Text = "检索中...";
            var store = _kbStore!;
            try
            {
                float[] queryVec;
                try
                {
                    queryVec = await _kbEmbedding.EmbedAsync(query, _kbConfig);
                }
                catch
                {
                    queryVec = FallbackHashEmbedding(query, _kbConfig.Dimension);
                }

                var results = await store.SearchAsync(collection, queryVec, topK);
                KbResultsList.Items.Clear();
                if (results.Count == 0)
                {
                    KbResultSummary.Text = "未找到相似内容";
                    return;
                }
                foreach (var r in results)
                {
                    var title = r.Content.Length > 80 ? r.Content[..80] : r.Content;
                    KbResultsList.Items.Add(new
                    {
                        Title = title,
                        Score = $"相似度 {r.Score:F4}",
                        Preview = r.Content.Length > 200 ? r.Content[..200] : r.Content
                    });
                }
                KbResultSummary.Text = $"找到 {results.Count} 条结果（Top-{topK}）";
            }
            catch (Exception ex)
            {
                KbResultSummary.Text = "❌ 检索失败: " + ex.Message;
            }
        }

        // ==================== AI底座启动集合 ====================

        /// <summary>引擎切换：显示对应的参数面板</summary>
        private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
        {
            // 初始化阶段控件还未创建，跳过
            if (LLamaCPPParams == null) return;

            MarkParamsDirty();

            string? engine = e.AddedItems.Count > 0
                ? (e.AddedItems[0] as ComboBoxItem)?.Content?.ToString()
                : (EngineSelector.SelectedItem as ComboBoxItem)?.Content?.ToString();

            LLamaCPPParams.Visibility = engine == "llama.cpp" ? Visibility.Visible : Visibility.Collapsed;
            VLLMParams.Visibility = engine == "vllm" ? Visibility.Visible : Visibility.Collapsed;
            LMStudioParams.Visibility = engine == "lmstudio" ? Visibility.Visible : Visibility.Collapsed;
            OllamaParams.Visibility = engine == "ollama" ? Visibility.Visible : Visibility.Collapsed;

            // 切换到当前引擎的预设
            ApplyCurrentPreset();
        }

        /// <summary>预设切换</summary>
        private void OnPresetChanged(object sender, RoutedEventArgs e)
        {
            if (LLamaGPULayers == null) return;
            ApplyCurrentPreset();
            MarkParamsDirty();
        }

        /// <summary>获取当前预设名称</summary>
        private string GetCurrentPreset()
        {
            if (PresetRecommended.IsChecked == true) return "推荐";
            if (PresetDefault.IsChecked == true) return "默认";
            if (PresetExtreme.IsChecked == true) return "暴力";
            return "推荐";
        }

        /// <summary>应用当前预设值到参数控件</summary>
        private void ApplyCurrentPreset()
        {
            // 初始化阶段控件还未创建则跳过
            if (LLamaGPULayers == null) return;

            string engine = (EngineSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama.cpp";
            string preset = GetCurrentPreset();

            switch (engine)
            {
                case "llama.cpp": ApplyLLamaPreset(preset); break;
                case "vllm": ApplyVLLMPreset(preset); break;
                case "lmstudio": ApplyLMStudioPreset(preset); break;
                case "ollama": ApplyOllamaPreset(preset); break;
            }
        }

        // ---- llama.cpp 预设 ----
        private void ApplyLLamaPreset(string preset)
        {
            switch (preset)
            {
                case "推荐":
                    LLamaGPULayers.Value = 35; LLamaContextSize.Value = 8192;
                    LLamaNPredict.Value = -1; LLamaThreads.Value = Environment.ProcessorCount;
                    LLamaBatchSize.Value = 2048; LLamaUBatchSize.Value = 512;
                    LLamaParallel.Value = 1;
                    LLamaMLock.IsChecked = true;
                    LLamaMMap.SelectedIndex = 0;
                    LLamaFlashAttn.SelectedIndex = 0;
                    LLamaTemperature.Text = "0.80";
                    LLamaTopK.Text = "40"; LLamaTopP.Text = "0.95"; LLamaMinP.Text = "0.05";
                    LLamaExtraArgs.Text = "--cont-batching";
                    break;
                case "默认":
                    LLamaGPULayers.Value = 0; LLamaContextSize.Value = 4096;
                    LLamaNPredict.Value = -1; LLamaThreads.Value = 4;
                    LLamaBatchSize.Value = 512; LLamaUBatchSize.Value = 512;
                    LLamaParallel.Value = 1;
                    LLamaMLock.IsChecked = false;
                    LLamaMMap.SelectedIndex = 0;
                    LLamaFlashAttn.SelectedIndex = 0;
                    LLamaTemperature.Text = "0.80";
                    LLamaTopK.Text = "40"; LLamaTopP.Text = "0.95"; LLamaMinP.Text = "0.05";
                    LLamaExtraArgs.Text = "";
                    break;
                case "暴力":
                    LLamaGPULayers.Value = 99; LLamaContextSize.Value = 32768;
                    LLamaNPredict.Value = -1; LLamaThreads.Value = Environment.ProcessorCount;
                    LLamaBatchSize.Value = 4096; LLamaUBatchSize.Value = 2048;
                    LLamaParallel.Value = 4;
                    LLamaMLock.IsChecked = true;
                    LLamaMMap.SelectedIndex = 1; // --no-mmap
                    LLamaFlashAttn.SelectedIndex = 1; // on
                    LLamaTemperature.Text = "0.60";
                    LLamaTopK.Text = "20"; LLamaTopP.Text = "0.90"; LLamaMinP.Text = "0.10";
                    LLamaExtraArgs.Text = "--cont-batching --no-warmup";
                    break;
            }
        }

        // ---- vllm 预设 ----
        private void ApplyVLLMPreset(string preset)
        {
            switch (preset)
            {
                case "推荐":
                    VLLMTensorParallel.Value = 1; VLLMMaxLen.Value = 8192;
                    VLLMBatchSize.Value = 128; VLLMQuantization.SelectedIndex = 0;
                    VLLMExtraArgs.Text = "--enforce-eager";
                    break;
                case "默认":
                    VLLMTensorParallel.Value = 1; VLLMMaxLen.Value = 4096;
                    VLLMBatchSize.Value = 32; VLLMQuantization.SelectedIndex = 0;
                    VLLMExtraArgs.Text = "";
                    break;
                case "暴力":
                    VLLMTensorParallel.Value = Math.Min(Environment.ProcessorCount / 2, 8);
                    VLLMMaxLen.Value = 65536; VLLMBatchSize.Value = 512;
                    VLLMQuantization.SelectedIndex = 1; // awq
                    VLLMExtraArgs.Text = "--disable-custom-all-reduce --num-scheduler-steps 16";
                    break;
            }
        }

        // ---- lmstudio 预设 ----
        private void ApplyLMStudioPreset(string preset)
        {
            switch (preset)
            {
                case "推荐":
                    LMStudioGPULayers.Value = 35; LMStudioContextSize.Value = 8192;
                    LMStudioThreads.Value = Environment.ProcessorCount;
                    LMStudioExtraArgs.Text = "--mlock";
                    break;
                case "默认":
                    LMStudioGPULayers.Value = 0; LMStudioContextSize.Value = 4096;
                    LMStudioThreads.Value = 4;
                    LMStudioExtraArgs.Text = "";
                    break;
                case "暴力":
                    LMStudioGPULayers.Value = 99; LMStudioContextSize.Value = 32768;
                    LMStudioThreads.Value = Environment.ProcessorCount;
                    LMStudioExtraArgs.Text = "--mlock --no-mmap";
                    break;
            }
        }

        // ---- ollama 预设 ----
        private void ApplyOllamaPreset(string preset)
        {
            switch (preset)
            {
                case "推荐":
                    OllamaContextSize.Value = 8192; OllamaGPULayers.Value = 35;
                    OllamaBatchSize.Value = 1024;
                    OllamaExtraArgs.Text = "--verbose";
                    break;
                case "默认":
                    OllamaContextSize.Value = 4096; OllamaGPULayers.Value = 0;
                    OllamaBatchSize.Value = 512;
                    OllamaExtraArgs.Text = "";
                    break;
                case "暴力":
                    OllamaContextSize.Value = 65536; OllamaGPULayers.Value = 99;
                    OllamaBatchSize.Value = 4096;
                    OllamaExtraArgs.Text = "--verbose --no-cache";
                    break;
            }
        }

        // ==================== 文件浏览（WinUI3 兼容） ====================

        private async Task<Windows.Storage.StorageFile?> PickFileAsync(string title, string extension)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            return await picker.PickSingleFileAsync();
        }

        // ==================== llama.cpp 参数同步 ====================

        private void OnLLamaValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 初始化阶段关联文本框可能还未创建
            if (LLamaGPULayersText == null) return;
            if (sender is Slider sl)
            {
                if (sl == LLamaGPULayers && LLamaGPULayersText != null) LLamaGPULayersText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaContextSize && LLamaContextSizeText != null) LLamaContextSizeText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaNPredict && LLamaNPredictText != null) LLamaNPredictText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaThreads && LLamaThreadsText != null) LLamaThreadsText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaBatchSize && LLamaBatchSizeText != null) LLamaBatchSizeText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaUBatchSize && LLamaUBatchSizeText != null) LLamaUBatchSizeText.Text = ((int)e.NewValue).ToString();
                else if (sl == LLamaParallel && LLamaParallelText != null) LLamaParallelText.Text = ((int)e.NewValue).ToString();
            }
            MarkParamsDirty();
        }
        private void OnLLamaSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MarkParamsDirty();
        }
        private void OnLLamaGPULayersTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaGPULayersText.Text, out var v) && v >= 0 && v <= 200)
                LLamaGPULayers.Value = v;
        }
        private void OnLLamaContextSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaContextSizeText.Text, out var v) && v >= 512 && v <= 131072)
                LLamaContextSize.Value = v;
        }
        private void OnLLamaNPredictTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaNPredictText.Text, out var v) && v >= -1 && v <= 16384)
                LLamaNPredict.Value = v;
        }
        private void OnLLamaThreadsTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaThreadsText.Text, out var v) && v >= 1 && v <= 64)
                LLamaThreads.Value = v;
        }
        private void OnLLamaBatchSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaBatchSizeText.Text, out var v) && v >= 128 && v <= 8192)
                LLamaBatchSize.Value = v;
        }
        private void OnLLamaUBatchSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaUBatchSizeText.Text, out var v) && v >= 128 && v <= 4096)
                LLamaUBatchSize.Value = v;
        }
        private void OnLLamaParallelTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LLamaParallelText.Text, out var v) && v >= 1 && v <= 16)
                LLamaParallel.Value = v;
        }

        // ==================== vllm 参数同步 ====================

        private void OnVLLMValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (VLLMTensorParallelText == null) return;
            if (sender is Slider sl)
            {
                if (sl == VLLMTensorParallel && VLLMTensorParallelText != null) VLLMTensorParallelText.Text = ((int)e.NewValue).ToString();
                else if (sl == VLLMMaxLen && VLLMMaxLenText != null) VLLMMaxLenText.Text = ((int)e.NewValue).ToString();
                else if (sl == VLLMBatchSize && VLLMBatchSizeText != null) VLLMBatchSizeText.Text = ((int)e.NewValue).ToString();
            }
        }
        private void OnVLLMSelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void OnVLLMTensorParallelTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(VLLMTensorParallelText.Text, out var v) && v >= 1 && v <= 8)
                VLLMTensorParallel.Value = v;
        }
        private void OnVLLMMaxLenTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(VLLMMaxLenText.Text, out var v) && v >= 2048 && v <= 65536)
                VLLMMaxLen.Value = v;
        }
        private void OnVLLMBatchSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(VLLMBatchSizeText.Text, out var v) && v >= 1 && v <= 512)
                VLLMBatchSize.Value = v;
        }

        // ==================== LM Studio 参数同步 ====================

        private void OnLMStudioValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (LMStudioGPULayersText == null) return;
            if (sender is Slider sl)
            {
                if (sl == LMStudioGPULayers && LMStudioGPULayersText != null) LMStudioGPULayersText.Text = ((int)e.NewValue).ToString();
                else if (sl == LMStudioContextSize && LMStudioContextSizeText != null) LMStudioContextSizeText.Text = ((int)e.NewValue).ToString();
                else if (sl == LMStudioThreads && LMStudioThreadsText != null) LMStudioThreadsText.Text = ((int)e.NewValue).ToString();
            }
        }
        private void OnLMStudioGPULayersTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LMStudioGPULayersText.Text, out var v) && v >= 0 && v <= 100)
                LMStudioGPULayers.Value = v;
        }
        private void OnLMStudioContextSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LMStudioContextSizeText.Text, out var v) && v >= 512 && v <= 32768)
                LMStudioContextSize.Value = v;
        }
        private void OnLMStudioThreadsTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(LMStudioThreadsText.Text, out var v) && v >= 1 && v <= 64)
                LMStudioThreads.Value = v;
        }

        // ==================== Ollama 参数同步 ====================

        private void OnOllamaValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (OllamaContextSizeText == null) return;
            if (sender is Slider sl)
            {
                if (sl == OllamaContextSize && OllamaContextSizeText != null) OllamaContextSizeText.Text = ((int)e.NewValue).ToString();
                else if (sl == OllamaGPULayers && OllamaGPULayersText != null) OllamaGPULayersText.Text = ((int)e.NewValue).ToString();
                else if (sl == OllamaBatchSize && OllamaBatchSizeText != null) OllamaBatchSizeText.Text = ((int)e.NewValue).ToString();
            }
        }
        private void OnOllamaContextSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(OllamaContextSizeText.Text, out var v) && v >= 512 && v <= 65536)
                OllamaContextSize.Value = v;
        }
        private void OnOllamaGPULayersTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(OllamaGPULayersText.Text, out var v) && v >= 0 && v <= 100)
                OllamaGPULayers.Value = v;
        }
        private void OnOllamaBatchSizeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(OllamaBatchSizeText.Text, out var v) && v >= 1 && v <= 4096)
                OllamaBatchSize.Value = v;
        }

        // ==================== 运行引擎 ====================

        private async void OnRunEngineClick(object sender, RoutedEventArgs e)
        {
            if (_runningProcess != null && !_runningProcess.HasExited)
            {
                await ShowDialogAsync("提示", "已有引擎在运行，请先停止再启动");
                return;
            }

            // 运行前保存所有参数
            _saveTimer?.Stop();
            SaveAllParams();

            string engine = (EngineSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama.cpp";

            // 显示日志区域
            RunLogTitle.Visibility = Visibility.Visible;
            RunLog.Visibility = Visibility.Visible;
            RunLog.Text = $"[{DateTime.Now:HH:mm:ss}] 正在准备启动 {engine}...\n";
            RunEngineBtn.Content = "⏹ 停止";
            RunEngineBtn.Click -= OnRunEngineClick;
            RunEngineBtn.Click += OnStopEngineClick;

            ModelSubDirSelector.IsEnabled = false;

            try
            {
                switch (engine)
                {
                    case "llama.cpp": await RunLLamaCPP(); break;
                    case "vllm": await RunVLLM(); break;
                    case "lmstudio": await RunLMStudio(); break;
                    case "ollama": await RunOllama(); break;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"错误: {ex.Message}");
                ResetRunButton();
            }
        }

        private void OnStopEngineClick(object sender, RoutedEventArgs e)
        {
            if (_runningProcess != null && !_runningProcess.HasExited)
            {
                _runningProcess.Kill();
                _runningProcess.WaitForExit(5000);
                _runningProcess = null;
                AppendLog("已手动停止引擎");
            }
            ResetRunButton();
        }

        private void ResetRunButton()
        {
            RunEngineBtn.Content = "▶ 运行";
            RunEngineBtn.Click -= OnStopEngineClick;
            RunEngineBtn.Click += OnRunEngineClick;
            ModelSubDirSelector.IsEnabled = true;
        }

        private void AppendLog(string text)
        {
            RunLog.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
            RunLog.Select(RunLog.Text.Length, 0); // 滚动到底部
        }

        // ---- llama.cpp 运行 ----
        private async Task RunLLamaCPP()
        {
            // 从统一模型目录获取模型路径
            string? modelPath = GetCurrentModelPath();
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                await ShowDialogAsync("错误", "请先在顶部「模型目录」中选择包含 .gguf 文件的子目录");
                ResetRunButton();
                return;
            }

            // 查找 llama.cpp 可执行文件（优先使用本引擎的工作目录）
            string mainExe = FindLLamaExe();
            if (string.IsNullOrEmpty(mainExe))
            {
                await ShowDialogAsync("错误", "未找到 llama.cpp 可执行文件，请设置本引擎的「启动器工作目录」");
                ResetRunButton();
                return;
            }

            var argsList = new System.Collections.Generic.List<string>();

            // ---- 模型路径 ----
            argsList.Add($"-m \"{modelPath}\"");

            // ---- 多模态投影（若用户已确认启用） ----
            if (_multimodalEnabled && !string.IsNullOrEmpty(_mmprojFilePath) && File.Exists(_mmprojFilePath))
            {
                argsList.Add($"--mmproj \"{_mmprojFilePath}\"");
                AppendLog("多模态已启用，添加 --mmproj 参数");
            }

            // ---- 基础参数 ----
            argsList.Add($"-ngl {(int)LLamaGPULayers.Value}");
            argsList.Add($"-c {(int)LLamaContextSize.Value}");
            argsList.Add($"-n {(int)LLamaNPredict.Value}");
            argsList.Add($"-t {(int)LLamaThreads.Value}");
            argsList.Add($"-b {(int)LLamaBatchSize.Value}");
            argsList.Add($"--ubatch-size {(int)LLamaUBatchSize.Value}");
            argsList.Add($"-np {(int)LLamaParallel.Value}");

            // ---- 内存与 IO ----
            if (LLamaMLock.IsChecked == true)
                argsList.Add("--mlock");
            string mmap = (LLamaMMap.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (mmap.Contains("禁用")) argsList.Add("--no-mmap");
            string fa = (LLamaFlashAttn.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
            if (fa != "auto（默认）") argsList.Add($"--flash-attn {fa.Split('（')[0]}");
            string numa = (LLamaNuma.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (!numa.Contains("禁用")) argsList.Add($"--numa {numa}");
            string ctk = (LLamaCacheTypeK.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "f16（默认）";
            if (!ctk.Contains("默认")) argsList.Add($"-ctk {ctk.Split('（')[0]}");
            string ctv = (LLamaCacheTypeV.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "f16（默认）";
            if (!ctv.Contains("默认")) argsList.Add($"-ctv {ctv.Split('（')[0]}");

            // ---- GPU 与设备 ----
            string sm = (LLamaSplitMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (!sm.Contains("layer（默认")) argsList.Add($"-sm {sm.Split('（')[0]}");
            string mg = LLamaMainGPU.Text.Trim();
            if (!string.IsNullOrEmpty(mg) && mg != "0") argsList.Add($"-mg {mg}");
            string dev = LLamaDevice.Text.Trim();
            if (!string.IsNullOrEmpty(dev)) argsList.Add($"-dev {dev}");
            string ts = LLamaTensorSplit.Text.Trim();
            if (!string.IsNullOrEmpty(ts)) argsList.Add($"-ts {ts}");

            // ---- 采样参数 ----
            string temp = LLamaTemperature.Text.Trim();
            if (!string.IsNullOrEmpty(temp) && temp != "0.80") argsList.Add($"--temp {temp}");
            string topk = LLamaTopK.Text.Trim();
            if (!string.IsNullOrEmpty(topk) && topk != "40") argsList.Add($"--top-k {topk}");
            string topp = LLamaTopP.Text.Trim();
            if (!string.IsNullOrEmpty(topp) && topp != "0.95") argsList.Add($"--top-p {topp}");
            string minp = LLamaMinP.Text.Trim();
            if (!string.IsNullOrEmpty(minp) && minp != "0.05") argsList.Add($"--min-p {minp}");
            string rp = LLamaRepeatPenalty.Text.Trim();
            if (!string.IsNullOrEmpty(rp) && rp != "1.00") argsList.Add($"--repeat-penalty {rp}");
            string pp = LLamaPresencePenalty.Text.Trim();
            if (!string.IsNullOrEmpty(pp) && pp != "0.00") argsList.Add($"--presence-penalty {pp}");
            string fp = LLamaFrequencyPenalty.Text.Trim();
            if (!string.IsNullOrEmpty(fp) && fp != "0.00") argsList.Add($"--frequency-penalty {fp}");

            int miro = LLamaMirostat.SelectedIndex;
            if (miro > 0) argsList.Add($"--mirostat {miro}");
            string miroLR = LLamaMirostatLR.Text.Trim();
            if (!string.IsNullOrEmpty(miroLR)) argsList.Add($"--mirostat-lr {miroLR}");
            string miroEnt = LLamaMirostatEnt.Text.Trim();
            if (!string.IsNullOrEmpty(miroEnt)) argsList.Add($"--mirostat-ent {miroEnt}");

            string seed = LLamaSeed.Text.Trim();
            if (!string.IsNullOrEmpty(seed) && seed != "-1") argsList.Add($"-s {seed}");

            // ---- 推测解码 ----
            string spec = (LLamaSpecType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (!spec.Contains("none")) argsList.Add($"--spec-type {spec.Split('（')[0].Trim()}");

            // ---- 额外参数 ----
            string extra = LLamaExtraArgs.Text.Trim();
            if (!string.IsNullOrEmpty(extra)) argsList.Add(extra);

            // ---- 判断启动的是 server 还是 cli ----
            bool isServer = Path.GetFileName(mainExe).ToLowerInvariant().Contains("server");
            if (isServer)
            {
                // 从控件读取 host 和 port，如果额外参数里已指定则不覆盖
                string host = string.IsNullOrWhiteSpace(LLamaHost.Text) ? "127.0.0.1" : LLamaHost.Text.Trim();
                string port = string.IsNullOrWhiteSpace(LLamaPort.Text) ? "8080" : LLamaPort.Text.Trim();
                if (!argsList.Exists(a => a.StartsWith("--host", StringComparison.OrdinalIgnoreCase)))
                    argsList.Add($"--host {host}");
                if (!argsList.Exists(a => a.StartsWith("--port", StringComparison.OrdinalIgnoreCase)))
                    argsList.Add($"--port {port}");
            }

            string args = string.Join(" ", argsList);

            // ---- 进程级 GPU 可见性过滤（HIP_VISIBLE_DEVICES） ----
            string visibleTag = (LLamaVisibleGPU.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            Dictionary<string, string>? env = null;
            if (!string.IsNullOrWhiteSpace(visibleTag))
            {
                // 多值（逗号分隔）保留原样，如 "1,2"
                string hipValue = string.Join(",", visibleTag.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => int.TryParse(s, out _)));
                if (!string.IsNullOrEmpty(hipValue))
                {
                    env = new Dictionary<string, string> { ["HIP_VISIBLE_DEVICES"] = hipValue };
                    AppendLog($"🎯 可见显卡: 仅 HIP_VISIBLE_DEVICES={hipValue}（仅作用于本次启动的进程，进程内序号将重排从 0 开始）");
                }
            }

            AppendLog($"程序: {mainExe}");
            AppendLog($"参数: {args}");
            if (isServer)
            {
                string host = string.IsNullOrWhiteSpace(LLamaHost.Text) ? "127.0.0.1" : LLamaHost.Text.Trim();
                string port = string.IsNullOrWhiteSpace(LLamaPort.Text) ? "8080" : LLamaPort.Text.Trim();
                AppendLog($"🌐 浏览器访问: http://{host}:{port}");
            }

            await StartProcessAsync(mainExe, args, env);
        }

        private string FindLLamaExe()
        {
            // 1. 优先在本引擎的启动器工作目录中搜索（server 优先）
            string workDir = LLamaLauncherDir.Text.Trim();
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            {
                string[] workDirExes = { "llama-server.exe", "llama-cli.exe", "main.exe" };
                foreach (var exe in workDirExes)
                {
                    string full = Path.Combine(workDir, exe);
                    if (File.Exists(full)) return full;
                }
            }

            // 2. 常见安装路径（server 优先）
            string[] candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "llama-server.exe"),
                Path.Combine(Environment.CurrentDirectory, "llama-cli.exe"),
                Path.Combine(Environment.CurrentDirectory, "main.exe"),
                "llama-server.exe", "llama-cli.exe", "main.exe",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // 3. 环境变量 PATH 搜索（server 优先）
            foreach (var dir in Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>())
            {
                var full = Path.Combine(dir.Trim(), "llama-server.exe");
                if (File.Exists(full)) return full;
                full = Path.Combine(dir.Trim(), "llama-cli.exe");
                if (File.Exists(full)) return full;
            }
            return string.Empty;
        }

        // ---- llama.cpp 可见 GPU（HIP_VISIBLE_DEVICES 过滤） ----

        private bool _visibleGpuPopulating;
        private bool _visibleGpuUILoaded;
        private bool _visibleGpuAutoProbeDone;

        /// <summary>解析 --list-devices 输出，返回 (总线前缀, 索引, 名称)</summary>
        private async Task<List<(string Bus, int Index, string Name)>> DetectLlamaGpusAsync(string exe)
        {
            var result = new List<(string, int, string)>();
            string output = await RunAndCaptureAsync(exe, "--list-devices", 15000);

            bool primaryMatched = false;
            foreach (var raw in output.Split('\n'))
            {
                string line = raw.Trim();
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?<bus>[A-Za-z_][A-Za-z0-9_]*?)(?<idx>\d+):\s*(?<name>.+)$");
                if (!m.Success) continue;
                string name = StripDeviceMemSuffix(m.Groups["name"].Value.Trim());
                result.Add((m.Groups["bus"].Value, int.Parse(m.Groups["idx"].Value), name));
                primaryMatched = true;
            }

            // 兜底：某些构建输出形如 "Device 0: ..."
            if (!primaryMatched)
            {
                foreach (var raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^Device\s*(?<idx>\d+):\s*(?<name>.+)$");
                    if (!m.Success) continue;
                    result.Add(("GPU", int.Parse(m.Groups["idx"].Value), StripDeviceMemSuffix(m.Groups["name"].Value.Trim())));
                }
            }
            return result;
        }

        private static string StripDeviceMemSuffix(string name)
        {
            int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) return name.Substring(0, paren).Trim();
            return name;
        }

        /// <summary>从配置读取上次保存的可见 GPU（物理序号，空=全部）</summary>
        private string GetSavedVisibleGPU()
        {
            if (_config != null && _config.EngineParams.TryGetValue("LLamaVisibleGPU", out var v))
                return (v ?? "").Trim();
            return "";
        }

        /// <summary>重建下拉项并恢复选择</summary>
        private void PopulateVisibleGPUList(List<(string Bus, int Index, string Name)> gpus, string selected)
        {
            _visibleGpuPopulating = true;
            try
            {
                LLamaVisibleGPU.Items.Clear();
                LLamaVisibleGPU.Items.Add(new ComboBoxItem { Content = "全部显卡（默认，不设过滤）", Tag = "" });

                foreach (var g in gpus)
                    LLamaVisibleGPU.Items.Add(new ComboBoxItem { Content = $"仅 {g.Bus}{g.Index}: {g.Name}", Tag = g.Index.ToString() });

                if (gpus.Count == 0)
                    LLamaVisibleGPU.Items.Add(new ComboBoxItem { Content = "（未探测到显卡，可检查启动器目录）", Tag = "" });

                int sel = 0;
                if (!string.IsNullOrEmpty(selected))
                {
                    for (int i = 1; i < LLamaVisibleGPU.Items.Count; i++)
                    {
                        if ((LLamaVisibleGPU.Items[i] as ComboBoxItem)?.Tag?.ToString() == selected) { sel = i; break; }
                    }
                    if (sel == 0)
                    {
                        LLamaVisibleGPU.Items.Add(new ComboBoxItem { Content = $"仅 GPU {selected}（上次选择，当前未探测到）", Tag = selected });
                        sel = LLamaVisibleGPU.Items.Count - 1;
                    }
                }
                LLamaVisibleGPU.SelectedIndex = sel;
                UpdateVisibleGPUTip();
            }
            finally
            {
                _visibleGpuPopulating = false;
                _visibleGpuUILoaded = true;
            }
        }

        private void UpdateVisibleGPUTip()
        {
            if (LLamaVisibleGPUTip == null) return;
            string tag = (LLamaVisibleGPU.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(tag))
                LLamaVisibleGPUTip.Visibility = Visibility.Collapsed;
            else
            {
                LLamaVisibleGPUTip.Text = "提示：选单卡后该卡在进程内重排为 device 0，-mg/-dev 建议填 0 或留空。";
                LLamaVisibleGPUTip.Visibility = Visibility.Visible;
            }
        }

        private void ShowVisibleGPUTip(string message)
        {
            if (LLamaVisibleGPUTip == null) return;
            LLamaVisibleGPUTip.Text = message;
            LLamaVisibleGPUTip.Visibility = Visibility.Visible;
        }

        private async void OnProbeLLamaGPUs(object sender, RoutedEventArgs e)
        {
            string exe = FindLLamaExe();
            if (string.IsNullOrEmpty(exe))
            {
                ShowVisibleGPUTip("⚠️ 未找到 llama.cpp 可执行文件，请先在「启动器工作目录」设置正确目录");
                await ShowDialogAsync("提示", "未找到 llama.cpp 可执行文件，请先在「启动器工作目录」设置正确目录");
                return;
            }

            LLamaProbeGPUsBtn.IsEnabled = false;
            LLamaProbeGPUsBtn.Content = "⏳ 探测中...";
            try
            {
                // 放到后台线程执行，避免阻塞 UI；内部会调用 llama-server.exe --list-devices
                var gpus = await Task.Run(() => DetectLlamaGpusAsync(exe));
                PopulateVisibleGPUList(gpus, GetSavedVisibleGPU());

                if (gpus.Count == 0)
                    ShowVisibleGPUTip("⚠️ 未探测到可用显卡（--list-devices 无有效输出），请检查启动器目录是否为正确的 llama.cpp 构建");
                else
                {
                    UpdateVisibleGPUTip();
                    // 展开下拉，让用户立即看到探测到的显卡
                    LLamaVisibleGPU.IsDropDownOpen = true;
                }
            }
            catch (Exception ex)
            {
                ShowVisibleGPUTip($"⚠️ 探测失败: {ex.Message}");
                await ShowDialogAsync("探测失败", ex.Message);
            }
            finally
            {
                LLamaProbeGPUsBtn.IsEnabled = true;
                LLamaProbeGPUsBtn.Content = "🔍 探测显卡";
            }
        }

        private void OnLLamaVisibleGPUChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateVisibleGPUTip();
            if (_visibleGpuPopulating) return;
            MarkParamsDirty();
        }

        /// <summary>进入 AI底座页面时自动探测一次（失败静默，可手动点按钮）</summary>
        private async void TryAutoProbeVisibleGPUs()
        {
            if (_visibleGpuAutoProbeDone) return;
            string exe = FindLLamaExe();
            if (string.IsNullOrEmpty(exe)) return;
            _visibleGpuAutoProbeDone = true;
            try
            {
                var gpus = await Task.Run(() => DetectLlamaGpusAsync(exe));
                PopulateVisibleGPUList(gpus, GetSavedVisibleGPU());
            }
            catch
            {
                _visibleGpuAutoProbeDone = false;
            }
        }

        // ==================== llama.cpp 环境检测 ====================

        // ---- vllm 运行 ----
        private async Task RunVLLM()
        {
            // 从统一模型目录获取模型路径
            string? modelPath = GetCurrentModelPath();
            if (string.IsNullOrEmpty(modelPath))
            {
                await ShowDialogAsync("错误", "请先在顶部「模型目录」中选择模型子目录");
                ResetRunButton();
                return;
            }
            string model = modelPath;

            int tp = (int)VLLMTensorParallel.Value;
            int maxLen = (int)VLLMMaxLen.Value;
            int batch = (int)VLLMBatchSize.Value;
            string quant = (VLLMQuantization.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "none";
            string extra = VLLMExtraArgs.Text.Trim();

            string args = $"{model} --tensor-parallel-size {tp} --max-model-len {maxLen} --max-batch-size {batch}";
            if (quant != "none") args += $" --quantization {quant}";
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;

            AppendLog("启动 vLLM（需要 Python 环境）");
            AppendLog($"命令: python -m vllm.entrypoints.openai.api_server {args}");

            await StartProcessAsync("python", $"-m vllm.entrypoints.openai.api_server {args}");
        }

        // ---- LM Studio 运行 ----
        private async Task RunLMStudio()
        {
            // 从统一模型目录获取模型路径
            string? modelPath = GetCurrentModelPath();
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                await ShowDialogAsync("错误", "请先在顶部「模型目录」中选择包含 .gguf 文件的子目录");
                ResetRunButton();
                return;
            }

            // 查找 LM Studio 安装路径（优先使用本引擎的工作目录）
            string lmStudioPath = FindLMStudioExe();
            if (string.IsNullOrEmpty(lmStudioPath))
            {
                await ShowDialogAsync("错误", "未找到 LM Studio 可执行文件，请设置本引擎的「启动器工作目录」");
                ResetRunButton();
                return;
            }

            int ngl = (int)LMStudioGPULayers.Value;
            int ctx = (int)LMStudioContextSize.Value;
            int threads = (int)LMStudioThreads.Value;
            string extra = LMStudioExtraArgs.Text.Trim();

            string args = $"--model \"{modelPath}\" -ngl {ngl} -c {ctx} -t {threads}";
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;

            AppendLog($"程序: {lmStudioPath}");
            AppendLog($"参数: {args}");

            await StartProcessAsync(lmStudioPath, args);
        }

        private string FindLMStudioExe()
        {
            // 1. 优先在本引擎的启动器工作目录中搜索
            string workDir = LMStudioLauncherDir.Text.Trim();
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            {
                string[] workDirExes = { "LM Studio.exe", "lmstudio.exe" };
                foreach (var exe in workDirExes)
                {
                    string full = Path.Combine(workDir, exe);
                    if (File.Exists(full)) return full;
                }
            }

            // 2. 常见安装路径
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                Path.Combine(localAppData, "LM Studio", "LM Studio.exe"),
                Path.Combine(localAppData, "Programs", "LM Studio", "LM Studio.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LM Studio", "LM Studio.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return string.Empty;
        }

        // ---- Ollama 运行 ----
        private async Task RunOllama()
        {
            // Ollama 使用模型名称而非路径，从子目录名称推断模型名
            string? modelDir = null;
            if (ModelSubDirSelector.SelectedItem is ComboBoxItem item && item.Tag is string dirPath)
                modelDir = Path.GetFileName(dirPath);
            string model = modelDir ?? "llama2";
            if (string.IsNullOrEmpty(model))
            {
                await ShowDialogAsync("错误", "请先在顶部「模型目录」中选择模型子目录");
                ResetRunButton();
                return;
            }

            int ctx = (int)OllamaContextSize.Value;
            int ngl = (int)OllamaGPULayers.Value;
            int batch = (int)OllamaBatchSize.Value;
            string extra = OllamaExtraArgs.Text.Trim();

            // 先确保模型已拉取
            AppendLog($"确保模型 {model} 已下载...");
            string pullArgs = $"pull {model}";
            AppendLog($"运行: ollama {pullArgs}");

            // 启动 ollama serve + run
            string runArgs = $"run {model} --num-ctx {ctx} --num-gpu-layers {ngl} --num-batch {batch}";
            if (!string.IsNullOrEmpty(extra)) runArgs += " " + extra;

            AppendLog($"启动: ollama {runArgs}");

            // Ollama 通过子进程运行
            string ollamaExe = FindOllamaExe();
            if (string.IsNullOrEmpty(ollamaExe))
            {
                await ShowDialogAsync("错误", "未找到 ollama.exe，请确认已安装（通常位于 %LOCALAPPDATA%\\Ollama）");
                ResetRunButton();
                return;
            }

            await StartProcessAsync(ollamaExe, runArgs);
        }

        private string FindOllamaExe()
        {
            // 1. 优先在本引擎的启动器工作目录中搜索
            string workDir = OllamaLauncherDir.Text.Trim();
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            {
                string full = Path.Combine(workDir, "ollama.exe");
                if (File.Exists(full)) return full;
            }

            // 2. 常见安装路径
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                Path.Combine(localAppData, "Ollama", "ollama.exe"),
                "ollama.exe"
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // 3. PATH 环境变量搜索
            foreach (var dir in Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>())
            {
                var full = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(full)) return full;
            }
            return string.Empty;
        }

        // ---- 通用进程启动 ----
        private async Task StartProcessAsync(string fileName, string arguments, Dictionary<string, string>? env = null)
        {
            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // 注入进程级环境变量（如 HIP_VISIBLE_DEVICES），避免影响父进程
                    if (env != null)
                    {
                        foreach (var kv in env)
                        {
                            if (string.IsNullOrEmpty(kv.Value))
                                psi.Environment.Remove(kv.Key);
                            else
                                psi.Environment[kv.Key] = kv.Value;
                        }
                    }

                    var process = new Process { StartInfo = psi };
                    _runningProcess = process;
                    _engineStartTime = DateTime.Now;
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            _ = DispatcherQueue.TryEnqueue(() => AppendLog(e.Data));
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            _ = DispatcherQueue.TryEnqueue(() => AppendLog($"[ERR] {e.Data}"));
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var pid = process.Id;
                    _ = DispatcherQueue.TryEnqueue(() => AppendLog($"进程已启动 (PID: {pid})"));

                    process.WaitForExit();

                    double elapsedSec = (DateTime.Now - _engineStartTime).TotalSeconds;
                    try { _metricsService?.AddEngineRunTime(elapsedSec); } catch { }

                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        int exitCode = process.HasExited ? process.ExitCode : -1;
                        AppendLog($"进程已退出 (ExitCode: {exitCode})");
                        if (_runningProcess == process)
                        {
                            _runningProcess = null;
                            ResetRunButton();
                        }
                    });
                }
                catch (Exception ex)
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        AppendLog($"启动失败: {ex.Message}");
                        _runningProcess = null;
                        ResetRunButton();
                    });
                }
            });
        }

        // ==================== 通用工具方法 ====================

        /// <summary>运行命令行并捕获输出（stdout + stderr）</summary>
        private async Task<string> RunAndCaptureAsync(string fileName, string arguments, int timeoutMs = 10000)
        {
            var tcs = new TaskCompletionSource<string>();
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = new Process { StartInfo = psi };
            var sb = new System.Text.StringBuilder();

            proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return sb.ToString() + "\n(命令执行超时，已强制终止)";
            }
            // 确保异步读取完成
            await Task.Delay(200);
            return sb.ToString();
        }

        // ==================== 通用对话框（WinUI3 兼容） ====================

        private async Task ShowDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        /// <summary>显示是/否对话框，返回 true=是, false=否, null=取消</summary>
        private async Task<bool?> ShowYesNoDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "是",
                CloseButtonText = "否",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        // ==================== AI梦工厂 ====================

        private AIDreamFactoryService? _dreamFactoryService;
        private TrayIconHelper? _trayHelper;
        private DreamFactoryConfig _dreamConfig = new();
        private int _toolsPage = 0;
        private DispatcherTimer? _notificationTimer;
        private DispatcherTimer? _genericNotificationTimer;
        private bool _isLoadingDreamConfig;
        private volatile bool _isClosing;

        private void LoadDreamFactoryConfig()
        {
            try
            {
                string configPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(ConfigManager.GetConfigFilePath()) ?? AppContext.BaseDirectory,
                    "dream_factory_config.json");
                if (System.IO.File.Exists(configPath))
                {
                    var json = System.IO.File.ReadAllText(configPath);
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<DreamFactoryConfig>(json);
                    if (cfg != null) _dreamConfig = cfg;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"加载AI梦工厂配置失败: {ex.Message}"); }
            C99.Services.AIDreamFactoryService.EnsureFileSearchTool(_dreamConfig);
            _isLoadingDreamConfig = true;
            ApplyDreamConfigToUI();
            _isLoadingDreamConfig = false;
        }

        private void SaveDreamFactoryConfig()
        {
            if (_isLoadingDreamConfig) return;
            try
            {
                string configPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(ConfigManager.GetConfigFilePath()) ?? AppContext.BaseDirectory,
                    "dream_factory_config.json");
                string? dir = System.IO.Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                UpdateDreamConfigFromUI();
                var json = System.Text.Json.JsonSerializer.Serialize(_dreamConfig,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(configPath, json);
                Debug.WriteLine($"AI梦工厂配置已保存到: {configPath}");
            }
            catch (Exception ex) { Debug.WriteLine($"保存AI梦工厂配置失败: {ex.Message}"); }
        }

        private void ApplyDreamConfigToUI()
        {
            DreamFactoryPort.Text = _dreamConfig.Port.ToString();
            // 恢复当前工作流模式（主流程 / 知识库检索流程）
            ApplyWorkflowModeButtons();

            foreach (ComboBoxItem item in DreamFactoryModelSource.Items)
            {
                if (item.Tag?.ToString() == _dreamConfig.ModelSource)
                { DreamFactoryModelSource.SelectedItem = item; break; }
            }

            DreamFactoryBuiltInPanel.Visibility = _dreamConfig.ModelSource == "BuiltIn"
                ? Visibility.Visible : Visibility.Collapsed;
            DreamFactoryCustomPanel.Visibility = _dreamConfig.ModelSource == "Custom"
                ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(_dreamConfig.BuiltInModel))
            {
                foreach (ComboBoxItem item in DreamFactoryBuiltInModel.Items)
                {
                    if (item.Content?.ToString()?.StartsWith(_dreamConfig.BuiltInModel) == true)
                    { DreamFactoryBuiltInModel.SelectedItem = item; break; }
                }
            }

            ScanBuiltInModelFiles();
            if (!string.IsNullOrEmpty(_dreamConfig.BuiltInModelFile))
            {
                foreach (ComboBoxItem item in DreamFactoryBuiltInModelFile.Items)
                {
                    if (item.Content?.ToString() == _dreamConfig.BuiltInModelFile ||
                        item.Tag?.ToString() == _dreamConfig.BuiltInModelFile)
                    { DreamFactoryBuiltInModelFile.SelectedItem = item; break; }
                }
            }

            // 自定义外部模型 → 关联设置页面的配置
            string extUrl = _config.ExternalLLMApiUrl;
            string extKey = _config.ExternalLLMApiKey;
            DreamFactoryCustomInfo.Text = string.IsNullOrEmpty(extUrl)
                ? "请在「设置」页面配置 API 地址和 Key"
                : $"API: {extUrl}  |  Key: {(string.IsNullOrEmpty(extKey) ? "(未设置)" : new string('*', Math.Min(extKey.Length, 16)))}";

            PopulateCustomModelCombo();

            DreamFactoryPrompt.Text = _dreamConfig.GetEffectiveSystemPrompt();
            DreamFactoryWorkflowName.Text = _dreamConfig.GetWorkflowName(_dreamConfig.CurrentWorkflowMode);

            foreach (ComboBoxItem item in DreamFactoryEncoding.Items)
            {
                if (item.Tag?.ToString() == _dreamConfig.Base64Encoding)
                { DreamFactoryEncoding.SelectedItem = item; break; }
            }

            DreamFactoryMaxTokens.Value = _dreamConfig.MaxTokens;
            DreamFactoryMaxTokensText.Text = _dreamConfig.MaxTokens.ToString();

            UpdateDreamFactoryStatusUI();
        }

        private void PopulateCustomModelCombo()
        {
            if (DreamFactoryCustomModel == null) return;
            DreamFactoryCustomModel.Items.Clear();
            var models = _config.ExternalLLMAvailableModels;

            // 确保已保存的模型名在列表中（即使缓存为空或不在缓存中）
            var displayList = new List<string>(models);
            string saved = _dreamConfig.CustomModelName;
            if (!string.IsNullOrEmpty(saved) && !displayList.Contains(saved))
                displayList.Insert(0, saved);

            int selectIdx = -1;
            for (int i = 0; i < displayList.Count; i++)
            {
                var item = new ComboBoxItem { Content = displayList[i], Tag = displayList[i] };
                DreamFactoryCustomModel.Items.Add(item);
                if (displayList[i] == saved)
                    selectIdx = i;
            }
            if (selectIdx >= 0) DreamFactoryCustomModel.SelectedIndex = selectIdx;
            DreamFactoryCustomModel.PlaceholderText = displayList.Count > 0 ? $"共 {displayList.Count} 个模型" : "请先在设置中获取模型列表...";
        }

        private void UpdateDreamConfigFromUI()
        {
            if (DreamFactoryPort == null || DreamFactoryWorkflowName == null) return;
            if (int.TryParse(DreamFactoryPort.Text, out int port) && port > 0 && port < 65536)
                _dreamConfig.Port = port;

            // 按当前模式保存对应的工作流名称
            if (_dreamConfig.CurrentWorkflowMode == DreamWorkflowMode.KnowledgeBase)
                _dreamConfig.CurrentWorkflowKb = DreamFactoryWorkflowName.Text.Trim();
            else
                _dreamConfig.CurrentWorkflow = DreamFactoryWorkflowName.Text.Trim();

            if (DreamFactoryModelSource?.SelectedItem is ComboBoxItem srcItem)
                _dreamConfig.ModelSource = srcItem.Tag?.ToString() ?? "BuiltIn";

            if (DreamFactoryBuiltInModel?.SelectedItem is ComboBoxItem modelItem)
                _dreamConfig.BuiltInModel = modelItem.Content?.ToString()?.Split(" (")[0] ?? "Local llama.cpp";

            if (DreamFactoryBuiltInModelFile?.SelectedItem is ComboBoxItem fileItem)
                _dreamConfig.BuiltInModelFile = fileItem.Tag?.ToString() ?? fileItem.Content?.ToString() ?? "";
            else if (!string.IsNullOrEmpty(DreamFactoryBuiltInModelFile?.Text))
                _dreamConfig.BuiltInModelFile = DreamFactoryBuiltInModelFile.Text.Trim();

            if (DreamFactoryCustomModel?.SelectedItem is ComboBoxItem customItem)
                _dreamConfig.CustomModelName = customItem.Tag?.ToString() ?? customItem.Content?.ToString() ?? "";

            // 按当前模式保存对应的 System Prompt
            if (_dreamConfig.CurrentWorkflowMode == DreamWorkflowMode.KnowledgeBase)
                _dreamConfig.SystemPromptKb = DreamFactoryPrompt?.Text ?? "";
            else
                _dreamConfig.SystemPrompt = DreamFactoryPrompt?.Text ?? "";

            if (DreamFactoryEncoding?.SelectedItem is ComboBoxItem encItem)
                _dreamConfig.Base64Encoding = encItem.Tag?.ToString() ?? "auto";

            _dreamConfig.MaxTokens = (int)DreamFactoryMaxTokens.Value;
        }

        private void UpdateDreamFactoryStatusUI()
        {
            bool running = _dreamFactoryService?.IsRunning == true;
            DreamFactoryStatus.Text = running ? "● 运行中" : "● 未启动";
            DreamFactoryStatus.Foreground = running
                ? new SolidColorBrush(Microsoft.UI.Colors.Green)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
            DreamFactoryToggleBtn.Content = running ? "⏹ 停止" : "▶ 启动";
            DreamFactoryPort.IsEnabled = !running;
        }

        private void StartDreamFactoryService()
        {
            UpdateDreamConfigFromUI();
            // 同步外部大模型配置（从设置 → 梦工厂）
            if (_dreamConfig.ModelSource == "Custom")
            {
                _dreamConfig.CustomApiUrl = _config.ExternalLLMApiUrl.TrimEnd('/') + "/chat/completions";
                _dreamConfig.CustomApiKey = _config.ExternalLLMApiKey;
            }
            _dreamFactoryService?.Dispose();
            _dreamFactoryService = new AIDreamFactoryService(_dreamConfig);
            _dreamFactoryService.Metrics = _metricsService;
            _dreamFactoryService.OnLog += OnDreamFactoryLog;
            _dreamFactoryService.OnReportGenerated += OnDreamFactoryReport;
            _dreamFactoryService.OnWebReportReady += ShowWebReportToast;
            _dreamFactoryService.OnPopupNotifyAsync += OnGenericPopupNotifyAsync;
            _dreamFactoryService.OnPopupConfirmAsync += OnGenericPopupConfirmAsync;
            _dreamFactoryService.KnowledgeSearcher = SearchKnowledgeBaseAsync;
            _dreamFactoryService.Start();
            UpdateDreamFactoryStatusUI();
        }

        /// <summary>知识库检索器：把问题向量化后在指定集合中召回 TopK 片段，返回拼接文本（HTTP 后台线程调用，需编组到 UI 线程）</summary>
        private Task<string> SearchKnowledgeBaseAsync(string question, int topK, string collection)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await SearchKnowledgeBaseCoreAsync(question, topK, collection));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult("");
                    Debug.WriteLine($"[知识库] 检索异常: {ex.Message}");
                }
            });
            return tcs.Task;
        }

        private async Task<string> SearchKnowledgeBaseCoreAsync(string question, int topK, string? collection)
        {
            try
            {
                if (!await EnsureKbStoreConnected())
                {
                    Debug.WriteLine("[知识库] 未连接，无法检索");
                    return "";
                }

                // 集合名未指定时：优先使用 UI 当前选中集合，其次取第一个集合
                string col = string.IsNullOrWhiteSpace(collection)
                    ? GetCurrentCollectionName()
                    : collection.Trim();
                if (string.IsNullOrWhiteSpace(col))
                    col = _kbConfig.CollectionName;
                if (string.IsNullOrWhiteSpace(col))
                {
                    try
                    {
                        var collections = await _kbStore!.ListCollectionsAsync();
                        col = collections.FirstOrDefault() ?? "";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[知识库] 获取集合列表失败: {ex.Message}");
                    }
                }
                if (string.IsNullOrEmpty(col))
                    return "";

                float[] queryVec;
                try
                {
                    queryVec = await _kbEmbedding.EmbedAsync(question, _kbConfig);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[知识库] 向量化失败: {ex.Message}");
                    queryVec = FallbackHashEmbedding(question, _kbConfig.Dimension);
                }

                var results = await _kbStore!.SearchAsync(col, queryVec, topK);
                if (results.Count == 0) return "";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"集合: {col}");
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    sb.AppendLine();
                    sb.AppendLine($"【片段 {i + 1}】（相似度 {r.Score:F4}）");
                    if (!string.IsNullOrEmpty(r.SourceFile))
                        sb.AppendLine($"来源: {r.SourceFile}");
                    sb.AppendLine(r.Content.Length > 800 ? r.Content[..800] + "..." : r.Content);
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[知识库] 检索异常: {ex.Message}");
                return "";
            }
        }

        private void OnDreamFactoryToggle(object sender, RoutedEventArgs e)
        {
            if (_dreamFactoryService?.IsRunning == true)
            { _dreamFactoryService.Stop(); UpdateDreamFactoryStatusUI(); }
            else { StartDreamFactoryService(); }
        }

        // ========== 工作流模式切换（主流程 / 知识库检索流程） ==========

        private async void OnWorkflowMainClick(object sender, RoutedEventArgs e)
        {
            await SwitchWorkflowModeAsync(DreamWorkflowMode.Main);
        }

        private async void OnWorkflowKbClick(object sender, RoutedEventArgs e)
        {
            await SwitchWorkflowModeAsync(DreamWorkflowMode.KnowledgeBase);
        }

        /// <summary>切换工作流模式：先把当前 UI 内容存档，再加载目标模式的配置到界面</summary>
        private async Task SwitchWorkflowModeAsync(DreamWorkflowMode mode)
        {
            if (_isLoadingDreamConfig) return;
            if (_dreamConfig.CurrentWorkflowMode == mode) return;

            // 1. 保存当前编辑的内容到原模式
            UpdateDreamConfigFromUI();

            // 2. 切换模式
            _dreamConfig.CurrentWorkflowMode = mode;

            // 3. 加载新模式的配置到 UI
            try
            {
                _isLoadingDreamConfig = true;
                DreamFactoryPrompt.Text = _dreamConfig.GetEffectiveSystemPrompt();
                DreamFactoryWorkflowName.Text = _dreamConfig.GetWorkflowName(mode);
                ApplyWorkflowModeButtons();
            }
            finally
            {
                _isLoadingDreamConfig = false;
            }

            // 4. 持久化
            SaveDreamFactoryConfig();

            // 5. 切到知识库检索流程时，检查是否已配置知识库检索动作，未配置则引导添加
            if (mode == DreamWorkflowMode.KnowledgeBase)
                await EnsureKbRetrievalActionAsync();
        }

        /// <summary>检查知识库检索流程的前置/后置逻辑是否配置了"调用工具→知识库"动作；都没有则弹窗引导添加到前置逻辑</summary>
        private async Task EnsureKbRetrievalActionAsync()
        {
            try
            {
                EnsureLogicPipelineExists();
                string wf = _dreamConfig.GetWorkflowName(DreamWorkflowMode.KnowledgeBase);
                if (!_dreamConfig.LogicPipelines.TryGetValue(wf, out var plc))
                    return;

                bool hasKbAction(LogicPipeline? pipe)
                {
                    if (pipe == null || !pipe.Enabled) return false;
                    return pipe.Actions.Any(a =>
                        a.ActionType == "call_tool"
                        && a.Params.TryGetValue("tool_name", out var tn)
                        && tn == "知识库");
                }

                // 前置 + 后置所有节点都未配置知识库检索动作 → 弹窗引导
                if (hasKbAction(plc.PreAILogic) || hasKbAction(plc.PostAILogic))
                    return;

                bool? confirmed = await ShowYesNoDialogAsync("知识库检索流程",
                    "知识库检索流程的前置/后置逻辑中均未配置知识库检索动作。\n\n" +
                    "是否自动在【前置】节点添加【调用工具 → 知识库】动作？\n\n" +
                    "(你也可以手动在逻辑设计器中选择\"调用工具\"并选择工具\"知识库\")");
                if (confirmed != true) return;

                plc.PreAILogic ??= new LogicPipeline();
                plc.PreAILogic.Enabled = true;
                plc.PreAILogic.Actions.Add(new LogicAction
                {
                    ActionType = "call_tool",
                    Params = new Dictionary<string, string> { ["tool_name"] = "知识库" }
                });
                SaveDreamFactoryConfig();
                await ShowDialogAsync("已添加", "已在【前置】逻辑中添加【调用工具 → 知识库】动作。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[知识库] 自动配置检索动作失败: {ex.Message}");
            }
        }

        /// <summary>刷新两模式切换按钮的高亮样式（互斥：一个按下，另一个弹起）</summary>
        private void ApplyWorkflowModeButtons()
        {
            if (WorkflowMainBtn == null || WorkflowKbBtn == null) return;
            bool isMain = _dreamConfig.CurrentWorkflowMode != DreamWorkflowMode.KnowledgeBase;
            SetWorkflowBtnHighlight(WorkflowMainBtn, isMain);
            SetWorkflowBtnHighlight(WorkflowKbBtn, !isMain);
            if (WorkflowModeHint != null)
                WorkflowModeHint.Text = isMain
                    ? $"当前：主流程（System Prompt / 工作流 {_dreamConfig.CurrentWorkflow}）"
                    : $"当前：知识库检索流程（System Prompt / 工作流 {_dreamConfig.CurrentWorkflowKb}）";
        }

        private void SetWorkflowBtnHighlight(Button btn, bool active)
        {
            if (btn == null) return;
            btn.Background = active
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
            btn.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            btn.BorderThickness = active ? new Thickness(0) : new Thickness(1);
        }

        private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
        {
            _trayHelper?.Dispose();
            _trayHelper = new TrayIconHelper();
            _trayHelper.OnDoubleClick += RestoreFromTray;
            _trayHelper.OnShowRequest += RestoreFromTray;
            _trayHelper.OnExitRequest += OnTrayExit;
            _trayHelper.Show("C99 - AI梦工厂");
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, 0);
        }

        private void OnDreamFactoryTestClick(object sender, RoutedEventArgs e)
        {
            int port = 9527;
            if (int.TryParse(DreamFactoryPort.Text, out var p) && p > 0 && p <= 65535)
                port = p;
            var win = new ApiTestWindow(port);
            win.Activate();
        }

        public void RestoreFromTray()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, 5);
            SetForegroundWindow(hwnd);
            _trayHelper?.Dispose();
            _trayHelper = null;
        }

        private void OnTrayExit()
        {
            _trayHelper?.Dispose();
            _trayHelper = null;
            this.Close();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void OnDreamFactoryModelSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DreamFactoryModelSource?.SelectedItem is not ComboBoxItem item) return;
            if (DreamFactoryBuiltInPanel == null || DreamFactoryCustomPanel == null) return;
            bool isBuiltIn = item.Tag?.ToString() == "BuiltIn";
            DreamFactoryBuiltInPanel.Visibility = isBuiltIn ? Visibility.Visible : Visibility.Collapsed;
            DreamFactoryCustomPanel.Visibility = isBuiltIn ? Visibility.Collapsed : Visibility.Visible;
            OnDreamFactoryConfigChanged(sender, null!);
        }

        private void OnDreamFactoryBuiltInModelChanged(object sender, SelectionChangedEventArgs e)
        {
            OnDreamFactoryConfigChanged(sender, e);
            ScanBuiltInModelFiles();
        }

        private void OnRefreshBuiltInModelFiles(object sender, RoutedEventArgs e)
        {
            ScanBuiltInModelFiles();
        }

        private void ScanBuiltInModelFiles()
        {
            DreamFactoryBuiltInModelFile?.Items.Clear();
            string searchPath = _config.LLMSearchPath;
            if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath)) return;

            try
            {
                var ggufFiles = Directory.GetFiles(searchPath, "*.gguf", SearchOption.AllDirectories);
                foreach (var file in ggufFiles)
                {
                    string displayName = Path.GetFileName(file);
                    string relativePath = file.Replace(searchPath, "").TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string tag = file;
                    var item = new ComboBoxItem
                    {
                        Content = displayName.Length > 60 ? displayName[..57] + "..." : displayName,
                        Tag = tag
                    };
                    DreamFactoryBuiltInModelFile?.Items.Add(item);
                }

                if (DreamFactoryBuiltInModelFile?.Items.Count > 0)
                    DreamFactoryBuiltInModelFile.PlaceholderText = $"共 {DreamFactoryBuiltInModelFile.Items.Count} 个模型";
                else if (DreamFactoryBuiltInModelFile != null)
                    DreamFactoryBuiltInModelFile.PlaceholderText = "未找到 .gguf 模型文件";
            }
            catch (Exception ex)
            {
                if (DreamFactoryBuiltInModelFile != null)
                    DreamFactoryBuiltInModelFile.PlaceholderText = $"扫描失败: {ex.Message}";
            }
        }

        private void OnDreamFactoryConfigChanged(object sender, object e)
        {
            SaveDreamFactoryConfig();
        }

        private void OnDreamFactoryLog(string msg)
        {
            if (_isClosing) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                try
                {
                    string nl = Environment.NewLine;
                    DreamFactoryLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}{nl}";
                    var lines = DreamFactoryLog.Text.Split(nl);
                    if (lines.Length > 200) DreamFactoryLog.Text = string.Join(nl, lines[^200..]);
                }
                catch (Exception) { }
            });
        }

        private void OnDreamFactoryReport(string summary, string account)
        {
            if (_isClosing) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                try
                {
                    string nl = Environment.NewLine;
                    string header = $"{nl}=== 工作报告 [{DateTime.Now:HH:mm}] {(string.IsNullOrEmpty(account) ? "" : $"账号:{account}")} ==={nl}";
                    DreamFactoryLog.Text += header + summary + nl;
                    DreamFactoryNotificationText.Text = summary;
                    DreamFactoryNotification.Visibility = Visibility.Visible;
                    _notificationTimer?.Stop();
                    _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                    _notificationTimer.Tick += (s, e) =>
                    { _notificationTimer?.Stop(); DreamFactoryNotification.Visibility = Visibility.Collapsed; };
                    _notificationTimer.Start();
                }
                catch (Exception) { }
            });
        }

        private void OnDismissReportNotification(object sender, RoutedEventArgs e)
        {
            _notificationTimer?.Stop();
            DreamFactoryNotification.Visibility = Visibility.Collapsed;
        }

        private void OnClearDreamFactoryLogs(object sender, RoutedEventArgs e)
        {
            DreamFactoryLog.Text = "";
        }

        private void OnDismissGenericNotification(object sender, RoutedEventArgs e)
        {
            _genericNotificationTimer?.Stop();
            GenericNotification.Visibility = Visibility.Collapsed;
        }

        private void ShowWebReportToast(string url, string account)
        {
            try
            {
                var template = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var textNodes = template.GetElementsByTagName("text");
                textNodes[0].AppendChild(template.CreateTextNode("工作报告已生成"));
                textNodes[1].AppendChild(template.CreateTextNode(
                    string.IsNullOrEmpty(account) ? "点击打开查看" : $"账号: {account}"));

                var toastElement = (Windows.Data.Xml.Dom.XmlElement)template.SelectSingleNode("/toast")!;
                toastElement.SetAttribute("launch", url);
                toastElement.SetAttribute("activationType", "protocol");

                var toast = new ToastNotification(template);
                ToastNotificationManager.CreateToastNotifier("C99").Show(toast);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Toast] 通知失败: {ex.Message}");
            }
        }

        private void OnDesignPreAILogic(object sender, RoutedEventArgs e)
        {
            UpdateDreamConfigFromUI();
            EnsureLogicPipelineExists();
            string wf = _dreamConfig.GetWorkflowName(_dreamConfig.CurrentWorkflowMode);
            if (_dreamConfig.LogicPipelines.TryGetValue(wf, out var plc))
            {
                plc.PreAILogic ??= new LogicPipeline();
                var win = new LogicDesignerWindow(plc.PreAILogic, $"{wf} - 前置逻辑(Pre-AI)", pipeline =>
                {
                    plc.PreAILogic = pipeline;
                    SaveDreamFactoryConfig();
                }, _dreamConfig.AITools);
                win.Activate();
            }
        }

        private void OnDesignPostAILogic(object sender, RoutedEventArgs e)
        {
            UpdateDreamConfigFromUI();
            EnsureLogicPipelineExists();
            string wf = _dreamConfig.GetWorkflowName(_dreamConfig.CurrentWorkflowMode);
            if (_dreamConfig.LogicPipelines.TryGetValue(wf, out var plc))
            {
                plc.PostAILogic ??= new LogicPipeline();
                var win = new LogicDesignerWindow(plc.PostAILogic, $"{wf} - 后置逻辑(Post-AI)", pipeline =>
                {
                    plc.PostAILogic = pipeline;
                    SaveDreamFactoryConfig();
                }, _dreamConfig.AITools);
                win.Activate();
            }
        }

        private void OnDesignPostAction(object sender, RoutedEventArgs e)
        {
            UpdateDreamConfigFromUI();
            EnsureLogicPipelineExists();
            string wf = _dreamConfig.GetWorkflowName(_dreamConfig.CurrentWorkflowMode);
            if (_dreamConfig.LogicPipelines.TryGetValue(wf, out var plc))
            {
                plc.PostAction ??= new PostActionConfig();
                var win = new PostActionSettingsWindow(plc.PostAction, wf, _dreamConfig.AITools, action =>
                {
                    plc.PostAction = action;
                    SaveDreamFactoryConfig();
                });
                win.Activate();
            }
        }

        private void EnsureLogicPipelineExists()
        {
            string wf = _dreamConfig.GetWorkflowName(_dreamConfig.CurrentWorkflowMode);
            if (string.IsNullOrEmpty(wf))
            {
                if (_dreamConfig.CurrentWorkflowMode == DreamWorkflowMode.KnowledgeBase)
                { _dreamConfig.CurrentWorkflowKb = "kb_report"; wf = "kb_report"; }
                else
                { _dreamConfig.CurrentWorkflow = "mail_report"; wf = "mail_report"; }
            }
            if (!_dreamConfig.LogicPipelines.ContainsKey(wf))
                _dreamConfig.LogicPipelines[wf] = new LogicPipelineConfig();
        }

        private Task OnGenericPopupNotifyAsync(string title, string message, int autoDismissSeconds)
        {
            if (_isClosing) return Task.CompletedTask;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                try
                {
                    GenericNotificationTitle.Text = title;
                    GenericNotificationText.Text = message;
                    GenericNotification.Visibility = Visibility.Visible;
                    _genericNotificationTimer?.Stop();
                    _genericNotificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoDismissSeconds) };
                    _genericNotificationTimer.Tick += (s, e) =>
                    { _genericNotificationTimer?.Stop(); GenericNotification.Visibility = Visibility.Collapsed; };
                    _genericNotificationTimer.Start();
                }
                catch (Exception) { }
            });
            return Task.CompletedTask;
        }

        private Task<bool> OnGenericPopupConfirmAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (_isClosing) { tcs.SetResult(false); return tcs.Task; }
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_isClosing) { tcs.TrySetResult(false); return; }
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = message,
                        PrimaryButtonText = "确认",
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot
                    };
                    var result = await dialog.ShowAsync();
                    tcs.TrySetResult(result == ContentDialogResult.Primary);
                }
                catch (Exception) { tcs.TrySetResult(false); }
            });
            return tcs.Task;
        }

        // ==================== 设置：外部大模型 ====================

        private void LoadSettingsExternalLLMConfig()
        {
            SettingsExternalLLMUrl.Text = _config.ExternalLLMApiUrl;
            SettingsExternalLLMKey.Text = _config.ExternalLLMApiKey;
            EnsureCurrentInHistory();
            PopulateSettingsModelCombo();

            SettingsApiInputPrice.Text = _dreamConfig.ApiInputPricePerMillion.ToString("F2");
            SettingsApiOutputPrice.Text = _dreamConfig.ApiOutputPricePerMillion.ToString("F2");
            SettingsLocalPrice.Text = _dreamConfig.LocalPricePerMillion.ToString("F2");
        }

        private void PopulateSettingsModelCombo()
        {
            SettingsExternalLLMModels.Items.Clear();
            var models = _config.ExternalLLMAvailableModels;
            foreach (var m in models)
                SettingsExternalLLMModels.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            if (models.Count > 0)
                SettingsExternalLLMModels.PlaceholderText = $"共 {models.Count} 个模型";
            else
                SettingsExternalLLMModels.PlaceholderText = "请先获取模型列表...";
        }

        private void OnSettingsExternalLLMChanged(object sender, object e)
        {
            SaveExternalLLMImmediate();
        }

        private void SaveExternalLLMImmediate()
        {
            _config.ExternalLLMApiUrl = SettingsExternalLLMUrl?.Text?.Trim() ?? "";
            _config.ExternalLLMApiKey = SettingsExternalLLMKey?.Text?.Trim() ?? "";
            ConfigManager.Save(_config);
        }

        // ===== 历史记录按钮 =====

        private void OnExternalLLMUrlHistoryClick(object sender, RoutedEventArgs e)
        {
            ShowHistoryFlyout((Button)sender, _config.ExternalLLMApiUrlHistory,
                selected => { SettingsExternalLLMUrl.Text = selected; SaveExternalLLMImmediate(); });
        }

        private void OnExternalLLMKeyHistoryClick(object sender, RoutedEventArgs e)
        {
            ShowHistoryFlyout((Button)sender, _config.ExternalLLMApiKeyHistory,
                selected => { SettingsExternalLLMKey.Text = selected; SaveExternalLLMImmediate(); });
        }

        // ===== 失焦时记录历史 =====

        private void OnExternalLLMUrlLostFocus(object sender, RoutedEventArgs e)
        {
            string url = SettingsExternalLLMUrl.Text.Trim();
            if (IsValidUrl(url))
                AddToUrlHistory(url);
        }

        private void OnExternalLLMKeyLostFocus(object sender, RoutedEventArgs e)
        {
            string key = SettingsExternalLLMKey.Text.Trim();
            if (!string.IsNullOrEmpty(key))
                AddToKeyHistory(key);
        }

        private static bool IsValidUrl(string url)
        {
            return !string.IsNullOrEmpty(url)
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                && Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        private void ShowHistoryFlyout(Button target, List<string> items, Action<string> onSelected)
        {
            var listView = new ListView
            {
                MaxHeight = 300,
                MinWidth = 280
            };

            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
            };

            foreach (var item in items)
            {
                var textBlock = new TextBlock
                {
                    Text = item,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var deleteBtn = new Button
                {
                    Content = "×",
                    Width = 24,
                    Height = 24,
                    FontSize = 12,
                    Padding = new Thickness(0),
                    Opacity = 0.5
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(textBlock, 0);
                Grid.SetColumn(deleteBtn, 1);
                grid.Children.Add(textBlock);
                grid.Children.Add(deleteBtn);

                var listItem = new ListViewItem { Content = grid };

                textBlock.Tapped += (s, e) =>
                {
                    onSelected(item);
                    flyout.Hide();
                };

                deleteBtn.Click += (s, e) =>
                {
                    items.Remove(item);
                    ConfigManager.Save(_config);
                    listView.Items.Remove(listItem);
                    if (listView.Items.Count == 0)
                        flyout.Hide();
                };

                listView.Items.Add(listItem);
            }

            flyout.Content = listView;
            target.Flyout = flyout;
            flyout.ShowAt(target);
        }

        // ===== 历史记录管理 =====

        private void EnsureCurrentInHistory()
        {
            var url = _config.ExternalLLMApiUrl?.Trim();
            if (!string.IsNullOrEmpty(url) && !_config.ExternalLLMApiUrlHistory.Contains(url))
            {
                _config.ExternalLLMApiUrlHistory.Insert(0, url);
                TrimHistory(_config.ExternalLLMApiUrlHistory);
            }
            var key = _config.ExternalLLMApiKey?.Trim();
            if (!string.IsNullOrEmpty(key) && !_config.ExternalLLMApiKeyHistory.Contains(key))
            {
                _config.ExternalLLMApiKeyHistory.Insert(0, key);
                TrimHistory(_config.ExternalLLMApiKeyHistory);
            }
            if (!string.IsNullOrEmpty(url) || !string.IsNullOrEmpty(key))
                ConfigManager.Save(_config);
        }

        private void AddToUrlHistory(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            _config.ExternalLLMApiUrlHistory.Remove(url);
            _config.ExternalLLMApiUrlHistory.Insert(0, url);
            TrimHistory(_config.ExternalLLMApiUrlHistory);
            ConfigManager.Save(_config);
        }

        private void AddToKeyHistory(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _config.ExternalLLMApiKeyHistory.Remove(key);
            _config.ExternalLLMApiKeyHistory.Insert(0, key);
            TrimHistory(_config.ExternalLLMApiKeyHistory);
            ConfigManager.Save(_config);
        }

        private static void TrimHistory(List<string> list)
        {
            while (list.Count > 20)
                list.RemoveAt(list.Count - 1);
        }

        private void OnSettingsApiPriceChanged(object sender, TextChangedEventArgs e)
        {
            double.TryParse(SettingsApiInputPrice?.Text, out var inp);
            double.TryParse(SettingsApiOutputPrice?.Text, out var outp);
            double.TryParse(SettingsLocalPrice?.Text, out var loc);
            _dreamConfig.ApiInputPricePerMillion = inp;
            _dreamConfig.ApiOutputPricePerMillion = outp;
            _dreamConfig.LocalPricePerMillion = loc;
            SaveDreamFactoryConfig();
        }

        private void OnDreamFactoryMaxTokensChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (DreamFactoryMaxTokensText == null) return;
            int val = (int)e.NewValue;
            DreamFactoryMaxTokensText.Text = val.ToString();
            _dreamConfig.MaxTokens = val;
            SaveDreamFactoryConfig();
        }

        private void OnDreamFactoryMaxTokensTextChanged(object sender, TextChangedEventArgs e)
        {
            if (DreamFactoryMaxTokens == null) return;
            if (int.TryParse(DreamFactoryMaxTokensText.Text, out var v) && v >= 256 && v <= 524288)
                DreamFactoryMaxTokens.Value = v;
        }

        private async void OnFetchExternalModels(object sender, RoutedEventArgs e)
        {
            string url = SettingsExternalLLMUrl.Text.Trim();
            string key = SettingsExternalLLMKey.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SettingsExternalLLMStatus.Text = "请先输入 API 地址";
                return;
            }
            url = url.TrimEnd('/');
            string modelsUrl = url + "/models";
            SettingsExternalLLMStatus.Text = "正在获取模型列表...";

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrEmpty(key))
                    req.Headers.Add("Authorization", $"Bearer {key}");
                var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    SettingsExternalLLMStatus.Text = $"请求失败: HTTP {(int)resp.StatusCode}";
                    return;
                }
                var body = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var dataArray = root.TryGetProperty("data", out var d) ? d : root;
                var modelList = new List<string>();
                if (dataArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrEmpty(id)) modelList.Add(id);
                    }
                }
                modelList.Sort();
                _config.ExternalLLMAvailableModels = modelList;
                ConfigManager.Save(_config);
                PopulateSettingsModelCombo();
                PopulateCustomModelCombo();
                SettingsExternalLLMStatus.Text = $"获取成功，共 {modelList.Count} 个模型";
            }
            catch (Exception ex)
            {
                SettingsExternalLLMStatus.Text = $"获取失败: {ex.Message}";
            }
        }

        private async void OnCheckExternalModelHealth(object sender, RoutedEventArgs e)
        {
            string url = SettingsExternalLLMUrl.Text.Trim();
            string key = SettingsExternalLLMKey.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SettingsExternalLLMStatus.Text = "请先输入 API 地址";
                return;
            }
            url = url.TrimEnd('/');
            string chatUrl = url + "/chat/completions";

            string selectedModel = (SettingsExternalLLMModels.SelectedItem as ComboBoxItem)?.Tag as string
                ?? SettingsExternalLLMModels.Text;
            if (string.IsNullOrEmpty(selectedModel))
            {
                if (_config.ExternalLLMAvailableModels.Count > 0)
                    selectedModel = _config.ExternalLLMAvailableModels[0];
                else
                {
                    SettingsExternalLLMStatus.Text = "请先获取模型列表并选择模型";
                    return;
                }
            }

            SettingsExternalLLMStatus.Text = "正在检测...";

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    model = selectedModel,
                    messages = new[] { new { role = "user", content = "hi" } },
                    max_tokens = 1
                });
                var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, chatUrl) { Content = content };
                if (!string.IsNullOrEmpty(key))
                    req.Headers.Add("Authorization", $"Bearer {key}");
                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    SettingsExternalLLMStatus.Text = "检测通过，接口可用";
                else
                    SettingsExternalLLMStatus.Text = $"检测失败: HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                SettingsExternalLLMStatus.Text = $"检测失败: {ex.Message}";
            }
        }

        private void OnClearExternalModelCache(object sender, RoutedEventArgs e)
        {
            _config.ExternalLLMAvailableModels.Clear();
            ConfigManager.Save(_config);
            SettingsExternalLLMModels.Items.Clear();
            SettingsExternalLLMModels.PlaceholderText = "请先获取模型列表...";
            SettingsExternalLLMStatus.Text = "缓存已清空";
            if (DreamFactoryCustomModel != null) { DreamFactoryCustomModel.Items.Clear(); DreamFactoryCustomModel.PlaceholderText = "请先在设置中获取模型列表..."; }
        }
    }
}
