using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
                if (_runningProcess != null && !_runningProcess.HasExited)
                {
                    try { _runningProcess.Kill(); } catch { }
                    try { _runningProcess.WaitForExit(3000); } catch { }
                    _runningProcess = null;
                }
                SaveAllParams();
            };

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
            HomeContent.Visibility = Visibility.Collapsed;
            AIDreamFactoryContent.Visibility = Visibility.Collapsed;
            AIGeneralStoreContent.Visibility = Visibility.Collapsed;
            SettingsContent.Visibility = Visibility.Collapsed;
            AboutContent.Visibility = Visibility.Collapsed;
            AIBaseContent.Visibility = Visibility.Collapsed;
        }

        private void ShowHome() { HideAllContents(); HomeContent.Visibility = Visibility.Visible; }
        private void ShowAIDreamFactory() { HideAllContents(); AIDreamFactoryContent.Visibility = Visibility.Visible; }
        private void ShowAIGeneralStore() { HideAllContents(); AIGeneralStoreContent.Visibility = Visibility.Visible; }
        private void ShowSettings() { HideAllContents(); SettingsContent.Visibility = Visibility.Visible; }
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

        private async void OnGridButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string label)
            {
                await ShowDialogAsync("功能入口", $"你点击了: {label}");
            }
        }

        private async void OnPageClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button pageBtn)
            {
                string pageText = pageBtn.Content?.ToString() ?? "";
                if (pageText == "...") return;
                string msg = pageText == "◀" || pageText == "▶"
                    ? $"翻页操作: {pageText}"
                    : $"跳转到第 {pageText} 页";
                await ShowDialogAsync("分页", msg);
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

                    _runningProcess = new Process { StartInfo = psi };
                    _runningProcess.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            _ = DispatcherQueue.TryEnqueue(() => AppendLog(e.Data));
                    };
                    _runningProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            _ = DispatcherQueue.TryEnqueue(() => AppendLog($"[ERR] {e.Data}"));
                    };

                    _runningProcess.Start();
                    _runningProcess.BeginOutputReadLine();
                    _runningProcess.BeginErrorReadLine();

                    _ = DispatcherQueue.TryEnqueue(() => AppendLog($"进程已启动 (PID: {_runningProcess.Id})"));

                    _runningProcess.WaitForExit();

                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        int exitCode = _runningProcess?.HasExited == true ? _runningProcess.ExitCode : -1;
                        AppendLog($"进程已退出 (ExitCode: {exitCode})");
                        _runningProcess = null;
                        ResetRunButton();
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
    }
}
