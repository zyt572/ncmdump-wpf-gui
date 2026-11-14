using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NcmGui.Models;

namespace NcmGui.Core;

public sealed record ConversionResult(FileStatus Status, string Message, string Output, long Bytes, double Seconds);

/// <summary>
/// 调用同目录下的 ncmdump.exe 完成解密。
/// 它的退出码不可靠（失败也返回 0），所以结果以输出内容和新增文件为准。
/// </summary>
public static class ConversionEngine
{
    private static readonly Regex AnsiRegex = new("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex ArrowRegex = new("'([^']*)'\\s*->\\s*'([^']*)'", RegexOptions.Compiled);
    private static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".ogg" };

    public static string? BinaryPath { get; private set; }

    private static readonly (string FileName, string ResourceName)[] BundledFiles =
    {
        ("ncmdump.exe", "NcmGui.Resources.ncmdump.exe"),
        ("libncmdump.dll", "NcmGui.Resources.libncmdump.dll"),
    };

    public static bool Locate()
    {
        BinaryPath = FindByDirectory();
        BinaryPath ??= ExtractBundled();
        return BinaryPath is not null;
    }

    private static string? FindByDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 5 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "ncmdump.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 单文件发布时 ncmdump 被打包进程序集，首次运行时释放到 LocalAppData。
    /// </summary>
    private static string? ExtractBundled()
    {
        try
        {
            var assembly = typeof(ConversionEngine).Assembly;
            var version = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NcmGui", "bin", version);
            Directory.CreateDirectory(folder);

            foreach (var (fileName, resourceName) in BundledFiles)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                var target = System.IO.Path.Combine(folder, fileName);
                if (File.Exists(target) && new FileInfo(target).Length == stream.Length) continue;
                using var file = File.Create(target);
                stream.CopyTo(file);
            }

            var binary = System.IO.Path.Combine(folder, "ncmdump.exe");
            return File.Exists(binary) ? binary : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> ReadVersionAsync()
    {
        if (BinaryPath is null) return string.Empty;
        try
        {
            var psi = new ProcessStartInfo(BinaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-v");
            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;
            var text = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var match = Regex.Match(text, @"\d+(\.\d+)+");
            return match.Success ? match.Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static async Task<ConversionResult> RunAsync(string src, string? outDir, bool remove, CancellationToken token)
    {
        // ncmdump 只认小写 .ncm，其它写法会被静默跳过
        if (!src.EndsWith(".ncm", StringComparison.Ordinal))
            return new ConversionResult(FileStatus.Error, "ncmdump 只识别小写的 .ncm 后缀，请重命名文件", string.Empty, 0, 0);

        if (BinaryPath is null)
            return new ConversionResult(FileStatus.Error, "找不到 ncmdump.exe", string.Empty, 0, 0);

        var watchDir = string.IsNullOrEmpty(outDir) ? System.IO.Path.GetDirectoryName(src)! : outDir;
        var before = Snapshot(watchDir);

        var psi = new ProcessStartInfo(BinaryPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(src);
        if (!string.IsNullOrEmpty(outDir))
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outDir);
        }
        if (remove)
        {
            psi.ArgumentList.Add("-m");
        }

        var sw = Stopwatch.StartNew();
        string text;
        using (var proc = new Process { StartInfo = psi })
        {
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                return new ConversionResult(FileStatus.Error, $"启动 ncmdump 失败：{ex.Message}", string.Empty, 0, sw.Elapsed.TotalSeconds);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var outTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
                if (token.IsCancellationRequested) throw;
                return new ConversionResult(FileStatus.Error, "转换超时（15 分钟）", string.Empty, 0, sw.Elapsed.TotalSeconds);
            }

            text = await outTask + await errTask;
        }

        return Parse(text, src, outDir, before, sw.Elapsed.TotalSeconds);
    }

    private static ConversionResult Parse(string raw, string src, string? outDir, Dictionary<string, long> before, double seconds)
    {
        var text = AnsiRegex.Replace(raw, string.Empty).Trim();
        var matches = ArrowRegex.Matches(text);
        var output = matches.Count > 0 ? matches[^1].Groups[2].Value : string.Empty;
        if (output.Length > 0 && !System.IO.Path.IsPathRooted(output))
            output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(src)!, output);

        var exists = output.Length > 0 && File.Exists(output);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var warns = lines.Where(l => l.Contains("[Warn]")).ToList();
        var errors = lines.Where(l => l.Contains("[Error]") || l.Contains("[Exception]")).ToList();

        if (exists)
        {
            var note = warns.Count > 0 ? StripTag(warns[^1]) : string.Empty;
            return new ConversionResult(FileStatus.Ok, note, output, new FileInfo(output).Length, seconds);
        }

        if (errors.Count > 0)
        {
            var message = StripTag(errors[^1]);
            message = Regex.Replace(message, @"\s*'[^']*'\s*$", string.Empty).Trim();
            return new ConversionResult(FileStatus.Error, message.Length > 0 ? message : errors[^1], string.Empty, 0, seconds);
        }

        // 输出文件名可能被改成「歌手 - 歌名」，所以再比对一次目录里新增的音频
        var watchDir = string.IsNullOrEmpty(outDir) ? System.IO.Path.GetDirectoryName(src)! : outDir;
        var stem = System.IO.Path.GetFileNameWithoutExtension(src);
        var found = FindNewAudio(before, watchDir, stem);
        if (found is not null)
            return new ConversionResult(FileStatus.Ok, string.Empty, found, new FileInfo(found).Length, seconds);

        if (text.Contains("[Done]"))
            return new ConversionResult(FileStatus.Warn, "转换完成，但没有找到输出文件", string.Empty, 0, seconds);

        var last = lines.Count > 0 ? StripTag(lines[^1]) : "ncmdump 没有输出，可能不是有效的 NCM 文件";
        return new ConversionResult(FileStatus.Error, last, string.Empty, 0, seconds);
    }

    private static string StripTag(string line)
        => Regex.Replace(line, "^\\[(Warn|Error|Exception)\\]\\s*", string.Empty).Trim();

    private static Dictionary<string, long> Snapshot(string directory)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!AudioExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())) continue;
                try { result[System.IO.Path.GetFileName(file)] = File.GetLastWriteTimeUtc(file).Ticks; }
                catch { /* 忽略无法访问的文件 */ }
            }
        }
        catch { /* 目录不可读就算了 */ }
        return result;
    }

    private static string? FindNewAudio(Dictionary<string, long> before, string directory, string stem)
    {
        var current = Snapshot(directory);
        var fresh = current.Where(kv => !before.TryGetValue(kv.Key, out var ticks) || ticks != kv.Value)
                           .Select(kv => kv.Key)
                           .ToList();
        var exact = fresh.FirstOrDefault(name => System.IO.Path.GetFileNameWithoutExtension(name)
            .Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return System.IO.Path.Combine(directory, exact);
        return fresh.Count == 1 ? System.IO.Path.Combine(directory, fresh[0]) : null;
    }
}
