using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Imaging;

namespace tagmane
{
    public class VLMPredictor
    {
        private InferenceSession _model;
        private List<string> _tagNames;
        private List<int> _ratingIndexes;
        private List<int> _generalIndexes;
        private List<int> _characterIndexes;
        private int _modelTargetSize;
        private const int MaxLogEntries = 20; // ログエントリの最大数を定義

        private const string MODEL_FILENAME = "model.onnx";
        private const string LABEL_FILENAME = "selected_tags.csv";

        public ObservableCollection<string> VLMLogEntries { get; } = new ObservableCollection<string>();

        public event EventHandler<string> LogUpdated;

        private List<string> _vlmLogEntries = new List<string>();
        private void AddLogEntry(string message)
        {
            lock (_vlmLogEntries)
            {
                string logMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
                _vlmLogEntries.Insert(0, logMessage);
                while (_vlmLogEntries.Count > MaxLogEntries)
                {
                    _vlmLogEntries.RemoveAt(_vlmLogEntries.Count - 1);
                }
                
                // メインウィンドウにログを通知
                LogUpdated?.Invoke(this, logMessage);
            }
        }

        public async Task LoadModel(string modelRepo)
        {
            AddLogEntry($"リポジトリからモデルを読み込みます: {modelRepo}");
            var (csvPath, modelPath) = await DownloadModel(modelRepo);

            _tagNames = new List<string>();
            _ratingIndexes = new List<int>();
            _generalIndexes = new List<int>();
            _characterIndexes = new List<int>();

            LoadLabels(csvPath);

            AddLogEntry("ONNX推論セッションを初期化しています");
            _model = new InferenceSession(modelPath);
            // _modelTargetSize = _model.InputMetadata["input"].Dimensions[2];
            _modelTargetSize = _model.InputMetadata.First().Value.Dimensions[2];
            AddLogEntry($"モデルの読み込みが完了しました。ターゲットサイズ: {_modelTargetSize}");
        }

        private async Task<(string, string)> DownloadModel(string modelRepo)
        {
            using var client = new HttpClient();
            var modelDir = Path.Combine(Path.GetTempPath(), "tagmane", modelRepo.Split('/').Last());
            Directory.CreateDirectory(modelDir);
            var csvPath = Path.Combine(modelDir, LABEL_FILENAME);
            var modelPath = Path.Combine(modelDir, MODEL_FILENAME);
            AddLogEntry($"モデルファイルのダウンロードを開始します: {modelRepo}");
            AddLogEntry($"辞書ダウンロードパス: {csvPath}");
            AddLogEntry($"モデルダウンロードパス: {modelPath}");

            if (!File.Exists(csvPath) || !File.Exists(modelPath))
            {
                await DownloadFileWithRetry(client, $"https://huggingface.co/{modelRepo}/resolve/main/{LABEL_FILENAME}", csvPath);
                var progress = new Progress<double>(p => Application.Current.Dispatcher.Invoke(() => ((MainWindow)Application.Current.MainWindow).ProgressBar.Value = p * 100));
                await DownloadFileWithRetry(client, $"https://huggingface.co/{modelRepo}/resolve/main/{MODEL_FILENAME}", modelPath, progress: progress);

                if (!VerifyFileIntegrity(modelPath))
                {
                    throw new Exception("ダウンロードされたモデルファイルが破損しているか不完全です。");
                }
                AddLogEntry("モデルファイルを新たにダウンロードしました。");
            }
            else
            {
                AddLogEntry("既存のモデルファイルを使用します。");
                Application.Current.Dispatcher.Invoke(() => ((MainWindow)Application.Current.MainWindow).ProgressBar.Value = 100);
            }

            // プログレスバーをリセット
            Application.Current.Dispatcher.Invoke(() => ((MainWindow)Application.Current.MainWindow).ProgressBar.Value = 0);

            return (csvPath, modelPath);
        }

        private async Task DownloadFileWithRetry(HttpClient client, string url, string filePath, int maxRetries = 3, IProgress<double> progress = null)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    AddLogEntry($"ファイルをダウンロードしています: {url}");
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;
                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var totalRead = 0L;
                    var buffer = new byte[8192];
                    var isMoreToRead = true;

                    do
                    {
                        var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            isMoreToRead = false;
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, read);

                            totalRead += read;
                            if (totalBytes.HasValue)
                            {
                                var progressPercentage = (double)totalRead / totalBytes.Value;
                                progress?.Report(progressPercentage);
                            }
                        }
                    }
                    while (isMoreToRead);

                    AddLogEntry($"ファイルのダウンロードが完了しました: {filePath}");
                    return;
                }
                catch (Exception ex)
                {
                    if (i == maxRetries - 1)
                        throw new Exception($"{maxRetries}回の試行後、ファイルのダウンロードに失敗しました: {ex.Message}");
                    AddLogEntry($"ダウンロード試行 {i + 1} 回目が失敗しました。再試行します...");
                }
                await Task.Delay(1000 * (i + 1)); // 指数バックオフ
            }
        }

        private bool VerifyFileIntegrity(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var sha256 = SHA256.Create();
                byte[] hash = sha256.ComputeHash(stream);
                AddLogEntry($"ファイルの整合性チェックに成功しました: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                AddLogEntry($"ファイルの整合性チェックに失敗しました: {ex.Message}");
                return false;
            }
        }

        private async Task DownloadFile(HttpClient client, string url, string filePath)
        {
            using var response = await client.GetAsync(url);
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create);
            await stream.CopyToAsync(fileStream);
        }

        private void LoadLabels(string csvPath)
        {
            var lines = File.ReadAllLines(csvPath).Skip(1);
            foreach (var (line, index) in lines.Select((l, i) => (l, i)))
            {
                var parts = line.Split(',');
                var name = parts[1].Replace("_", " ");
                _tagNames.Add(name);

                var category = int.Parse(parts[2]);
                switch (category)
                {
                    case 9:
                        _ratingIndexes.Add(index);
                        break;
                    case 0:
                        _generalIndexes.Add(index);
                        break;
                    case 4:
                        _characterIndexes.Add(index);
                        break;
                }
            }
        }

        public (string, Dictionary<string, float>, Dictionary<string, float>, Dictionary<string, float>) Predict(
            BitmapImage image,
            float generalThresh,
            bool generalMcutEnabled,
            float characterThresh,
            bool characterMcutEnabled)
        {
            AddLogEntry("VLMログ：推論を開始します");
            AddLogEntry($"generalThresh: {generalThresh}");
            AddLogEntry($"generalMcutEnabled: {generalMcutEnabled}");
            AddLogEntry($"characterThresh: {characterThresh}");
            AddLogEntry($"characterMcutEnabled: {characterMcutEnabled}");

            var inputTensor = PrepareImage(image);
            AddLogEntry($"inputTensor: {inputTensor[0, 1, 1, 0]}");
            // var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", inputTensor) };
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_model.InputMetadata.First().Key, inputTensor) };
            var output = _model.Run(inputs);
            var predictions = output.First().AsEnumerable<float>().ToArray();

            var labels = _tagNames.Zip(predictions, (name, pred) => (name, pred)).ToList();

            var rating = _ratingIndexes.Select(i => labels[i]).ToDictionary(x => x.name, x => x.pred);
            var general = GetFilteredTags(_generalIndexes, labels, generalThresh, generalMcutEnabled);
            var characters = GetFilteredTags(_characterIndexes, labels, characterThresh, characterMcutEnabled);

            var sortedGeneralStrings = string.Join(", ", general.OrderByDescending(x => x.Value).Select(x => x.Key));

            return (sortedGeneralStrings, rating, characters, general);
        }

        private DenseTensor<float> PrepareImage(BitmapImage image)
        {
            AddLogEntry("VLMログ：画像を準備しています");

            // 画像のサイズを取得
            int width = image.PixelWidth;
            int height = image.PixelHeight;

            // 画像のサイズがモデルのターゲットサイズと一致しない場合、リサイズ
            if (width != _modelTargetSize || height != _modelTargetSize)
            {
                image = ResizeImage(image, _modelTargetSize, _modelTargetSize);
            }

            AddLogEntry("VLMログ：画像のリサイズが完了しました");
            // 画像をバイト配列に変換
            byte[] pixels = new byte[4 * _modelTargetSize * _modelTargetSize];
            image.CopyPixels(pixels, 4 * _modelTargetSize, 0);
            // テンソルを作成
            var tensor = new DenseTensor<float>(new[] { 1, _modelTargetSize, _modelTargetSize, 3 });

            // ピクセルデータをテンソルに変換
            for (int y = 0; y < _modelTargetSize; y++)
            {
                for (int x = 0; x < _modelTargetSize; x++)
                {

                    int i = (y * _modelTargetSize + x) * 4;
                    tensor[0, y, x, 2] = pixels[i + 2];     // R
                    tensor[0, y, x, 1] = pixels[i + 1];     // G
                    tensor[0, y, x, 0] = pixels[i];         // B
                }
            }

            AddLogEntry("VLMログ：画像をテンソルに変換しました");

            // テンソルを画像に変換
            // BitmapSource previewImage = TensorToBitmapSource(tensor);
            // AddLogEntry("VLMログ：画像を準備しました");

            // // UI スレッドで MessageBox を表示
            // Application.Current.Dispatcher.Invoke(() =>
            // {
            //     var window = new Window
            //     {
            //         Title = "推論前の画像プレビュー",
            //         SizeToContent = SizeToContent.WidthAndHeight,
            //         WindowStartupLocation = WindowStartupLocation.CenterScreen,
            //         Content = new System.Windows.Controls.Image { Source = previewImage, Stretch = System.Windows.Media.Stretch.None }
            //     };
            //     window.ShowDialog();
            // });

            return tensor;
        }

        private BitmapImage ResizeImage(BitmapImage image, int width, int height)
        {
            // 新しいBitmapImageを作成
            BitmapImage resizedImage = new BitmapImage();
            resizedImage.BeginInit();
            resizedImage.CacheOption = BitmapCacheOption.OnLoad;
            resizedImage.UriSource = image.UriSource;
            resizedImage.DecodePixelWidth = width;
            resizedImage.DecodePixelHeight = height;
            // resizedImage.InterpolationMode = BitmapInterpolationMode.Cubic; // BICUBICで補完
            resizedImage.EndInit();
            resizedImage.Freeze(); // パフォーマンス向上のため

            return resizedImage;
        }

        private BitmapSource TensorToBitmapSource(DenseTensor<float> tensor)
        {
            int width = tensor.Dimensions[1];
            int height = tensor.Dimensions[2];
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    pixels[i + 2] = (byte)(tensor[0, y, x, 2]);     // R
                    pixels[i + 1] = (byte)(tensor[0, y, x, 1]);     // G
                    pixels[i] = (byte)(tensor[0, y, x, 0]);         // B
                    pixels[i + 3] = 255; // A (完全不透明)
                }
            }

            return BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        }

        private Dictionary<string, float> GetFilteredTags(
            List<int> indexes,
            List<(string name, float pred)> labels,
            float threshold,
            bool mcutEnabled)
        {
            var tags = indexes.Select(i => labels[i]).ToList();

            if (mcutEnabled)
            {
                threshold = McutThreshold(tags.Select(x => x.pred).ToArray());
            }

            return tags.Where(x => x.pred > threshold).ToDictionary(x => x.name, x => x.pred);
        }

        private float McutThreshold(float[] probs)
        {
            var sortedProbs = probs.OrderByDescending(x => x).ToArray();
            var diffs = sortedProbs.Zip(sortedProbs.Skip(1), (a, b) => a - b).ToArray();
            var t = Array.IndexOf(diffs, diffs.Max());
            return (sortedProbs[t] + sortedProbs[t + 1]) / 2;
        }
    }
}
