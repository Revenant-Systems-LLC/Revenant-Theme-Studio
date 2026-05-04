using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class UpscalingService : IDisposable
    {
        private InferenceSession? _session;
        private readonly string _modelPath;
        private const int TileSize = 128;
        private const int TilePad = 10;
        private const int ScaleFactor = 4;

        public bool IsModelLoaded => _session != null;

        public UpscalingService()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelPath = Path.Combine(appDir, "Assets", "Models", "realesrgan-x4plus.onnx");
        }

        public UpscalingService(string modelPath)
        {
            _modelPath = modelPath;
        }

        public void LoadModel()
        {
            if (_session != null) return;
            if (!File.Exists(_modelPath))
                throw new FileNotFoundException("Real-ESRGAN model not found. Place realesrgan-x4plus.onnx in Assets/Models/.", _modelPath);

            var options = new SessionOptions();
            try
            {
                options.AppendExecutionProvider_DML(0);
            }
            catch
            {
                // DirectML not available — fall back to CPU
            }

            _session = new InferenceSession(_modelPath, options);
        }

        public async Task<string> UpscaleImageAsync(string inputPath, int targetWidth, int targetHeight,
            CancellationToken ct = default, IProgress<int>? progress = null)
        {
            LoadModel();

            return await Task.Run(() =>
            {
                var bitmap = LoadImage(inputPath);
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;

                float scaleNeeded = Math.Max(
                    (float)targetWidth / width,
                    (float)targetHeight / height);

                if (scaleNeeded <= 1.0f)
                {
                    progress?.Report(100);
                    return inputPath;
                }

                var pixels = GetPixelData(bitmap);
                var upscaled = RunTiledInference(pixels, width, height, ct, progress);

                int outW = width * ScaleFactor;
                int outH = height * ScaleFactor;
                var outputPath = Path.Combine(
                    Path.GetTempPath(),
                    $"rts_upscaled_{Guid.NewGuid():N}.png");

                SaveImage(upscaled, outW, outH, outputPath);
                progress?.Report(100);
                return outputPath;
            }, ct);
        }

        private float[] RunTiledInference(byte[] pixels, int width, int height,
            CancellationToken ct, IProgress<int>? progress)
        {
            int outW = width * ScaleFactor;
            int outH = height * ScaleFactor;
            var output = new float[outH * outW * 3];

            int tilesX = (int)Math.Ceiling((double)width / TileSize);
            int tilesY = (int)Math.Ceiling((double)height / TileSize);
            int totalTiles = tilesX * tilesY;
            int tilesDone = 0;

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    ct.ThrowIfCancellationRequested();

                    int x0 = tx * TileSize;
                    int y0 = ty * TileSize;
                    int tileW = Math.Min(TileSize, width - x0);
                    int tileH = Math.Min(TileSize, height - y0);

                    int padL = Math.Min(TilePad, x0);
                    int padT = Math.Min(TilePad, y0);
                    int padR = Math.Min(TilePad, width - x0 - tileW);
                    int padB = Math.Min(TilePad, height - y0 - tileH);

                    int inX = x0 - padL;
                    int inY = y0 - padT;
                    int inW = tileW + padL + padR;
                    int inH = tileH + padT + padB;

                    var tileInput = ExtractTile(pixels, width, height, inX, inY, inW, inH);
                    var tileOutput = RunInference(tileInput, inW, inH);

                    int outPadL = padL * ScaleFactor;
                    int outPadT = padT * ScaleFactor;
                    int cropW = tileW * ScaleFactor;
                    int cropH = tileH * ScaleFactor;
                    int dstX = x0 * ScaleFactor;
                    int dstY = y0 * ScaleFactor;
                    int srcTotalW = inW * ScaleFactor;

                    for (int row = 0; row < cropH; row++)
                    {
                        for (int col = 0; col < cropW; col++)
                        {
                            int srcIdx = ((row + outPadT) * srcTotalW + (col + outPadL)) * 3;
                            int dstIdx = ((dstY + row) * outW + (dstX + col)) * 3;

                            if (dstIdx + 2 < output.Length && srcIdx + 2 < tileOutput.Length)
                            {
                                output[dstIdx] = tileOutput[srcIdx];
                                output[dstIdx + 1] = tileOutput[srcIdx + 1];
                                output[dstIdx + 2] = tileOutput[srcIdx + 2];
                            }
                        }
                    }

                    tilesDone++;
                    progress?.Report((int)(tilesDone * 95.0 / totalTiles));
                }
            }

            return output;
        }

        private float[] RunInference(float[] input, int width, int height)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 3;
                    tensor[0, 0, y, x] = input[idx];
                    tensor[0, 1, y, x] = input[idx + 1];
                    tensor[0, 2, y, x] = input[idx + 2];
                }
            }

            var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", tensor) };
            using var results = _session!.Run(inputs);
            var outputTensor = results[0].AsTensor<float>();

            int outH = height * ScaleFactor;
            int outW = width * ScaleFactor;
            var output = new float[outH * outW * 3];

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int idx = (y * outW + x) * 3;
                    output[idx] = Math.Clamp(outputTensor[0, 0, y, x], 0f, 1f);
                    output[idx + 1] = Math.Clamp(outputTensor[0, 1, y, x], 0f, 1f);
                    output[idx + 2] = Math.Clamp(outputTensor[0, 2, y, x], 0f, 1f);
                }
            }

            return output;
        }

        private static float[] ExtractTile(byte[] pixels, int imgW, int imgH,
            int x, int y, int w, int h)
        {
            var tile = new float[w * h * 3];
            for (int row = 0; row < h; row++)
            {
                for (int col = 0; col < w; col++)
                {
                    int srcRow = Math.Clamp(y + row, 0, imgH - 1);
                    int srcCol = Math.Clamp(x + col, 0, imgW - 1);
                    int srcIdx = (srcRow * imgW + srcCol) * 4;
                    int dstIdx = (row * w + col) * 3;

                    tile[dstIdx] = pixels[srcIdx + 2] / 255f;     // R (BGRA → RGB)
                    tile[dstIdx + 1] = pixels[srcIdx + 1] / 255f; // G
                    tile[dstIdx + 2] = pixels[srcIdx] / 255f;     // B
                }
            }
            return tile;
        }

        private static BitmapSource LoadImage(string path)
        {
            var uri = new Uri(path, UriKind.Absolute);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            if (image.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                return converted;
            }
            return image;
        }

        private static byte[] GetPixelData(BitmapSource bitmap)
        {
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static void SaveImage(float[] data, int width, int height, string path)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int srcIdx = i * 3;
                int dstIdx = i * 4;
                pixels[dstIdx] = (byte)(Math.Clamp(data[srcIdx + 2], 0f, 1f) * 255);     // B
                pixels[dstIdx + 1] = (byte)(Math.Clamp(data[srcIdx + 1], 0f, 1f) * 255); // G
                pixels[dstIdx + 2] = (byte)(Math.Clamp(data[srcIdx], 0f, 1f) * 255);     // R
                pixels[dstIdx + 3] = 255;                                                  // A
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, width * 4);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
