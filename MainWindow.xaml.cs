﻿using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;

namespace tagmane
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private FileExplorer _fileExplorer;
        private List<ImageInfo> _imageInfos;
        private List<ImageInfo> _originalImageInfos;
        private Dictionary<string, int> _allTags;
        private bool _isUpdatingSelection = false;
        private HashSet<string> _filterTags = new HashSet<string>();
        private HashSet<string> _selectedTags = new HashSet<string>();
        private HashSet<string> _currentImageTags = new HashSet<string>();
        private Stack<ITagAction> _undoStack = new Stack<ITagAction>();
        private Stack<ITagAction> _redoStack = new Stack<ITagAction>();
        private ObservableCollection<string> _debugLogEntries;
        private ObservableCollection<string> _logEntries;
        private ObservableCollection<ActionLogItem> _actionLogItems;
        private const int MaxLogEntries = 20; // 100から20に変更
        // private Point? _startPoint;
        // private ListViewItem _draggedItem;
        // private bool _isDragging = false;
        private VLMPredictor _vlmPredictor;
        private CancellationTokenSource _cts;
        private List<(string Name, double GeneralThreshold)> _vlmModels = new List<(string, double)> 
        {
            ("SmilingWolf/wd-eva02-large-tagger-v3", 0.50),
            ("SmilingWolf/wd-vit-large-tagger-v3", 0.25),
            ("SmilingWolf/wd-v1-4-swinv2-tagger-v2", 0.35),
            ("SmilingWolf/wd-vit-tagger-v3", 0.25),
            ("SmilingWolf/wd-swinv2-tagger-v3", 0.25),
            ("SmilingWolf/wd-convnext-tagger-v3", 0.25),
            ("SmilingWolf/wd-v1-4-moat-tagger-v2", 0.35),
            ("SmilingWolf/wd-v1-4-convnext-tagger-v2", 0.35),
            ("SmilingWolf/wd-v1-4-vit-tagger-v2", 0.35),
            ("SmilingWolf/wd-v1-4-convnextv2-tagger-v2", 0.35),
            ("fancyfeast/joytag", 0.5)
        };

        private static readonly string[] DefaultCategoryFiles = {
            "tagcount/General.json",
            "tagcount/Copyright.json",
            "tagcount/Artist.json",
            "tagcount/Character.json",
            "tagcount/Meta.json"
        };

        private static readonly string[] CustomCategoryFiles = {
            "tagcount_custom/ParsonCounts.json",
            "tagcount_custom/Face.json"
        };

        private const double DefaultCharacterThreshold = 0.85;
        private Dictionary<string, TagCategory> _tagCategories;
        private Dictionary<string, TagCategory> _defaultTagCategories;
        private Dictionary<string, TagCategory> _customTagCategories;
        private ObservableCollection<CategoryItem> _tagCategoryNames;
        private bool _useCustomCategories = true;
        private List<string> _prefixOrder;
        private List<string> _suffixOrder;

        // インターフェースを追加
        private interface ITagAction
        {
            void DoAction();
            void UndoAction();
            string Description { get; }
        }

        private class TagPositionInfo
        {
            public string Tag { get; set; }
            public int Position { get; set; }
        }

        private class TagAction : ITagAction
        {
            public ImageInfo Image { get; set; }
            public TagPositionInfo TagInfo { get; set; }
            public bool IsAdd { get; set; }
            public Action DoAction { get; set; }
            public Action UndoAction { get; set; }
            public string Description { get; set; }

            void ITagAction.DoAction() => DoAction();
            void ITagAction.UndoAction() => UndoAction();
        }

        private class TagGroupAction : ITagAction
        {
            public ImageInfo Image { get; set; }
            public List<TagPositionInfo> TagInfos { get; set; }
            public bool IsAdd { get; set; }
            public Action DoAction { get; set; }
            public Action UndoAction { get; set; }
            public string Description { get; set; }

            void ITagAction.DoAction() => DoAction();
            void ITagAction.UndoAction() => UndoAction();
        }

        private class TagCategory
        {
            [JsonPropertyName("0")]
            public Dictionary<string, int> Tags { get; set; }
        }

        private class CategoryItem
        {
            public string Name { get; set; }
            public string OrderType { get; set; } // "Prefix", "Suffix", or ""
        }

        public ObservableCollection<string> Tags { get; set; }    
        private FilterMode _currentFilterMode = FilterMode.Off;
        private enum FilterMode { Off, And, Or }

        private string _webpDllPath;
        private WebPHandler _webPHandler;

        // 非同期処理のフラグ
        private bool _isAsyncProcessing = false;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                _fileExplorer = new FileExplorer();
                _allTags = new Dictionary<string, int>();
                _logEntries = new ObservableCollection<string>();
                _debugLogEntries = new ObservableCollection<string>();
                _actionLogItems = new ObservableCollection<ActionLogItem>();
                ActionListView.ItemsSource = _actionLogItems;
                
                // デバッグ用のメッセージを追加
                MessageBox.Show("MainWindowが初期化されました。");
                
                // ウィンドウを表示
                this.Show();

                InitializeVLMPredictor();

                //各種設定を読み込む
                LoadSettings();

                Tags = new ObservableCollection<string>();
                TagListView.ItemsSource = Tags;

                _tagCategories = new Dictionary<string, TagCategory>();
                _defaultTagCategories = new Dictionary<string, TagCategory>();
                _customTagCategories = new Dictionary<string, TagCategory>();
                _tagCategoryNames = new ObservableCollection<CategoryItem>();
                TagCategoryListView.ItemsSource = _tagCategoryNames;

                _prefixOrder = new List<string>();
                _suffixOrder = new List<string>();

                LoadTagCategories();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"MainWindowの初期化中にエラーが発生しました: {ex.Message}\n\nStackTrace: {ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /*
        基本モジュール
        */

        // キーイベントハンドラ（ショートカットキー）
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectFolder();
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveAllTags();
                e.Handled = true;
            }
        }

        private void SelectWebPDllButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "DLLファイル (*.dll)|*.dll",
                Title = "WebP.dllを選択してください"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _webpDllPath = openFileDialog.FileName;
                WebPDllPathTextBox.Text = _webpDllPath;
                SaveSettings();
            }
        }

        private void LoadSettings()
        {
            _webpDllPath = Properties.Settings.Default.WebPDllPath;
            WebPDllPathTextBox.Text = _webpDllPath;
            _webPHandler = new WebPHandler(_webpDllPath);

            // VLMモデルの設定を読み込む
            string savedModel = Properties.Settings.Default.SelectedVLMModel;
            VLMModelComboBox.ItemsSource = _vlmModels.Select(m => m.Name);
            if (!string.IsNullOrEmpty(savedModel) && _vlmModels.Any(m => m.Name == savedModel)) 
            { 
                VLMModelComboBox.SelectedIndex = _vlmModels.FindIndex(m => m.Name == savedModel);
            }
            else { VLMModelComboBox.SelectedIndex = 0; }
            UpdateThresholds(_vlmModels[VLMModelComboBox.SelectedIndex].GeneralThreshold, DefaultCharacterThreshold);

            AddMainLogEntry("設定を復元しました。");
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.WebPDllPath = _webpDllPath;
            
            // 選択されたVLMモデルを保存
            if (VLMModelComboBox.SelectedItem is string selectedModel) { Properties.Settings.Default.SelectedVLMModel = selectedModel; }
            
            Properties.Settings.Default.Save();
            AddMainLogEntry("設定を保存しました。");
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }

        private BitmapSource LoadImage(string imagePath)
        {
            string extension = Path.GetExtension(imagePath).ToLower();
            if (extension == ".webp")
            {
                return _webPHandler.LoadWebPImage(imagePath);
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private TagGroupAction CreateAddTagsAction(ImageInfo imageInfo, List<string> newTags)
        {
            return new TagGroupAction
            {
                Image = imageInfo,
                TagInfos = newTags.Select(tag => new TagPositionInfo { Tag = tag, Position = imageInfo.Tags.Count }).ToList(),
                IsAdd = true,
                DoAction = () =>
                {
                    foreach (var tag in newTags)
                    {
                        imageInfo.Tags.Add(tag);
                    }
                    AddMainLogEntry($"{imageInfo.ImagePath}に{newTags.Count}個のタグを追加しました");
                    UpdateUIAfterImageInfosChange();
                },
                UndoAction = () =>
                {
                    foreach (var tag in newTags)
                    {
                        imageInfo.Tags.Remove(tag);
                    }
                    AddMainLogEntry($"{imageInfo.ImagePath}から{newTags.Count}個のタグの追加を取り消しました");
                    UpdateUIAfterImageInfosChange();
                },
                Description = $"{imageInfo.ImagePath}に{newTags.Count}個のタグを追加"
            };
        }

        private void UpdateProgressBar(double progress)
        {
            Dispatcher.Invoke(() =>
            {
                // ログレスバーの更新処理
                // ProgressBarコントロールがUIに追加されていることを前提としています
                ProgressBar.Value = progress * 100;
            });
        }

        // 画像リストの更新 (すべての表示内容を更新する)
        private void UpdateUIAfterImageInfosChange()
        {
            if (_imageInfos == null) { return; }
            UpdateImageList();
            if (_allTags == null) { return; }
            UpdateCurrentTags();
            UpdateAllTags();
            UpdateTagListView();
            UpdateAllTagsListView();
            UpdateSelectedTagsListBox();
            UpdateFilteredTagsListBox();
            UpdateSearchedTagsListView();
        }

        // 選択された画像の更新 (選択が変化したときのみ)
        private void UpdateUIAfterSelectionChange()
        {
            if (_imageInfos == null || _allTags == null) { return; }
            UpdateCurrentTags();
            UpdateTagListView();
            UpdateAllTagsListView();
            UpdateSelectedTagsListBox();
            UpdateFilteredTagsListBox();
            UpdateSearchedTagsListView();
        }

        // ボタンの状態を更新
        private void UpdateButtonStates()
        {
            UndoButton.IsEnabled = _undoStack.Count > 0;
            RedoButton.IsEnabled = _redoStack.Count > 0;
        }

        // デバッグログを追加するメソッド
        private void AddDebugLogEntry(string message)
        {
            string logMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
            _debugLogEntries.Insert(0, logMessage);
            while (_debugLogEntries.Count > MaxLogEntries)
            {
                _debugLogEntries.RemoveAt(_debugLogEntries.Count - 1);
            }
            DebugLogTextBox.Text = string.Join(Environment.NewLine, _debugLogEntries);
        }

        // ログを追加するメソッド
        private void AddMainLogEntry(string message)
        {
            string logMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
            _logEntries.Insert(0, logMessage);
            while (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveAt(_logEntries.Count - 1);
            }
            // TextBoxに直接ログを追加
            MainLogTextBox.Text = string.Join(Environment.NewLine, _logEntries);
        }

        // アクションログを追加するメソッド
        private void AddActionLogItem(string actionType, string description)
        {
            _actionLogItems.Insert(0, new ActionLogItem { ActionType = actionType, Description = description });
            while (_actionLogItems.Count > MaxLogEntries)
            {
                _actionLogItems.RemoveAt(_actionLogItems.Count - 1);
            }
        }

        // ActionLogItemクラスを追加
        public class ActionLogItem
        {
            public string ActionType { get; set; }
            public string Description { get; set; }
        }
        
        /*
        ここまで基本モジュール
        */

        // 左ペイン: フォルダ選択と画像リスト表示
        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            SelectFolder();
        }

        private void SelectFolder()
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "フォルダを選択してください"
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                _originalImageInfos = _fileExplorer.GetImageInfos(dialog.FileName);
                _imageInfos = _originalImageInfos;
                
                // Undo/Redoスタックをクリア
                _undoStack.Clear();
                _redoStack.Clear();
                
                UpdateUIAfterImageInfosChange();
                
                AddMainLogEntry($"{_imageInfos.Count}個の画像が見つかりました。");
                AddMainLogEntry($"フォルダを選択しました: {dialog.FileName}");
                AddMainLogEntry("Undo/Redoスタックをクリアしました。");
            }
        }

        // タグの保存
        private void SaveTagsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAllTags();
        }

        // すべての画像のタグを保存
        private void SaveAllTags()
        {
            if (_imageInfos == null || _imageInfos.Count == 0)
            {
                AddMainLogEntry("対象の画像がありません。");
                return;
            }
            if (ConfirmCheckBox.IsChecked == true)
            {
                var result = MessageBox.Show("すべての画像のタグを保存しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    AddMainLogEntry("タグの保存がキャンセルされました。");
                    return;
                }
            }
            foreach (var imageInfo in _imageInfos)
            {
                SaveTagsToFile(imageInfo);
                imageInfo.AssociatedText = string.Join(", ", imageInfo.Tags);
            }
            MessageBox.Show("すべての画像のタグを保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
            
            // 中央ペインの更新
            if (ImageListBox.SelectedItem is ImageInfo selectedImage)
            {
                AssociatedText.Text = selectedImage.AssociatedText;
            }
        }

        // 画像のタグをファイルに保存
        private void SaveTagsToFile(ImageInfo imageInfo)
        {
            string textFilePath = System.IO.Path.ChangeExtension(imageInfo.ImagePath, ".txt");
            string tagString = string.Join(", ", imageInfo.Tags);
            
            try
            {
                File.WriteAllText(textFilePath, tagString);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルの保存中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                AddMainLogEntry($"タグの保存に失敗: {System.IO.Path.GetFileName(imageInfo.ImagePath)} - {ex.Message}");
            }
        }

        // 選択された画像とそのタグを削除
        private async void DeleteSelectedImageAndTags_Click(object sender, RoutedEventArgs e)
        {
            var selectedImage = ImageListBox.SelectedItem as ImageInfo;
            if (selectedImage == null)
            {
                AddMainLogEntry("画像が選択されていません。");
                return;
            }

            if (ConfirmCheckBox.IsChecked == true)
            {
                var result = MessageBox.Show($"選択された画像 '{System.IO.Path.GetFileName(selectedImage.ImagePath)}' とそのタグを削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    AddMainLogEntry("削除がキャンセルされました。");
                    return;
                }
            }

            try
            {
                // ImageListBoxの選択をクリア
                ImageListBox.SelectedItem = null;
                
                _imageInfos.Remove(selectedImage);
                _originalImageInfos.Remove(selectedImage);

                // GCを強制的に実行してリソースを解放
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (File.Exists(selectedImage.ImagePath))
                {
                    File.Delete(selectedImage.ImagePath);
                }
                string textFilePath = System.IO.Path.ChangeExtension(selectedImage.ImagePath, ".txt");
                if (File.Exists(textFilePath))
                {
                    File.Delete(textFilePath);
                }

                // Undo/Redoスタックをクリア
                _undoStack.Clear();
                _redoStack.Clear();

                UpdateUIAfterImageInfosChange();
                UpdateButtonStates();
                AddMainLogEntry($"画像 '{System.IO.Path.GetFileName(selectedImage.ImagePath)}' とそのタグを削除しました。");
                AddMainLogEntry("Undo/Redoスタックをクリアしました。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"画像の削除中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                AddMainLogEntry($"画像の削除中にエラーが発生: {ex.Message}");
            }
        }

        // キャンセルボタンのクリックイベントハンドラ
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            AddMainLogEntry("処理のキャンセルが要求されました");
        }

        // 画像リストの更新
        private void UpdateImageList()
        {
            ImageListBox.ItemsSource = _imageInfos;
        }

        // 中央ペイン: 選択された画像の表示と関連テキストの表示
        private void ImageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;

            // 画像の更新前に表示委を解除し、メモリを解放する
            SelectedImage.Source = null;
            AssociatedText.Text = "";

            // ガベージコレクタを強制的に実行してメモリを解放する
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (ImageListBox.SelectedItem is ImageInfo selectedImage)
            {
                try
                {
                    _isUpdatingSelection = true;
                    SelectedImage.Source = LoadImage(selectedImage.ImagePath);
                    AssociatedText.Text = selectedImage.AssociatedText;
                    _currentImageTags = new HashSet<string>(selectedImage.Tags);
                    
                    UpdateUIAfterSelectionChange();
                    
                    AddMainLogEntry($"画像を選択しました: {System.IO.Path.GetFileName(selectedImage.ImagePath)}");
                }
                catch (Exception ex)
                {
                    AddMainLogEntry($"画像の読み込み中にエラーが発生しました: {ex.Message}");
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
        }

        // 元に戻す
        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count > 0)
            {
                var action = _undoStack.Pop();
                action.UndoAction();
                _redoStack.Push(action);
                UpdateAllTags();
                UpdateButtonStates();
                AddActionLogItem("元に戻す", action.Description);
            }
        }

        // やり直し
        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_redoStack.Count > 0)
            {
                var action = _redoStack.Pop();
                action.DoAction();
                _undoStack.Push(action);
                UpdateAllTags();
                UpdateButtonStates();
                AddActionLogItem("やり直し", action.Description);
            }
        }

        // 右ペイン1: 現在の画像のタグリスト表示と選択
        // タグリストビューの更新
        private void UpdateTagListView()
        {
            AddDebugLogEntry("UpdateTagListView");

            var currentTags = _currentImageTags.ToList();
            TagListView.ItemsSource = currentTags;

            _isUpdatingSelection = true;
            try
            {
                // 選択状態を更新
                TagListView.SelectionChanged -= TagListView_SelectionChanged;
                TagListView.SelectedItems.Clear();
                var tagsToSelect = currentTags.Where(tag => _selectedTags.Contains(tag)).ToList();
                foreach (var tag in tagsToSelect)
                {
                    TagListView.SelectedItems.Add(tag);
                }
                TagListView.SelectionChanged += TagListView_SelectionChanged;
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        // 個別タグリストの選択
        private void TagListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AddDebugLogEntry("TagListView_SelectionChanged");

            // ここで選択は処理しないので、SelectionChangedでの選択状態の反映はキャンセルする
            foreach (var item in e.AddedItems)
            {
                if (TagListView.SelectedItems.Contains(item))
                {
                    TagListView.SelectedItems.Remove(item);
                }
            }
        }

        // 個別タグリストの選択解除
        private void DeselectTagButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedTags = TagListView.SelectedItems.Cast<string>().ToList();
            foreach (var tag in selectedTags)
            {
                _selectedTags.Remove(tag);
            }
            UpdateTagListView();
            UpdateAllTagsListView();
            UpdateSelectedTagsListBox();
        }

        // 個別タグの更新
        private void UpdateCurrentTags()
        {
            _currentImageTags.Clear();
            var imageInfo = ImageListBox.SelectedItem as ImageInfo;
            if (imageInfo != null)
            {
                _currentImageTags = new HashSet<string>(imageInfo.Tags);
            }
        }

        // タグの追加
        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedImage = ImageListBox.SelectedItem as ImageInfo;
            if (selectedImage != null)
            {
                var selectedTags = SelectedTagsListBox.Items.Cast<string>().ToList();
                var addedTags = new List<TagPositionInfo>();

                foreach (var tag in selectedTags)
                {
                    if (!selectedImage.Tags.Contains(tag))
                    {
                        int insertPosition = selectedImage.Tags.Count;
                        addedTags.Add(new TagPositionInfo { Tag = tag, Position = insertPosition });
                    }
                }

                if (addedTags.Count > 0)
                {
                    var action = new TagGroupAction
                    {
                        Image = selectedImage,
                        TagInfos = addedTags,
                        IsAdd = true,
                        DoAction = () =>
                        {
                            foreach (var tagInfo in addedTags)
                            {
                                selectedImage.Tags.Insert(tagInfo.Position, tagInfo.Tag);
                            }
                            AddMainLogEntry($"{addedTags.Count}個のタグを追加しました");
                            UpdateUIAfterImageInfosChange();
                        },
                        UndoAction = () =>
                        {
                            foreach (var tagInfo in addedTags.OrderByDescending(t => t.Position))
                            {
                                selectedImage.Tags.RemoveAt(tagInfo.Position);
                            }
                            AddMainLogEntry($"{addedTags.Count}個のタグの追加を取り消しました");
                            UpdateUIAfterImageInfosChange();
                        },
                        Description = $"{addedTags.Count}個のタグ追加"
                    };

                    action.DoAction();
                    _undoStack.Push(action);
                    _redoStack.Clear();
                    UpdateButtonStates();
                }
            }
        }

        // タグの削除
        private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedImage = ImageListBox.SelectedItem as ImageInfo;
            if (selectedImage != null)
            {
                var selectedTags = TagListView.SelectedItems.Cast<string>().ToList();
                var removedTags = new List<TagPositionInfo>();

                foreach (var tag in selectedTags)
                {
                    int removePosition = selectedImage.Tags.IndexOf(tag);
                    if (removePosition != -1)
                    {
                        removedTags.Add(new TagPositionInfo { Tag = tag, Position = removePosition });
                    }
                }

                if (removedTags.Count > 0)
                {
                    var action = new TagGroupAction
                    {
                        Image = selectedImage,
                        TagInfos = removedTags,
                        IsAdd = false,
                        DoAction = () =>
                        {
                            foreach (var tagInfo in removedTags.OrderByDescending(t => t.Position))
                            {
                                selectedImage.Tags.RemoveAt(tagInfo.Position);
                                _selectedTags.Remove(tagInfo.Tag);
                            }
                            AddMainLogEntry($"{removedTags.Count}個のタグを削除しました");
                            UpdateUIAfterImageInfosChange();
                        },
                        UndoAction = () =>
                        {
                            foreach (var tagInfo in removedTags)
                            {
                                selectedImage.Tags.Insert(tagInfo.Position, tagInfo.Tag);
                            }
                            AddMainLogEntry($"{removedTags.Count}個のタグの削除を取り消しました");
                            UpdateUIAfterImageInfosChange();
                        },
                        Description = $"{removedTags.Count}個のタグを削除"
                    };

                    action.DoAction();
                    _undoStack.Push(action);
                    _redoStack.Clear();
                    UpdateButtonStates();
                }
            }
        }

        // 右ペイン2: 全タグリストの表示、選択、ソート
        // 全タグリストビューの更新
        private void UpdateAllTagsListView()
        {
            AddDebugLogEntry("UpdateAllTagsListView");
            var sortedTags = _allTags
                .Select(kvp => new
                {
                    Tag = kvp.Key,
                    Count = kvp.Value,
                    IsSelected = _selectedTags.Contains(kvp.Key),
                    IsCurrentImageTag = _currentImageTags.Contains(kvp.Key),
                    Category = GetTagCategory(kvp.Key)
                })
                .OrderByDescending(item => item.IsSelected)
                .ThenByDescending(item => item.Count)
                .ThenBy(item => item.Tag)
                .ToList();

            AllTagsListView.SelectionChanged -= AllTagsListView_SelectionChanged;
            AllTagsListView.ItemsSource = sortedTags;

            // 選択状態を更新
            AllTagsListView.SelectedItems.Clear();
            var selectedItems = AllTagsListView.Items.Cast<dynamic>()
                .Where(item => _selectedTags.Contains(item.Tag))
                .ToList();
            foreach (var item in selectedItems)
            {
                AllTagsListView.SelectedItems.Add(item);
            }
            AllTagsListView.SelectionChanged += AllTagsListView_SelectionChanged;
        }

        // 全タグリストの選択
        private void AllTagsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AddDebugLogEntry("AllTagsListView_SelectionChanged");
            if (_isUpdatingSelection) return;

            _isUpdatingSelection = true;
            try
            {
                foreach (var item in e.RemovedItems)
                {
                    var removedTag = ((dynamic)item).Tag as string;
                    _selectedTags.Remove(removedTag);
                }

                foreach (var item in e.AddedItems)
                {
                    var addedTag = ((dynamic)item).Tag as string;
                    _selectedTags.Add(addedTag);
                }

                UpdateAllTagsListView();
                UpdateTagListView();  // 個別タグリストを更新
                UpdateSelectedTagsListBox();
                UpdateSearchedTagsListView();
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        // 全タグリストの選択解除
        private void DeselectAllTagsButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedTags.Clear();
            UpdateTagListView();
            UpdateAllTagsListView();
            UpdateSelectedTagsListBox();
            UpdateSearchedTagsListView();
        }

        // 全タグの更新
        private void UpdateAllTags()
        {
            if (_allTags == null) { _allTags = new Dictionary<string, int>(); }
            _allTags.Clear();
            foreach (var imageInfo in _imageInfos)
            {
                foreach (var tag in imageInfo.Tags)
                {
                    if (_allTags.ContainsKey(tag))
                    {
                        _allTags[tag]++;
                    }
                    else
                    {
                        _allTags[tag] = 1;
                    }
                }
            }
        }

        // フィルタリング
        private void FilterImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_originalImageInfos == null || _originalImageInfos.Count == 0)
            {
                AddMainLogEntry("画像が読み込まれていません。");
                return;
            }
            _currentFilterMode = (FilterMode)(((int)_currentFilterMode + 1) % 3);
            switch (_currentFilterMode)
            {
                case FilterMode.Off:
                    _filterTags = new HashSet<string>();
                    _imageInfos = _originalImageInfos;
                    break;
                case FilterMode.And:
                    _filterTags = _selectedTags;
                    _imageInfos = _originalImageInfos.Where(image => _filterTags.All(tag => image.Tags.Contains(tag))).ToList();
                    break;
                case FilterMode.Or:
                    _filterTags = _selectedTags;
                    _imageInfos = _originalImageInfos.Where(image => image.Tags.Any(tag => _filterTags.Contains(tag))).ToList();
                    break;
            }
            UpdateImageList();
            UpdateAllTags();
            UpdateFilteredTagsListBox();
            UpdateFilterButton();
        }

        private void UpdateFilterButton()
        {
            var filterButton = (Button)FindName("FilterImageButton");
            switch (_currentFilterMode)
            {
                case FilterMode.Off:
                    filterButton.Content = new Image { Source = new BitmapImage(new Uri("/icon/filter.png", UriKind.Relative)), Width = 32, Height = 32 };
                    filterButton.ToolTip = "フィルタリング: オフ";
                    break;
                case FilterMode.And:
                    filterButton.Content = new Image { Source = new BitmapImage(new Uri("/icon/and.png", UriKind.Relative)), Width = 32, Height = 32 };
                    filterButton.ToolTip = "フィルタリング: AND";
                    break;
                case FilterMode.Or:
                    filterButton.Content = new Image { Source = new BitmapImage(new Uri("/icon/or.png", UriKind.Relative)), Width = 32, Height = 32 };
                    filterButton.ToolTip = "フィルタリング: OR";
                    break;
            }
        }

        // 右ペイン3: ユーザー入力タグの追加
        private void AddTextboxinputButton_Click(object sender, RoutedEventArgs e)
        {
            AddTagFromTextBox(false);
        }

        private void AddAllTextboxinputButton_Click(object sender, RoutedEventArgs e)
        {
            AddTagFromTextBox(true);
        }

        private void AddTagFromTextBox(bool addToAllTags)
        {
            string newTag = SearchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(newTag))
            {
                AddMainLogEntry("タグを入力してください。");
                return;
            }

            if (addToAllTags)
            {
                if (_imageInfos == null || _imageInfos.Count == 0)
                {
                    AddMainLogEntry("対象の画像がありません。");
                    return;
                }

                var addedToImages = new List<ImageInfo>();

                foreach (var imageInfo in _imageInfos)
                {
                    if (!imageInfo.Tags.Contains(newTag))
                    {
                        imageInfo.Tags.Add(newTag);
                        addedToImages.Add(imageInfo);
                    }
                }

                if (addedToImages.Count > 0)
                {
                    var action = new TagGroupAction
                    {
                        DoAction = () =>
                        {
                            AddMainLogEntry($"タグ '{newTag}' を {addedToImages.Count} 個の画像に追加しました。");
                            UpdateUIAfterImageInfosChange();
                        },
                        UndoAction = () =>
                        {
                            foreach (var imageInfo in addedToImages)
                            {
                                imageInfo.Tags.Remove(newTag);
                            }
                            AddMainLogEntry($"タグ '{newTag}' の追加を {addedToImages.Count} 個の画像から取り消しました。");
                            UpdateUIAfterImageInfosChange();
                        },
                        Description = $"タグ '{newTag}' を {addedToImages.Count} 個の画像に追加"
                    };

                    _undoStack.Push(action);
                    _redoStack.Clear();
                    UpdateButtonStates();
                    action.DoAction();
                }
                else
                {
                    AddMainLogEntry($"タグ '{newTag}' は既にすべての画像に存在します。");
                }
            }
            else
            {
                var selectedImage = ImageListBox.SelectedItem as ImageInfo;
                if (selectedImage == null)
                {
                    AddMainLogEntry("画像が選択されていません。");
                    return;
                }

                if (!selectedImage.Tags.Contains(newTag))
                {
                    var action = new TagAction
                    {
                        Image = selectedImage,
                        TagInfo = new TagPositionInfo { Tag = newTag, Position = selectedImage.Tags.Count },
                        IsAdd = true,
                        DoAction = () =>
                        {
                            selectedImage.Tags.Add(newTag);
                            AddMainLogEntry($"タグ '{newTag}' を追加しました。");
                            UpdateUIAfterImageInfosChange();
                        },
                        UndoAction = () =>
                        {
                            selectedImage.Tags.Remove(newTag);
                            AddMainLogEntry($"タグ '{newTag}' の追加を取り消しました。");
                            UpdateUIAfterImageInfosChange();
                        },
                        Description = $"タグ '{newTag}' を追加"
                    };

                    action.DoAction();
                    _undoStack.Push(action);
                    _redoStack.Clear();
                    UpdateButtonStates();
                }
                else
                {
                    AddMainLogEntry($"タグ '{newTag}' は既に存在します。");
                }
            }

            SearchTextBox.Clear();
            UpdateSearchedTagsListView();
        }
        
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchedTagsListView();
        }

        private void UpdateSearchedTagsListView()
        {
            AddDebugLogEntry("UpdateSearchedTagsListView");
            AddDebugLogEntry($"SearchTextBox.Text: {SearchTextBox.Text}");

            string searchText = SearchTextBox.Text.ToLower();
            SearchedTagsListView.SelectionChanged -= SearchedTagsListView_SelectionChanged;

            if (!string.IsNullOrEmpty(searchText))
            {
                var matchingTags = _allTags.Keys
                    .Where(tag => tag.ToLower().Contains(searchText))
                    .OrderBy(tag => tag)
                    .ToList();

                AddDebugLogEntry($"matchingTags: {string.Join(", ", matchingTags)}");

                SearchedTagsListView.ItemsSource = matchingTags;
                SearchedTagsListView.SelectedItems.Clear();

                var tagsToSelect = matchingTags.Where(tag => _selectedTags.Contains(tag)).ToList();
                foreach (var tag in tagsToSelect)
                {
                    SearchedTagsListView.SelectedItems.Add(tag);
                }
            }
            else
            {
                // 検索バーが空の場合、SearchedTagsListViewを空にする
                SearchedTagsListView.ItemsSource = null;
            }

            SearchedTagsListView.SelectionChanged += SearchedTagsListView_SelectionChanged;
        }

        private void SearchedTagsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AddDebugLogEntry("SearchedTagsListView_SelectionChanged");
            if (_isUpdatingSelection) return;
            
            foreach (string tag in e.RemovedItems)
            {
                _selectedTags.Remove(tag);
            }

            foreach (string tag in e.AddedItems)
            {
                _selectedTags.Add(tag);
            }

            UpdateUIAfterSelectionChange();
        }

        // 選択されたタグリストの更新
        private void UpdateSelectedTagsListBox()
        {
            SelectedTagsListBox.ItemsSource = _selectedTags.ToList();
        }

        // フィルターするタグリストの更新
        private void UpdateFilteredTagsListBox()
        {
            FilteredTagsListBox.ItemsSource = _filterTags.ToList();
        }

        // 全画像からタグの削除
        private void RemoveAllTagsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imageInfos == null || _imageInfos.Count == 0)
            {
                AddMainLogEntry("対象の画像がありません。");
                return;
            }
            if (ConfirmCheckBox.IsChecked == true)
            {
                var result = MessageBox.Show("選択したタグをすべての画像から削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    AddMainLogEntry("タグの削除がキャンセルされました。");
                    return;
                }
            }
            var selectedTags = AllTagsListView.SelectedItems.Cast<dynamic>().Select(item => item.Tag as string).ToList();
            var removedTags = new Dictionary<ImageInfo, List<TagPositionInfo>>();

            foreach (var imageInfo in _imageInfos)
            {
                var tagsToRemove = imageInfo.Tags
                    .Select((tag, index) => new { Tag = tag, Index = index })
                    .Where(item => selectedTags.Contains(item.Tag))
                    .Select(item => new TagPositionInfo { Tag = item.Tag, Position = item.Index })
                    .ToList();

                if (tagsToRemove.Count > 0)
                {
                    removedTags[imageInfo] = tagsToRemove;
                }
            }

            if (removedTags.Count > 0)
            {
                var action = new TagGroupAction
                {
                    DoAction = () =>
                    {
                        foreach (var kvp in removedTags)
                        {
                            foreach (var tagInfo in kvp.Value.OrderByDescending(t => t.Position))
                            {
                                //テスト中
                                if (tagInfo.Position < kvp.Key.Tags.Count)
                                {
                                    kvp.Key.Tags.RemoveAt(tagInfo.Position);
                                }
                                else
                                {
                                    AddMainLogEntry($"タグの削除に失敗しました: インデックス {tagInfo.Position} が範囲外です。対象画像: {kvp.Key.ImagePath}、タグ: {tagInfo.Tag}");
                                }
                            }
                        }
                        foreach (var tag in selectedTags)
                        {
                            _selectedTags.Remove(tag);
                        }
                        AddMainLogEntry($"{removedTags.Sum(kvp => kvp.Value.Count)}個のタグを削除しました。");
                        UpdateUIAfterImageInfosChange();
                    },
                    UndoAction = () =>
                    {
                        foreach (var kvp in removedTags)
                        {
                            foreach (var tagInfo in kvp.Value)
                            {
                                kvp.Key.Tags.Insert(tagInfo.Position, tagInfo.Tag);
                            }
                        }
                        AddMainLogEntry($"{removedTags.Sum(kvp => kvp.Value.Count)}個のタグを復元しました。");
                        UpdateUIAfterImageInfosChange();
                    },
                    Description = $"{removedTags.Sum(kvp => kvp.Value.Count)}個のタグを全画像から削除"
                };
                _undoStack.Push(action);
                _redoStack.Clear();
                action.DoAction();
                UpdateButtonStates();
            }
        }

        /*
        ドラッグアンドドロップ関連の操作
        */

        private ListViewItem _draggedItem;
        private Point? _startPoint;
        private bool _isDragging;

        private void TagListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AddDebugLogEntry("TagListView_PreviewMouseLeftButtonDown");
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _startPoint = e.GetPosition(null);
                _draggedItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
                _isDragging = false;
            }
        }

        private void TagListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _startPoint.HasValue && _draggedItem != null)
            {
                Point currentPosition = e.GetPosition(null);
                Vector diff = _startPoint.Value - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    ListView listView = sender as ListView;
                    ListViewItem listViewItem = _draggedItem;
                    
                    if (listViewItem != null && listViewItem.Content is string tagData)
                    {
                        DataObject dragData = new DataObject("TagData", tagData);
                        DragDrop.DoDragDrop(listViewItem, dragData, DragDropEffects.Move);
                    }
                }
            }
        }

        private void TagListView_Drop(object sender, DragEventArgs e)
        {
            AddDebugLogEntry("TagListView_Drop");
            if (e.Data.GetDataPresent("TagData"))
            {
                string droppedTag = (string)e.Data.GetData("TagData");
                ListViewItem targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
                AddDebugLogEntry($"droppedTag: {droppedTag}");
                AddDebugLogEntry($"targetItem: {targetItem}");

                if (targetItem != null && targetItem.Content is string)
                {
                    int targetIndex = TagListView.Items.IndexOf(targetItem.Content);
                    int sourceIndex = TagListView.Items.IndexOf(droppedTag);

                    AddDebugLogEntry($"sourceIndex: {sourceIndex}, targetIndex: {targetIndex}");

                    if (sourceIndex != -1 && targetIndex != -1 && sourceIndex != targetIndex)
                    {
                        var selectedImage = ImageListBox.SelectedItem as ImageInfo;
                        AddDebugLogEntry($"selectedImage: {selectedImage}");
                        if (selectedImage != null)
                        {
                            var action = new TagGroupAction
                            {
                                Image = selectedImage,
                                TagInfos = new List<TagPositionInfo> 
                                { 
                                    new TagPositionInfo { Tag = droppedTag, Position = sourceIndex },
                                    new TagPositionInfo { Tag = droppedTag, Position = targetIndex }
                                },
                                IsAdd = false,
                                DoAction = () =>
                                {
                                    string movedTag = selectedImage.Tags[sourceIndex];
                                    selectedImage.Tags.RemoveAt(sourceIndex);
                                    selectedImage.Tags.Insert(targetIndex, movedTag);
                                    UpdateUIAfterImageInfosChange();
                                    AddMainLogEntry($"タグ '{droppedTag}' を移動しました: {sourceIndex} -> {targetIndex}");
                                },
                                UndoAction = () =>
                                {
                                    string movedTag = selectedImage.Tags[targetIndex];
                                    selectedImage.Tags.RemoveAt(targetIndex);
                                    selectedImage.Tags.Insert(sourceIndex, movedTag);
                                    UpdateUIAfterImageInfosChange();
                                    AddMainLogEntry($"タグ '{droppedTag}' の移動を元に戻しました: {targetIndex} -> {sourceIndex}");
                                },
                                Description = $"タグ '{droppedTag}' を移動: {sourceIndex} -> {targetIndex}"
                            };

                            action.DoAction();
                            _undoStack.Push(action);
                            _redoStack.Clear();
                            UpdateButtonStates();
                        }
                    }
                }
            }
        }

        private void TagListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging && _draggedItem != null)
            {
                string tag = _draggedItem.Content as string;
                if (tag != null)
                {
                    if (_selectedTags.Contains(tag))
                    {
                        _selectedTags.Remove(tag);
                        AddDebugLogEntry($"タグ '{tag}' の選択を解除しました。");
                    }
                    else
                    {
                        _selectedTags.Add(tag);
                        AddDebugLogEntry($"タグ '{tag}' を選択しました。");
                    }
                    UpdateUIAfterSelectionChange();
                }
            }
            _draggedItem = null;
            _startPoint = null;
            _isDragging = false;
        }

        private void AllTagsListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AddDebugLogEntry("AllTagsListView_PreviewMouseLeftButtonDown");
            // _startPoint = e.GetPosition(null);
            // var item = (e.OriginalSource as FrameworkElement)?.DataContext;
            // if (item != null)
            // {
            //     _draggedItem = (sender as ListView)?.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
            // }
        }

        private void AllTagsListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            AddDebugLogEntry("AllTagsListView_PreviewMouseMove");
            // if (_startPoint == null || _draggedItem == null) return;

            // Point currentPosition = e.GetPosition(null);
            // Vector diff = currentPosition - _startPoint.Value;

            // if (e.LeftButton == MouseButtonState.Pressed &&
            //     (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance + 2 ||
            //      Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance + 2))
            // {
            //     DragDrop.DoDragDrop(_draggedItem, _draggedItem.DataContext, DragDropEffects.Copy);
            //     _startPoint = null;
            //     _draggedItem = null;
            // }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T)
                {
                    return (T)current;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            while (current != null);
            return null;
        }

        /*
        ここまでドラッグアンドドロップ関連メソッド
        ここからVLM関連メソッド
        */

        private void VLMModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VLMModelComboBox.SelectedItem is string selectedModel)
            {
                var modelInfo = _vlmModels.First(m => m.Name == selectedModel);
                UpdateThresholds(modelInfo.GeneralThreshold, DefaultCharacterThreshold);
                LoadVLMModel(selectedModel);
                
                // 設定を保存
                SaveSettings();
            }
        }

        private void UpdateThresholds(double generalThreshold, double characterThreshold)
        {
            GeneralThresholdSlider.Value = generalThreshold;
            CharacterThresholdSlider.Value = characterThreshold;
        }

        private void GeneralThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // スライダーの値が変更されたときの処理
            // 必要に応じて、この値をVLMPredictorに渡す
        }

        private void CharacterThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // スライダーの値が変更されたときの処理
            // 必要に応じて、この値をVLMPredictorに渡す
        }

        private async void InitializeVLMPredictor()
        {
            AddDebugLogEntry("InitializeVLMPredictor");
            _vlmPredictor = new VLMPredictor();
            _vlmPredictor.LogUpdated += UpdateVLMLog;
        }

        private async void LoadVLMModel(string modelName)
        {
            try
            {
                AddMainLogEntry($"VLMモデル '{modelName}' の読み込みを開始します。");
                await _vlmPredictor.LoadModel(modelName);
                AddMainLogEntry($"VLMモデル '{modelName}' の読み込みが完了しました。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"VLMモデルの読み込みに失敗しました: {ex.Message}");
                AddMainLogEntry($"VLMモデルの読み込みに失敗しました: {ex.Message}");
            }
        }

        // VLM推論を実行するボタンのクリックイベントハンドラ
        private async void VLMPredictButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ボタンを無効化して、処理中であることを示す
                VLMPredictButton.IsEnabled = false;

                // キャンセルトークンソースを作成
                _cts = new CancellationTokenSource();
                
                // 選択された画像を取得
                var selectedImage = ImageListBox.SelectedItem as ImageInfo;
                if (selectedImage == null)
                {
                    AddMainLogEntry("画像が選択されていません。");
                    return;
                }

                // 非同期でPredictVLMTagsを呼び出す
                var predictedTags = await PredictVLMTagsAsync(selectedImage, _cts.Token);
                
                if (selectedImage != null && predictedTags.Any())
                {
                    // 既存のタグと重複しないタグを抽出
                    var newTags = predictedTags.Except(selectedImage.Tags).ToList();
                    
                    if (newTags.Any())
                    {
                        // 新しいタグを追加するアクションを作成
                        var action = new TagGroupAction
                        {
                            Image = selectedImage,
                            TagInfos = newTags.Select(tag => new TagPositionInfo { Tag = tag, Position = selectedImage.Tags.Count }).ToList(),
                            IsAdd = true,
                            DoAction = () =>
                            {
                                foreach (var tagInfo in newTags)
                                {
                                    selectedImage.Tags.Add(tagInfo);
                                }
                                AddMainLogEntry($"VLM推論により{newTags.Count}個の新しいタグを追加しました");
                                UpdateUIAfterImageInfosChange();
                            },
                            UndoAction = () =>
                            {
                                for (int i = 0; i < newTags.Count; i++)
                                {
                                    selectedImage.Tags.RemoveAt(selectedImage.Tags.Count - 1);
                                }
                                AddMainLogEntry($"VLM推論により追加された{newTags.Count}個のタグを削除しました");
                                UpdateUIAfterImageInfosChange();
                            },
                            Description = $"VLM推論により{newTags.Count}個のタグを追加"
                        };

                        // アクションを実行し、Undoスタックに追加
                        action.DoAction();
                        _undoStack.Push(action);
                        _redoStack.Clear();
                        UpdateButtonStates();
                    }
                    else
                    {
                        AddMainLogEntry("VLM推論により新しいタグは見つかりませんでした");
                    }
                }
            }
            catch (Exception ex)
            {
                // エラーメッセージをログに記録
                AddMainLogEntry($"VLM推論中にエラーが発生しました: {ex.Message}");
                MessageBox.Show($"エラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 処理が完了したらボタンを再度有効化
                VLMPredictButton.IsEnabled = true;
            }
        }

        // すべての画像にVLM推論でタグを追加するメソッド
        private async void VLMPredictAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAsyncProcessing) { return; }
            _isAsyncProcessing = true;

            if (_imageInfos == null || _imageInfos.Count == 0)
            {
                AddMainLogEntry("対象画像がありません。");
                return;
            }
            if (ConfirmCheckBox.IsChecked == true)
            {
                var result = MessageBox.Show("すべての画像に対してVLM推論を実行しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    AddMainLogEntry("VLM推論がキャンセルされました。");
                    return;
                }
            }
            try
            {
                // ボタンを無効化して、処理中であることを示す
                VLMPredictAllButton.IsEnabled = false;
                
                // キャンセルトークンソースを作成
                _cts = new CancellationTokenSource();
                
                AddMainLogEntry("すべての画像に対してVLM推を開始します");

                var batchSize = 1; // バッチサイズを設定
                var totalImages = _imageInfos.Count;
                var processedImages = 0;

                for (int i = 0; i < _imageInfos.Count; i += batchSize)
                {
                    var batch = _imageInfos.Skip(i).Take(batchSize).ToList();
                    var batchTasks = batch.Select(async imageInfo =>
                    {
                        try
                        {
                            var predictedTags = await PredictVLMTagsAsync(imageInfo, _cts.Token);
                            var newTags = predictedTags.Except(imageInfo.Tags).ToList();
                            if (newTags.Any())
                            {
                                var action = CreateAddTagsAction(imageInfo, newTags);
                                action.DoAction();
                                _undoStack.Push(action);
                            }
                            
                            Interlocked.Increment(ref processedImages);
                            UpdateProgressBar((double)processedImages / totalImages);
                            
                            AddMainLogEntry($"{imageInfo.ImagePath}のVLM推論が完了しました");
                        }
                        catch (OperationCanceledException)
                        {
                            AddMainLogEntry("処理がキャンセルされました");
                        }
                        catch (Exception ex)
                        {
                            AddMainLogEntry($"{imageInfo.ImagePath}のVLM推論中にエラーが発生しました: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(batchTasks);

                    if (_cts.Token.IsCancellationRequested)
                        break;
                }

                AddMainLogEntry("すべての画像に対するVLM推論が完了しました");
            }
            catch (InvalidOperationException ex)
            {
                AddMainLogEntry($"VLM推論エラー: {ex.Message}");
                MessageBox.Show(ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                AddMainLogEntry($"VLM推論中にエラーが発生しました: {ex.Message}");
                MessageBox.Show($"エラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 処理が完了したらボタンを再度有効化
                _isAsyncProcessing = false;
                VLMPredictAllButton.IsEnabled = true;
                UpdateProgressBar(0);
            }
        }

        // VLM推論
        private async Task<List<string>> PredictVLMTagsAsync(ImageInfo imageInfo, CancellationToken cancellationToken)
        {
            AddMainLogEntry("VLM推論を開始します");

            try
            {
                float generalThreshold = 0.35f;
                float characterThreshold = 0.85f;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    generalThreshold = (float)GeneralThresholdSlider.Value;
                    characterThreshold = (float)CharacterThresholdSlider.Value;
                });
                var (generalTags, rating, characters, allTags) = await Task.Run(() => _vlmPredictor.Predict(
                    new BitmapImage(new Uri(imageInfo.ImagePath)),
                    generalThreshold, // generalThresh
                    false, // generalMcutEnabled
                    characterThreshold, // characterThresh
                    false  // characterMcutEnabled
                ), cancellationToken);

                // 結果を表示または処理する
                await Dispatcher.InvokeAsync(() => AddMainLogEntry($"VLM推論結果: {generalTags}"));

                // generalTagsが空の場合は空のリストを返す
                if (string.IsNullOrWhiteSpace(generalTags)) { return new List<string>(); }

                // generalTagsとcharactersを結合して返す
                var predictedTags = generalTags.Split(',').Select(t => t.Trim()).ToList();
                predictedTags.AddRange(characters.Keys);
                return predictedTags;
            }
            catch (Exception ex)
            {
                AddMainLogEntry($"VLM推論中にエラーが発生しました: {ex.Message}");
                throw;
            }
        }

        // VLMログの更新
        private void UpdateVLMLog(object sender, string log)
        {
            Dispatcher.Invoke(() =>
            {
                AddDebugLogEntry("UpdateVLMLog");
                // ログエントリの数が最大数を超えた場合、古いエントリを削除
                var lines = VLMLogTextBox.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= MaxLogEntries)
                {
                    VLMLogTextBox.Text = string.Join(Environment.NewLine, lines.Take(MaxLogEntries - 1));
                }
                
                // 新しいログを上に追加
                VLMLogTextBox.Text = log + Environment.NewLine + VLMLogTextBox.Text;
            });
        }

        /*
        ここまでVLM関連メソッド
        ここからタグカテゴリ関連メソッド
        */

        private void LoadTagCategories()
        {
            _defaultTagCategories = LoadCategoriesFromFiles(DefaultCategoryFiles);
            _customTagCategories = LoadCategoriesFromFiles(CustomCategoryFiles);

            UpdateTagCategories();
        }

        private Dictionary<string, TagCategory> LoadCategoriesFromFiles(string[] files)
        {
            var categories = new Dictionary<string, TagCategory>();
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var file in files)
            {
                try
                {
                    string fullPath = Path.Combine(baseDirectory, file);
                    if (!File.Exists(fullPath))
                    {
                        AddMainLogEntry($"ファイルが見つかりません: {fullPath}");
                        continue;
                    }

                    string jsonContent = File.ReadAllText(fullPath);
                    var tagDictionary = JsonSerializer.Deserialize<Dictionary<string, int>>(jsonContent);
                    
                    if (tagDictionary == null)
                    {
                        AddMainLogEntry($"{file}の読み込み中にエラーが発生しました: デシリアライズ結果がnullです。");
                        continue;
                    }

                    string categoryName = Path.GetFileNameWithoutExtension(file);
                    
                    // タグ名のアンダースコアをスペースに置換
                    var updatedTags = new Dictionary<string, int>();
                    foreach (var tag in tagDictionary)
                    {
                        string updatedTagName = tag.Key.Replace('_', ' ');
                        updatedTags[updatedTagName] = tag.Value;
                    }

                    categories[categoryName] = new TagCategory { Tags = updatedTags };

                    AddMainLogEntry($"{categoryName}カテゴリのタグを読み込みました。タグ数: {updatedTags.Count}");
                }
                catch (Exception ex)
                {
                    AddMainLogEntry($"{file}の読み込み中にエラーが発生しました: {ex.Message}");
                }
            }

            return categories;
        }

        private void UpdateTagCategories()
        {
            if (_tagCategories == null) { return; }
            _tagCategories.Clear();

            if (_defaultTagCategories != null)
            {
                foreach (var category in _defaultTagCategories)
                {
                    _tagCategories[category.Key] = category.Value;
                }
            }

            if (_useCustomCategories && _customTagCategories != null)
            {
                foreach (var category in _customTagCategories)
                {
                    _tagCategories[category.Key] = category.Value;
                }
            }

            UpdateTagCategoryListView();
        }

        private void UpdateTagCategoryListView()
        {
            var allCategories = _tagCategories.Keys.ToList();
            if (!allCategories.Contains("Unknown"))
            {
                allCategories.Add("Unknown");
            }

            var orderedCategories = _prefixOrder.Concat(_suffixOrder).ToList();
            var remainingCategories = allCategories.Except(orderedCategories).ToList();
            
            _tagCategoryNames.Clear();
            foreach (var category in _prefixOrder)
            {
                _tagCategoryNames.Add(new CategoryItem { Name = category, OrderType = "Prefix" });
            }
            foreach (var category in remainingCategories)
            {
                _tagCategoryNames.Add(new CategoryItem { Name = category, OrderType = "" });
            }
            foreach (var category in _suffixOrder)
            {
                _tagCategoryNames.Add(new CategoryItem { Name = category, OrderType = "Suffix" });
            }
        }

        private void UseCustomCategoriesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _useCustomCategories = UseCustomCategoriesCheckBox.IsChecked ?? false;
            UpdateTagCategories();
            UpdateUIAfterImageInfosChange();
        }

        private string GetTagCategory(string tag)
        {
            if (_useCustomCategories)
            {
                foreach (var category in _customTagCategories)
                {
                    if (category.Value.Tags.ContainsKey(tag))
                    {
                        return category.Key;
                    }
                }
            }

            foreach (var category in _defaultTagCategories)
            {
                if (category.Value.Tags.ContainsKey(tag))
                {
                    return category.Key;
                }
            }

            return "Unknown";
        }

        private void MoveToPrefix_Click(object sender, RoutedEventArgs e)
        {
            if (TagCategoryListView.SelectedItem is CategoryItem selectedCategory)
            {
                _prefixOrder.Remove(selectedCategory.Name);
                _suffixOrder.Remove(selectedCategory.Name);
                _prefixOrder.Add(selectedCategory.Name);
                UpdateTagCategoryListView();
            }
        }

        private void MoveToSuffix_Click(object sender, RoutedEventArgs e)
        {
            if (TagCategoryListView.SelectedItem is CategoryItem selectedCategory)
            {
                _prefixOrder.Remove(selectedCategory.Name);
                _suffixOrder.Remove(selectedCategory.Name);
                _suffixOrder.Add(selectedCategory.Name);
                UpdateTagCategoryListView();
            }
        }

        private void RemoveFromOrders_Click(object sender, RoutedEventArgs e)
        {
            if (TagCategoryListView.SelectedItem is CategoryItem selectedCategory)
            {
                _prefixOrder.Remove(selectedCategory.Name);
                _suffixOrder.Remove(selectedCategory.Name);
                UpdateTagCategoryListView();
            }
        }

        // 選択された画像のタグをカテゴリ順に並び替え
        private void SortByCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedImage = ImageListBox.SelectedItem as ImageInfo;
            if (selectedImage != null)
            {
                SortImageTagsByCategory(selectedImage);
                UpdateUIAfterImageInfosChange();
            }
        }
        
        // すべての画像のタグをカテゴリ順に並び替え
        // private async void SortByCategoryAllButton_Click(object sender, RoutedEventArgs e)
        // {
        //     if (_isAsyncProcessing) { return; }
        //     _isAsyncProcessing = true;

        //     ProgressBar.Visibility = Visibility.Visible;
        //     ProgressBar.Value = 0;

        //     _cts = new CancellationTokenSource();

        //     try
        //     {
        //         await Task.Run(() =>
        //         {
        //             int totalImages = _imageInfos.Count;
        //             for (int i = 0; i < totalImages; i++)
        //             {
        //                 if (_cts.Token.IsCancellationRequested)
        //                     break;

        //                 var image = _imageInfos[i];
        //                 SortImageTagsByCategory(image);

        //                 Dispatcher.Invoke(() =>
        //                 {
        //                     ProgressBar.Value = (i + 1) * 100 / totalImages;
        //                 });
        //             }
        //         }, _cts.Token);
        //     }
        //     catch (OperationCanceledException)
        //     {
        //         AddMainLogEntry("タグの並び替えがキャンセルされました。");
        //     }
        //     finally
        //     {
        //         _isAsyncProcessing = false;
        //         ProgressBar.Visibility = Visibility.Collapsed;
        //         UpdateUIAfterImageInfosChange();
        //     }
        // }

        private async void SortByCategoryAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAsyncProcessing) { return; }
            _isAsyncProcessing = true;

            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;

            _cts = new CancellationTokenSource();

            try
            {
                await Task.Run(() =>
                {
                    int totalImages = _imageInfos.Count;
                    for (int i = 0; i < totalImages; i++)
                    {
                        if (_cts.Token.IsCancellationRequested)
                            break;

                        var image = _imageInfos[i];
                        SortImageTagsByCategory(image);

                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = (i + 1) * 100 / totalImages;
                        });
                    }
                }, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                AddMainLogEntry("タグの並び替えがキャンセルされました。");
            }
            finally
            {
                _isAsyncProcessing = false;
                ProgressBar.Visibility = Visibility.Collapsed;
                Dispatcher.Invoke(() =>
                {
                    UpdateUIAfterImageInfosChange();
                });
            }
        }

        private void SortImageTagsByCategory(ImageInfo image)
        {
            var prefixTags = new List<string>();
            var suffixTags = new List<string>();
            var remainingTags = new List<string>(image.Tags);

            var tagMoves = new List<TagPositionInfo>();

            // Prefix tags
            foreach (var category in _prefixOrder)
            {
                var categoryTags = remainingTags.Where(tag => GetTagCategory(tag) == category).ToList();
                foreach (var tag in categoryTags)
                {
                    int sourceIndex = image.Tags.IndexOf(tag);
                    int targetIndex = prefixTags.Count;
                    tagMoves.Add(new TagPositionInfo { Tag = tag, Position = sourceIndex });
                    tagMoves.Add(new TagPositionInfo { Tag = tag, Position = targetIndex });
                }
                prefixTags.AddRange(categoryTags);
                remainingTags.RemoveAll(tag => categoryTags.Contains(tag));
            }

            // Suffix tags
            foreach (var category in _suffixOrder.AsEnumerable().Reverse())
            {
                var categoryTags = remainingTags.Where(tag => GetTagCategory(tag) == category).ToList();
                foreach (var tag in categoryTags)
                {
                    int sourceIndex = image.Tags.IndexOf(tag);
                    int targetIndex = prefixTags.Count + remainingTags.Count;
                    tagMoves.Add(new TagPositionInfo { Tag = tag, Position = sourceIndex });
                    tagMoves.Add(new TagPositionInfo { Tag = tag, Position = targetIndex });
                }
                suffixTags.InsertRange(0, categoryTags);
                remainingTags.RemoveAll(tag => categoryTags.Contains(tag));
            }

            var newTags = prefixTags.Concat(remainingTags).Concat(suffixTags).ToList();

            var action = new TagGroupAction
            {
                Image = image,
                TagInfos = tagMoves,
                IsAdd = false,
                DoAction = () =>
                {
                    image.Tags = newTags;
                    Dispatcher.Invoke(() =>
                    {
                        UpdateUIAfterImageInfosChange();
                        AddMainLogEntry($"画像 '{image.ImagePath}' のタグをカテゴリ順に並び替えました");
                    });
                },
                UndoAction = () =>
                {
                    image.Tags = new List<string>(image.Tags);
                    Dispatcher.Invoke(() =>
                    {
                        UpdateUIAfterImageInfosChange();
                        AddMainLogEntry($"画像 '{image.ImagePath}' のタグの並び替えを元に戻しました");
                    });
                },
                Description = $"画像 '{image.ImagePath}' のタグをカテゴリ順に並び替え"
            };

            action.DoAction();
            _undoStack.Push(action);
            _redoStack.Clear();
            Dispatcher.Invoke(() =>
            {
                UpdateButtonStates();
            });
        }

        /*
        ここまでタグカテゴリ関連メソッド
        */
    }
}