using C99.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace C99
{
    public sealed partial class PostActionSettingsWindow : Window
    {
        private readonly PostActionConfig _config;
        private readonly Action<PostActionConfig> _onSave;
        private StackPanel _paramPanel;
        private bool _suppressUpdate;

        private static readonly Dictionary<string, string> ActionTypeLabels = new()
        {
            ["none"] = "无动作",
            ["web_report"] = "打开网页报告",
            ["word"] = "输出 Word 文档",
            ["excel"] = "输出 Excel 表格",
            ["markdown"] = "输出 Markdown 文件",
            ["siyuan"] = "上传思源笔记",
        };

        public PostActionSettingsWindow(PostActionConfig config, string workflowName, Action<PostActionConfig> onSave)
        {
            _config = config;
            _onSave = onSave;
            this.Title = $"输出动作设置 - {workflowName}";

            var rootGrid = new Grid { Margin = new Thickness(20), MinWidth = 440 };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            this.Content = rootGrid;

            // Row 0: Title
            var titleTb = new TextBlock
            {
                Text = $"输出动作设置 - {workflowName}",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(titleTb, 0);
            rootGrid.Children.Add(titleTb);

            var typeCombo = new ComboBox
            {
                Height = 34,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var kv in ActionTypeLabels)
                typeCombo.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
            typeCombo.SelectionChanged += (s, e) =>
            {
                if (_suppressUpdate) return;
                if (typeCombo.SelectedItem is ComboBoxItem item)
                {
                    _config.ActionType = item.Tag?.ToString() ?? "none";
                    RenderParamPanel();
                }
            };
            Grid.SetRow(typeCombo, 1);
            rootGrid.Children.Add(typeCombo);

            // Row 2: Dynamic parameters
            _paramPanel = new StackPanel();
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                MaxHeight = 360
            };
            scrollViewer.Content = _paramPanel;
            Grid.SetRow(scrollViewer, 2);
            rootGrid.Children.Add(scrollViewer);

            // Row 3: Buttons
            var bottomStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            Grid.SetRow(bottomStack, 3);
            rootGrid.Children.Add(bottomStack);

            var saveBtn = new Button
            {
                Content = "保存",
                FontSize = 14,
                Height = 36,
                Width = 100,
                Margin = new Thickness(0, 0, 12, 0)
            };
            saveBtn.Click += (s, e) => { _onSave(_config); this.Close(); };
            bottomStack.Children.Add(saveBtn);

            var cancelBtn = new Button
            {
                Content = "取消",
                FontSize = 14,
                Height = 36,
                Width = 100
            };
            cancelBtn.Click += (s, e) => this.Close();
            bottomStack.Children.Add(cancelBtn);

            // Init
            _suppressUpdate = true;
            foreach (ComboBoxItem item in typeCombo.Items)
            {
                if (item.Tag?.ToString() == _config.ActionType)
                {
                    typeCombo.SelectedItem = item;
                    break;
                }
            }
            _suppressUpdate = false;
            RenderParamPanel();
        }

        private void RenderParamPanel()
        {
            _paramPanel.Children.Clear();

            switch (_config.ActionType)
            {
                case "word":
                case "excel":
                case "markdown":
                    AddLabel("输出目录");
                    AddDirRow("OutputDir");
                    AddLabel("提示：生成的文件名将自动包含时间戳", fontSize: 11, colorHex: "888888");
                    break;

                case "siyuan":
                    AddLabel("思源笔记 API 地址");
                    AddTextBox("SiyuanApiUrl", "http://localhost:6806");

                    AddLabel("API Key (Authorization Bearer)");
                    AddTextBox("SiyuanApiKey", "");

                    AddLabel("笔记本 ID");
                    AddTextBox("SiyuanNotebookId", "");

                    AddLabel("提示：标题将自动使用\"工作报告\"+时间。" +
                             "API 文档：POST /api/notebook/createDocWithMd",
                             fontSize: 11, colorHex: "888888");
                    break;

                case "web_report":
                    AddLabel("提示：启动服务后，可访问 http://localhost:9527/report/latest" +
                             " 查看最新工作报告（HTML页面）。", fontSize: 12,
                             colorHex: "888888");

                    var queryCheckbox = new CheckBox
                    {
                        Content = "允许页面询问",
                        IsChecked = _config.AllowPageQuery,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    queryCheckbox.Checked += (s, e) => _config.AllowPageQuery = true;
                    queryCheckbox.Unchecked += (s, e) => _config.AllowPageQuery = false;
                    _paramPanel.Children.Add(queryCheckbox);
                    break;

                default:
                    // "none"
                    AddLabel("不执行任何额外动作，仅显示通知。", fontSize: 12,
                             colorHex: "888888");
                    break;
            }
        }

        private void AddLabel(string text, int fontSize = 13, string colorHex = "")
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            if (!string.IsNullOrEmpty(colorHex))
                tb.Foreground = ParseColor(colorHex) as SolidColorBrush;
            _paramPanel.Children.Add(tb);
        }

        private static object ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 0xFF, r = 0, g = 0, b = 0;
            if (hex.Length == 8) { a = Convert.ToByte(hex[..2], 16); r = Convert.ToByte(hex[2..4], 16); g = Convert.ToByte(hex[4..6], 16); b = Convert.ToByte(hex[6..8], 16); }
            else if (hex.Length == 6) { r = Convert.ToByte(hex[..2], 16); g = Convert.ToByte(hex[2..4], 16); b = Convert.ToByte(hex[4..6], 16); }
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));
        }

        private void AddTextBox(string propName, string placeholder)
        {
            var tb = new TextBox
            {
                Height = 34,
                FontSize = 13,
                PlaceholderText = placeholder,
                Margin = new Thickness(0, 0, 0, 12),
                Text = GetProp(propName)
            };
            tb.TextChanged += (s, e) => SetProp(propName, tb.Text);
            _paramPanel.Children.Add(tb);
        }

        private void AddDirRow(string propName)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tb = new TextBox
            {
                Height = 34,
                FontSize = 13,
                PlaceholderText = "点击右侧选择文件夹...",
                Text = GetProp(propName)
            };
            tb.TextChanged += (s, e) => SetProp(propName, tb.Text);
            Grid.SetColumn(tb, 0);
            grid.Children.Add(tb);

            var browseBtn = new Button
            {
                Content = "浏览...",
                FontSize = 12,
                Height = 34,
                Width = 60,
                Margin = new Thickness(8, 0, 0, 0)
            };
            browseBtn.Click += async (s, e) =>
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    tb.Text = folder.Path;
                    SetProp(propName, folder.Path);
                }
            };
            Grid.SetColumn(browseBtn, 1);
            grid.Children.Add(browseBtn);

            _paramPanel.Children.Add(grid);
        }

        private string GetProp(string name)
        {
            return name switch
            {
                "OutputDir" => _config.OutputDir,
                "SiyuanApiUrl" => _config.SiyuanApiUrl,
                "SiyuanApiKey" => _config.SiyuanApiKey,
                "SiyuanNotebookId" => _config.SiyuanNotebookId,
                _ => ""
            };
        }

        private void SetProp(string name, string value)
        {
            switch (name)
            {
                case "OutputDir": _config.OutputDir = value; break;
                case "SiyuanApiUrl": _config.SiyuanApiUrl = value; break;
                case "SiyuanApiKey": _config.SiyuanApiKey = value; break;
                case "SiyuanNotebookId": _config.SiyuanNotebookId = value; break;
            }
        }
    }
}
