using C99.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace C99
{
    internal sealed class TreeNodeData
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public override string ToString() => DisplayName;
    }

    public sealed partial class ToolEditorWindow : Window
    {
        private readonly AIToolItem _config;
        private readonly Action<AIToolItem> _onSave;
        private TextBox _descBox;
        private TextBox _dirBox;
        private TreeView _treeView;
        private TextBox _previewBox;
        private TextBlock _binaryHint;

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".obj", ".bin", ".lib", ".exp",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif", ".svg",
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".mp3", ".wav", ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv",
            ".woff", ".woff2", ".ttf", ".otf", ".eot",
            ".db", ".sqlite", ".mdb",
            ".iso", ".img", ".dmg",
        };

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".html", ".htm",
            ".css", ".js", ".ts", ".py", ".cs", ".java", ".yaml", ".yml", ".toml",
            ".ini", ".cfg", ".conf", ".log", ".sql", ".sh", ".bat", ".ps1",
            ".r", ".rb", ".php", ".swift", ".kt", ".lua", ".pl", ".rs", ".go",
            ".c", ".cpp", ".h", ".hpp", ".sln", ".csproj", ".props", ".targets",
            ".gitignore", ".env", ".editorconfig", ".gradle", ".m", ".mm",
            ".vue", ".jsx", ".tsx", ".sass", ".scss", ".less", ".dockerfile",
            ".cmake", ".makefile", ".mk",
        };

        public ToolEditorWindow(AIToolItem config, string title, Action<AIToolItem> onSave)
        {
            _config = config;
            _onSave = onSave;
            this.Title = $"工具编辑 - {title}";

            var rootGrid = new Grid { Margin = new Thickness(20), MinWidth = 700, MinHeight = 500 };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            this.Content = rootGrid;

            // Row 0: Description
            var descPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            descPanel.Children.Add(new TextBlock
            {
                Text = "描述",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });
            _descBox = new TextBox
            {
                Height = 80,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Text = _config.Description,
                PlaceholderText = "工具描述..."
            };
            _descBox.TextChanged += (s, e) => _config.Description = _descBox.Text;
            descPanel.Children.Add(_descBox);
            Grid.SetRow(descPanel, 0);
            rootGrid.Children.Add(descPanel);

            // Row 1: Directory path
            var dirPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            dirPanel.Children.Add(new TextBlock
            {
                Text = "目录路径",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var dirGrid = new Grid();
            dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _dirBox = new TextBox
            {
                Height = 34,
                FontSize = 13,
                PlaceholderText = "工具所在的目录路径...",
                Text = _config.DirectoryPath
            };
            _dirBox.TextChanged += (s, e) =>
            {
                _config.DirectoryPath = _dirBox.Text.Trim();
                RefreshTreeView();
            };
            Grid.SetColumn(_dirBox, 0);
            dirGrid.Children.Add(_dirBox);
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
                    _dirBox.Text = folder.Path;
                }
            };
            Grid.SetColumn(browseBtn, 1);
            dirGrid.Children.Add(browseBtn);
            dirPanel.Children.Add(dirGrid);
            Grid.SetRow(dirPanel, 1);
            rootGrid.Children.Add(dirPanel);

            // Row 2: Split view - TreeView (left) + Preview (right)
            var splitGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: TreeView
            var treeBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x44, 0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 4, 0)
            };
            _treeView = new TreeView();
            _treeView.SelectionChanged += OnTreeSelectionChanged;
            treeBorder.Child = _treeView;
            Grid.SetColumn(treeBorder, 0);
            splitGrid.Children.Add(treeBorder);

            // Right: Preview
            var previewBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x44, 0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(4, 0, 0, 0)
            };
            var previewStack = new StackPanel();
            _previewBox = new TextBox
            {
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            var previewScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _previewBox
            };
            _binaryHint = new TextBlock
            {
                Text = "二进制文件无法预览",
                FontSize = 14,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            previewStack.Children.Add(previewScroll);
            previewStack.Children.Add(_binaryHint);
            previewBorder.Child = previewStack;
            Grid.SetColumn(previewBorder, 1);
            splitGrid.Children.Add(previewBorder);

            Grid.SetRow(splitGrid, 2);
            rootGrid.Children.Add(splitGrid);

            // Row 3: Buttons
            var bottomStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
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

            Grid.SetRow(bottomStack, 3);
            rootGrid.Children.Add(bottomStack);

            // Init
            RefreshTreeView();
        }

        private void RefreshTreeView()
        {
            _treeView.RootNodes.Clear();
            _previewBox.Visibility = Visibility.Collapsed;
            _binaryHint.Visibility = Visibility.Collapsed;

            string dir = _config.DirectoryPath;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var rootNode = new TreeViewNode
            {
                Content = new TreeNodeData
                {
                    DisplayName = Path.GetFileName(dir),
                    FullPath = dir,
                    IsDirectory = true
                },
                IsExpanded = true
            };
            PopulateTreeNodes(rootNode, dir);
            _treeView.RootNodes.Add(rootNode);
        }

        private void PopulateTreeNodes(TreeViewNode parent, string directory)
        {
            try
            {
                foreach (var dirPath in Directory.GetDirectories(directory))
                {
                    var dirNode = new TreeViewNode
                    {
                        Content = new TreeNodeData
                        {
                            DisplayName = Path.GetFileName(dirPath),
                            FullPath = dirPath,
                            IsDirectory = true
                        }
                    };
                    PopulateTreeNodes(dirNode, dirPath);
                    parent.Children.Add(dirNode);
                }

                foreach (var filePath in Directory.GetFiles(directory))
                {
                    var fileNode = new TreeViewNode
                    {
                        Content = new TreeNodeData
                        {
                            DisplayName = Path.GetFileName(filePath),
                            FullPath = filePath,
                            IsDirectory = false
                        }
                    };
                    parent.Children.Add(fileNode);
                }
            }
            catch { }
        }

        private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        {
            _previewBox.Visibility = Visibility.Collapsed;
            _binaryHint.Visibility = Visibility.Collapsed;

            if (_treeView.SelectedNode?.Content is not TreeNodeData data) return;
            string path = data.FullPath;

            if (data.IsDirectory) return;

            if (!File.Exists(path))
            {
                ShowPreview("(文件不存在)");
                return;
            }

            string ext = Path.GetExtension(path);

            if (BinaryExtensions.Contains(ext) || !TextExtensions.Contains(ext))
            {
                _binaryHint.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                const long maxPreviewSize = 500 * 1024;
                if (fileInfo.Length > maxPreviewSize)
                {
                    ShowPreview($"(文件过大，无法预览: {fileInfo.Length / 1024}KB)");
                    return;
                }

                string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
                ShowPreview(content);
            }
            catch
            {
                ShowPreview("(无法读取文件)");
            }
        }

        private void ShowPreview(string content)
        {
            _previewBox.Text = content;
            _previewBox.Visibility = Visibility.Visible;
            _binaryHint.Visibility = Visibility.Collapsed;
        }
    }
}
