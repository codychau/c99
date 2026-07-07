using C99.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace C99
{
    public sealed partial class LogicDesignerWindow : Window
    {
        private LogicPipeline _pipeline;
        private readonly Action<LogicPipeline> _onSave;
        private readonly string _pipelineLabel;
        private int _selectedIndex = -1;
        private bool _suppressUpdates;

        private ListView _actionList;
        private StackPanel _paramPanel;
        private ComboBox _actionTypeCombo;
        private Button _deleteBtn, _moveUpBtn, _moveDownBtn;

        private struct ParamDef
        {
            public string Key;
            public string Display;
            public string Control;
            public string[]? Options;
            public string? Default;
        }

        private static readonly Dictionary<string, ParamDef[]> ActionParamDefs = new()
        {
            ["replace_text"] = new[] {
                new ParamDef { Key = "target", Display = "目标变量", Control = "text" },
                new ParamDef { Key = "find", Display = "查找内容", Control = "text" },
                new ParamDef { Key = "replace", Display = "替换为", Control = "text" },
            },
            ["regex_replace"] = new[] {
                new ParamDef { Key = "target", Display = "目标变量", Control = "text" },
                new ParamDef { Key = "pattern", Display = "正则表达式", Control = "text" },
                new ParamDef { Key = "replacement", Display = "替换为", Control = "text" },
            },
            ["http_request"] = new[] {
                new ParamDef { Key = "url", Display = "URL", Control = "text" },
                new ParamDef { Key = "method", Display = "方法", Control = "combo", Options = new[] { "POST", "GET" } },
                new ParamDef { Key = "body", Display = "请求体", Control = "text_multi" },
                new ParamDef { Key = "result_var", Display = "结果存入变量", Control = "text" },
            },
            ["search_files"] = new[] {
                new ParamDef { Key = "folder_path", Display = "文件夹路径", Control = "text" },
                new ParamDef { Key = "recursive", Display = "递归搜索子目录", Control = "check" },
                new ParamDef { Key = "keywords", Display = "关键词（逗号分隔，支持 {var} 模板；留空则返回全部）", Control = "text", Default = "{ai_response}" },
                new ParamDef { Key = "smart_analysis", Display = "智能分析关键词", Control = "check" },
            },
            ["log"] = new[] {
                new ParamDef { Key = "message", Display = "日志内容（支持 {var} 模板）", Control = "text_multi" },
            },
            ["popup_notify"] = new[] {
                new ParamDef { Key = "title", Display = "标题", Control = "text" },
                new ParamDef { Key = "message", Display = "内容（支持 {var} 模板）", Control = "text_multi" },
                new ParamDef { Key = "auto_dismiss", Display = "自动消失秒数", Control = "text" },
            },
            ["popup_confirm"] = new[] {
                new ParamDef { Key = "title", Display = "标题", Control = "text" },
                new ParamDef { Key = "message", Display = "内容（支持 {var} 模板）", Control = "text_multi" },
            },
        };

        public LogicDesignerWindow(LogicPipeline pipeline, string title, Action<LogicPipeline> onSave)
        {
            _pipeline = pipeline;
            _onSave = onSave;
            _pipelineLabel = title;
            this.Title = $"逻辑设计器 - {title}";

            var rootGrid = new Grid { Margin = new Thickness(16) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            this.Content = rootGrid;

            // Row 0: Header
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            var titleTb = new TextBlock { Text = $"逻辑设计器 - {title}", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleTb, 0);
            headerGrid.Children.Add(titleTb);

            var toggleStack = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(toggleStack, 1);
            headerGrid.Children.Add(toggleStack);
            toggleStack.Children.Add(new TextBlock { Text = "启用", VerticalAlignment = VerticalAlignment.Center, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            var enabledToggle = new ToggleSwitch { OnContent = "", OffContent = "", Margin = new Thickness(0, 0, 16, 0) };
            enabledToggle.IsOn = _pipeline.Enabled;
            enabledToggle.Toggled += (s, e) => _pipeline.Enabled = enabledToggle.IsOn;
            toggleStack.Children.Add(enabledToggle);

            // Row 1: Body
            var bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bodyGrid, 1);
            rootGrid.Children.Add(bodyGrid);

            // Left panel: Action list
            var leftBorder = new Microsoft.UI.Xaml.Controls.Border { BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x44, 0x88, 0x88, 0x88)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) };
            Grid.SetColumn(leftBorder, 0);
            bodyGrid.Children.Add(leftBorder);

            var leftGrid = new Grid();
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftBorder.Child = leftGrid;

            leftGrid.Children.Add(new TextBlock { Text = "动作列表（按顺序执行）", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

            _actionList = new ListView { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(0, 0, 0, 8) };
            _actionList.SelectionChanged += OnActionSelected;
            Grid.SetRow(_actionList, 1);
            leftGrid.Children.Add(_actionList);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(btnStack, 2);
            leftGrid.Children.Add(btnStack);

            var addBtn = new Button { Content = "+ 添加动作", FontSize = 12, Height = 30, Margin = new Thickness(0, 0, 6, 0) };
            addBtn.Click += OnAddAction;
            btnStack.Children.Add(addBtn);

            _deleteBtn = new Button { Content = "删除", FontSize = 12, Height = 30, Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
            _deleteBtn.Click += OnDeleteAction;
            btnStack.Children.Add(_deleteBtn);

            _moveUpBtn = new Button { Content = "▲", FontSize = 12, Height = 30, Width = 36, Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
            _moveUpBtn.Click += OnMoveUp;
            btnStack.Children.Add(_moveUpBtn);

            _moveDownBtn = new Button { Content = "▼", FontSize = 12, Height = 30, Width = 36, IsEnabled = false };
            _moveDownBtn.Click += OnMoveDown;
            btnStack.Children.Add(_moveDownBtn);

            // Right panel: Params
            var rightBorder = new Microsoft.UI.Xaml.Controls.Border { BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x44, 0x88, 0x88, 0x88)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12) };
            Grid.SetColumn(rightBorder, 2);
            bodyGrid.Children.Add(rightBorder);

            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightBorder.Child = rightGrid;

            rightGrid.Children.Add(new TextBlock { Text = "动作参数", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });

            rightGrid.Children.Add(new TextBlock { Text = "选择动作类型", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), Margin = new Thickness(0, 0, 0, 4) });

            _actionTypeCombo = new ComboBox { Height = 34, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
            AddCbi(_actionTypeCombo, "文本替换 (replace_text)", "replace_text");
            AddCbi(_actionTypeCombo, "正则替换 (regex_replace)", "regex_replace");
            AddCbi(_actionTypeCombo, "HTTP请求 (http_request)", "http_request");
            AddCbi(_actionTypeCombo, "搜索资料库 (search_files)", "search_files");
            AddCbi(_actionTypeCombo, "日志 (log)", "log");
            AddCbi(_actionTypeCombo, "弹窗提醒 (popup_notify)", "popup_notify");
            AddCbi(_actionTypeCombo, "弹窗确认 (popup_confirm)", "popup_confirm");
            _actionTypeCombo.SelectionChanged += OnActionTypeChanged;
            Grid.SetRow(_actionTypeCombo, 1);
            rightGrid.Children.Add(_actionTypeCombo);

            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto };
            Grid.SetRow(scrollViewer, 2);
            rightGrid.Children.Add(scrollViewer);

            _paramPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            scrollViewer.Content = _paramPanel;

            // Row 2: Buttons
            var bottomStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            Grid.SetRow(bottomStack, 2);
            rootGrid.Children.Add(bottomStack);

            var saveBtn = new Button { Content = "保存", FontSize = 14, Height = 36, Width = 100, Margin = new Thickness(0, 0, 12, 0) };
            saveBtn.Click += (s, e) => { FillDefaults(); _onSave(_pipeline); this.Close(); };
            bottomStack.Children.Add(saveBtn);

            var cancelBtn = new Button { Content = "取消", FontSize = 14, Height = 36, Width = 100 };
            cancelBtn.Click += (s, e) => { this.Close(); };
            bottomStack.Children.Add(cancelBtn);

            RefreshActionList();
            if (_pipeline.Actions.Count > 0)
                _actionList.SelectedIndex = 0;
        }

        private static void AddCbi(ComboBox cb, string text, string tag) =>
            cb.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

        private void RefreshActionList()
        {
            _suppressUpdates = true;
            var labels = new List<string>();
            for (int i = 0; i < _pipeline.Actions.Count; i++)
                labels.Add($"{i + 1}. {GetActionLabel(_pipeline.Actions[i])}");
            _actionList.ItemsSource = labels;
            _suppressUpdates = false;
        }

        private static string GetActionLabel(LogicAction a)
        {
            string typeName = a.ActionType switch
            {
                "replace_text" => "文本替换",
                "regex_replace" => "正则替换",
                "http_request" => "HTTP请求",
                "search_files" => "搜索资料库",
                "log" => "日志",
                "popup_notify" => "弹窗提醒", "popup_confirm" => "弹窗确认",
                _ => a.ActionType
            };
            string detail = a.ActionType switch
            {
                "replace_text" => a.Params.TryGetValue("target", out var v) ? v : "",
                "http_request" => a.Params.TryGetValue("url", out var v) ? v : "",
                "search_files" => a.Params.TryGetValue("folder_path", out var v) ? v : "",
                "popup_notify" => a.Params.TryGetValue("title", out var v) ? v : "",
                "popup_confirm" => a.Params.TryGetValue("title", out var v) ? v : "",
                _ => ""
            };
            return string.IsNullOrEmpty(detail) ? typeName : $"{typeName}: {detail}";
        }

        private void OnActionSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUpdates) return;
            _selectedIndex = _actionList.SelectedIndex;
            if (_selectedIndex >= 0 && _selectedIndex < _pipeline.Actions.Count)
            {
                RenderParamPanel(_pipeline.Actions[_selectedIndex]);
                _deleteBtn.IsEnabled = true;
                _moveUpBtn.IsEnabled = _selectedIndex > 0;
                _moveDownBtn.IsEnabled = _selectedIndex < _pipeline.Actions.Count - 1;
            }
            else
            {
                _paramPanel.Children.Clear();
                _deleteBtn.IsEnabled = false;
                _moveUpBtn.IsEnabled = false;
                _moveDownBtn.IsEnabled = false;
            }
        }

        private void RenderParamPanel(LogicAction action)
        {
            _suppressUpdates = true;
            _paramPanel.Children.Clear();

            foreach (ComboBoxItem item in _actionTypeCombo.Items)
            {
                if (item.Tag?.ToString() == action.ActionType)
                {
                    _actionTypeCombo.SelectedItem = item;
                    break;
                }
            }

            if (!ActionParamDefs.TryGetValue(action.ActionType, out var defs))
            {
                _suppressUpdates = false;
                return;
            }

            if (action.ActionType == "search_files")
            {
                var note = new TextBlock
                {
                    Text = "注意：只会搜索文本文件、markdown文件这些以文本为基础的文件",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                _paramPanel.Children.Add(note);
            }

            foreach (var d in defs)
            {
                string currentValue = action.Params.TryGetValue(d.Key, out var v) ? v : (d.Default ?? "");

                var label = new TextBlock { Text = d.Display, FontSize = 13, Margin = new Thickness(0, 8, 0, 4) };
                _paramPanel.Children.Add(label);

                if (d.Control == "combo" && d.Options != null)
                {
                    var combo = new ComboBox { Height = 34, FontSize = 13, Tag = d.Key, Margin = new Thickness(0, 0, 0, 4) };
                    int selIdx = 0;
                    for (int i = 0; i < d.Options.Length; i++)
                    {
                        var cbi = new ComboBoxItem { Content = d.Options[i], Tag = d.Options[i] };
                        combo.Items.Add(cbi);
                        if (d.Options[i] == currentValue) selIdx = i;
                    }
                    combo.SelectedIndex = selIdx;
                    combo.SelectionChanged += OnParamComboChanged;
                    _paramPanel.Children.Add(combo);
                }
                else if (d.Control == "text_multi")
                {
                    var tb = new TextBox
                    {
                        Height = 72, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true, Text = currentValue, Tag = d.Key,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    tb.TextChanged += OnParamTextChanged;
                    _paramPanel.Children.Add(tb);
                }
                else if (d.Control == "check")
                {
                    var cb = new CheckBox
                    {
                        Content = d.Display,
                        FontSize = 13,
                        Tag = d.Key,
                        IsChecked = currentValue == "是",
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    cb.Checked += (s, e) => OnCheckChanged(s, true);
                    cb.Unchecked += (s, e) => OnCheckChanged(s, false);
                    _paramPanel.Children.Add(cb);
                }
                else if (d.Key == "folder_path" && action.ActionType == "search_files")
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    var tb = new TextBox
                    {
                        Height = 32, FontSize = 13, Text = currentValue,
                        Tag = d.Key, MinWidth = 200
                    };
                    tb.TextChanged += OnParamTextChanged;
                    row.Children.Add(tb);
                    var browseBtn = new Button
                    {
                        Content = "浏览",
                        FontSize = 12,
                        Height = 32,
                        Width = 56,
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    browseBtn.Click += async (s, e) =>
                    {
                        var picker = new Windows.Storage.Pickers.FolderPicker();
                        picker.FileTypeFilter.Add("*");
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var folder = await picker.PickSingleFolderAsync();
                        if (folder != null)
                            tb.Text = folder.Path;
                    };
                    row.Children.Add(browseBtn);
                    _paramPanel.Children.Add(row);
                }
                else
                {
                    var tb = new TextBox
                    {
                        Height = 32, FontSize = 13, Text = currentValue,
                        Tag = d.Key, Margin = new Thickness(0, 0, 0, 4)
                    };
                    tb.TextChanged += OnParamTextChanged;
                    _paramPanel.Children.Add(tb);
                }
            }

            _suppressUpdates = false;
        }

        private void OnParamTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressUpdates) return;
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            if (sender is TextBox tb && tb.Tag is string key)
            {
                _pipeline.Actions[_selectedIndex].Params[key] = tb.Text;
                RefreshActionLabel(_selectedIndex);
            }
        }

        private void OnParamComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUpdates) return;
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            if (sender is ComboBox cb && cb.Tag is string key && cb.SelectedItem is ComboBoxItem cbi)
            {
                _pipeline.Actions[_selectedIndex].Params[key] = cbi.Tag?.ToString() ?? "";
            }
        }

        private void OnCheckChanged(object sender, bool isChecked)
        {
            if (_suppressUpdates) return;
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            if (sender is CheckBox cb && cb.Tag is string key)
            {
                _pipeline.Actions[_selectedIndex].Params[key] = isChecked ? "是" : "否";
            }
        }

        private void OnActionTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUpdates) return;
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            if (_actionTypeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string newType)
            {
                var oldAction = _pipeline.Actions[_selectedIndex];
                oldAction.ActionType = newType;
                if (ActionParamDefs.TryGetValue(newType, out var defs))
                {
                    var newParams = new Dictionary<string, string>();
                    foreach (var d in defs)
                        newParams[d.Key] = oldAction.Params.TryGetValue(d.Key, out var v) ? v : (d.Default ?? "");
                    oldAction.Params = newParams;
                }
                RefreshActionList();
                _actionList.SelectedIndex = _selectedIndex;
                RenderParamPanel(oldAction);
            }
        }

        private void RefreshActionLabel(int index)
        {
            if (index < 0 || index >= _pipeline.Actions.Count) return;
            var labels = _actionList.ItemsSource as IList<string>;
            if (labels == null) return;
            string newLabel = $"{index + 1}. {GetActionLabel(_pipeline.Actions[index])}";
            if (index < labels.Count) labels[index] = newLabel;
        }

        private void OnAddAction(object sender, RoutedEventArgs e)
        {
            var newAction = new LogicAction { ActionType = "log" };
            newAction.Params["message"] = "";
            _pipeline.Actions.Add(newAction);
            RefreshActionList();
            _actionList.SelectedIndex = _pipeline.Actions.Count - 1;
        }

        private void OnDeleteAction(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            _pipeline.Actions.RemoveAt(_selectedIndex);
            RefreshActionList();
            if (_pipeline.Actions.Count > 0)
            {
                _actionList.SelectedIndex = Math.Min(_selectedIndex, _pipeline.Actions.Count - 1);
            }
            else
            {
                _paramPanel.Children.Clear();
                _selectedIndex = -1;
            }
        }

        private void OnMoveUp(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex <= 0 || _selectedIndex >= _pipeline.Actions.Count) return;
            (_pipeline.Actions[_selectedIndex - 1], _pipeline.Actions[_selectedIndex]) =
                (_pipeline.Actions[_selectedIndex], _pipeline.Actions[_selectedIndex - 1]);
            RefreshActionList();
            _actionList.SelectedIndex = _selectedIndex - 1;
        }

        private void OnMoveDown(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _pipeline.Actions.Count - 1) return;
            (_pipeline.Actions[_selectedIndex], _pipeline.Actions[_selectedIndex + 1]) =
                (_pipeline.Actions[_selectedIndex + 1], _pipeline.Actions[_selectedIndex]);
            RefreshActionList();
            _actionList.SelectedIndex = _selectedIndex + 1;
        }

        private void FillDefaults()
        {
            foreach (var action in _pipeline.Actions)
            {
                if (!ActionParamDefs.TryGetValue(action.ActionType, out var defs)) continue;
                foreach (var d in defs)
                {
                    if (d.Default != null && !action.Params.ContainsKey(d.Key))
                        action.Params[d.Key] = d.Default;
                }
            }
        }
    }
}
