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
        private void ShowSettings() { HideAllContents(); SettingsContent.Visibility = Visibility.Visible; LoadSettingsExternalLLMConfig(); }
        private void ShowAbout() { HideAllContents(); AboutContent.Visibility = Visibility.Visible; }
        private void ShowAIBase() { HideAllContents(); AIBaseContent.Visibility = Visibility.Visible; }

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

            AppendLog($"程序: {mainExe}");
            AppendLog($"参数: {args}");
            if (isServer)
            {
                string host = string.IsNullOrWhiteSpace(LLamaHost.Text) ? "127.0.0.1" : LLamaHost.Text.Trim();
                string port = string.IsNullOrWhiteSpace(LLamaPort.Text) ? "8080" : LLamaPort.Text.Trim();
                AppendLog($"🌐 浏览器访问: http://{host}:{port}");
            }

            await StartProcessAsync(mainExe, args);
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

        // ==================== llama.cpp 环境检测 ====================

        private async void OnLLamaDetectEnv(object sender, RoutedEventArgs e)
        {
            string exe = FindLLamaExe();
            if (string.IsNullOrEmpty(exe))
            {
                await ShowDialogAsync("错误", "未找到 llama.cpp 可执行文件，请先设置「启动器工作目录」");
                return;
            }

            // 禁用按钮防重复点击
            LLamaDetectEnvBtn.IsEnabled = false;
            LLamaDetectEnvBtn.Content = "⏳ 检测中...";

            try
            {
                var output = new System.Text.StringBuilder();
                output.AppendLine("═══════════════════════════════════════");
                output.AppendLine("  llama.cpp 环境检测报告");
                output.AppendLine("═══════════════════════════════════════");
                output.AppendLine();

                // 1. 基本信息
                output.AppendLine($"可执行文件: {exe}");
                output.AppendLine($"文件大小: {FormatFileSize(new FileInfo(exe).Length)}");
                string exeDir = Path.GetDirectoryName(exe) ?? "";
                output.AppendLine($"所在目录: {exeDir}");
                output.AppendLine();

                // 2. 列出设备 (--list-devices)
                output.AppendLine("── [设备列表] ──");
                string deviceOutput = await RunAndCaptureAsync(exe, "--list-devices");
                output.AppendLine(string.IsNullOrWhiteSpace(deviceOutput) ? "(无输出)" : deviceOutput);
                output.AppendLine();

                // 3. 系统 GPU 环境变量
                output.AppendLine("── [系统环境] ──");
                foreach (var key in new[] { "CUDA_VISIBLE_DEVICES", "CUDA_PATH", "CUDA_HOME", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES" })
                {
                    string? val = Environment.GetEnvironmentVariable(key);
                    output.AppendLine($"  {key} = {val ?? "(未设置)"}");
                }
                output.AppendLine($"  PATH 含 CUDA: {(Environment.GetEnvironmentVariable("PATH")?.Contains("CUDA") == true ? "是" : "否")}");
                output.AppendLine($"  PATH 含 ROCM: {(Environment.GetEnvironmentVariable("PATH")?.Contains("ROCm") == true ? "是" : "否")}");
                output.AppendLine();

                // 4. 显示版本信息 (--version)
                output.AppendLine("── [版本信息] ──");
                string versionOut = await RunAndCaptureAsync(exe, "--version");
                output.AppendLine(string.IsNullOrWhiteSpace(versionOut) ? "(无输出)" : versionOut);
                output.AppendLine();

                output.AppendLine("═══════════════════════════════════════");
                output.AppendLine("检测完成");

                // 弹出小窗展示结果
                var dialog = new ContentDialog
                {
                    Title = "llama.cpp 环境检测",
                    CloseButtonText = "关闭",
                    XamlRoot = this.Content.XamlRoot,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 500,
                        MinWidth = 560,
                        Content = new TextBlock
                        {
                            Text = output.ToString(),
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Padding = new Thickness(12)
                        }
                    }
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                await ShowDialogAsync("检测失败", ex.Message);
            }
            finally
            {
                LLamaDetectEnvBtn.IsEnabled = true;
                LLamaDetectEnvBtn.Content = "🔍 检测环境";
            }
        }

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
        private async Task StartProcessAsync(string fileName, string arguments)
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

        /// <summary>格式化文件大小</summary>
        private string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIdx = 0;
            double size = bytes;
            while (size >= 1024 && unitIdx < units.Length - 1)
            {
                size /= 1024;
                unitIdx++;
            }
            return $"{size:F2} {units[unitIdx]}";
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
            DreamFactoryWorkflowName.Text = _dreamConfig.CurrentWorkflow;

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

            DreamFactoryPrompt.Text = _dreamConfig.SystemPrompt;

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
            _dreamFactoryService.Start();
            UpdateDreamFactoryStatusUI();
        }

        private void OnDreamFactoryToggle(object sender, RoutedEventArgs e)
        {
            if (_dreamFactoryService?.IsRunning == true)
            { _dreamFactoryService.Stop(); UpdateDreamFactoryStatusUI(); }
            else { StartDreamFactoryService(); }
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
            if (_dreamConfig.LogicPipelines.TryGetValue(_dreamConfig.CurrentWorkflow, out var plc))
            {
                plc.PreAILogic ??= new LogicPipeline();
                var win = new LogicDesignerWindow(plc.PreAILogic, $"{_dreamConfig.CurrentWorkflow} - 前置逻辑(Pre-AI)", pipeline =>
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
            if (_dreamConfig.LogicPipelines.TryGetValue(_dreamConfig.CurrentWorkflow, out var plc))
            {
                plc.PostAILogic ??= new LogicPipeline();
                var win = new LogicDesignerWindow(plc.PostAILogic, $"{_dreamConfig.CurrentWorkflow} - 后置逻辑(Post-AI)", pipeline =>
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
            if (_dreamConfig.LogicPipelines.TryGetValue(_dreamConfig.CurrentWorkflow, out var plc))
            {
                plc.PostAction ??= new PostActionConfig();
                var win = new PostActionSettingsWindow(plc.PostAction, _dreamConfig.CurrentWorkflow, action =>
                {
                    plc.PostAction = action;
                    SaveDreamFactoryConfig();
                });
                win.Activate();
            }
        }

        private void EnsureLogicPipelineExists()
        {
            string wf = _dreamConfig.CurrentWorkflow;
            if (string.IsNullOrEmpty(wf)) { _dreamConfig.CurrentWorkflow = "mail_report"; wf = "mail_report"; }
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
            _config.ExternalLLMApiUrl = SettingsExternalLLMUrl?.Text?.Trim() ?? "";
            _config.ExternalLLMApiKey = SettingsExternalLLMKey?.Text?.Trim() ?? "";
            ConfigManager.Save(_config);
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
