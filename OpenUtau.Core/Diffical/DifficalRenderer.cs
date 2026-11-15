using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Runtime.InteropServices;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Diffical {

    /// <summary>
    /// Diffical UTAU 引擎渲染器
    ///
    /// 实现 IRenderer 接口，通过 HTTP 与 Python 后端通信，
    /// 实现整句渲染（不是逐音素渲染）。
    ///
    /// 关键设计：
    /// - 不修改用户音高/力度/时长曲线（LoadRenderedPitch 返回用户输入）
    /// - 负责波形合成、音质增强、细节丰富
    /// - 支持音色融合、呼吸音过渡、整句流畅度等高级功能
    /// </summary>
    public class DifficalRenderer : IRenderer {

        public const string name = "DIFFICAL";
        public override string ToString() => name;

        /// <summary>
        /// 渲染缓存版本。后端（Python）渲染逻辑更新时递增，
        /// 使旧缓存失效——否则 phrase.hash 不变，用户永远听到旧音频。
        /// </summary>
        const string CacheVersion = "20261018handover2"; // amplitude-faithful coherence handover + periodic-vowel loudness; keep old caches

        static readonly HashSet<string> supportedExp = new HashSet<string>() {
            Format.Ustx.DYN,    // 力度
            Format.Ustx.PITD,   // 音高偏差
            Format.Ustx.CLR,    // 清亮度
            Format.Ustx.SHFT,   // 音高偏移
            Format.Ustx.VEL,    // 速度
            Format.Ustx.VOL,    // 音量
            Format.Ustx.MOD,    // 调制
            Format.Ustx.MODP,   // 调制加
            Format.Ustx.ALT,    // 替换
            Format.Ustx.GENC,   // 性别
            Format.Ustx.BREC,   // 气声
            Format.Ustx.TENC,   // 张力
            Format.Ustx.VOIC,   // 浊音度
            Format.Ustx.DIR,    // 直通
            // Diffical 扩展表达式
            "GWL",       // 咆哮强度（Growl）
            "LOWC",      // 动态低切（Low Cut）
            "BREL",      // 低频气声（Breath Low）
            "BREH",      // 高频气声（Breath High）
            "WARM",      // 温暖度（Warmth）
            "HCMP",      // 谐波压缩（Harmonic Compressor）
            "TBRF",      // 音色融合（Timbre Fusion）
            "BRTH",      // 呼吸音（Breath）
            "DFL",       // 流畅度（Fluency）
            "HUM",       // 真人化（Humanization）
        };

        public USingerType SingerType => USingerType.Classic;

        /// <summary>
        /// 不回写渲染后的音高，完全尊重用户输入
        /// </summary>
        public bool SupportsRenderPitch => false;

        /// <summary>
        /// 不回写渲染后的实际曲线，完全尊重用户输入
        /// </summary>
        public bool SupportsRealCurve => false;

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return supportedExp.Contains(descriptor.abbr);
        }

        /// <summary>
        /// 整句时长布局
        /// </summary>
        public RenderResult Layout(RenderPhrase phrase) {
            return new RenderResult() {
                leadingMs = phrase.leadingMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.durationMs + phrase.leadingMs,
            };
        }

        /// <summary>
        /// 核心渲染方法
        /// 
        /// 将整句 RenderPhrase 序列化为 JSON，
        /// 发送到 Python 后端进行渲染，
        /// 返回整句 PCM。
        /// </summary>
        public Task<RenderResult> Render(
            RenderPhrase phrase,
            Progress progress,
            int trackNo,
            CancellationTokenSource cancellation,
            bool isPreRender = false,
            RenderPhraseEvents? renderEvents = null) {

            return Task.Run(async () => {
                cancellation.Token.ThrowIfCancellationRequested();
                var result = Layout(phrase);

                // 渲染状态（OpenUtau 底部进度条）：与其他渲染器一致，
                // 开始时报 0，结束时报 phrase.phones.Length。
                string progressInfo = $"Track {trackNo + 1} : Diffical \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
                progress.Complete(0, progressInfo);

                // 整句缓存：与 classic resampler（res-{hash}.wav）同模式——
                // 缓存文件由本渲染器管理，但通过 phrase.AddCacheFile 注册给
                // OpenUtau（导出/清理时统一处理）。缓存 key 含 CacheVersion，
                // Python 渲染逻辑更新时递增版本使旧缓存失效。
                string cachePath = Path.Join(
                    PathManager.Inst.CachePath,
                    $"diffical-{CacheVersion}-{phrase.hash:x16}.wav"
                );
                phrase.AddCacheFile(cachePath);

                // 检查缓存
                if (File.Exists(cachePath)) {
                    try {
                        using (var waveStream = Wave.OpenFile(cachePath)) {
                            result.samples = Wave.GetSamples(
                                waveStream.ToSampleProvider().ToMono(1, 0));
                        }
                        if (result.samples != null && result.samples.Length > 0) {
                            Log.Information($"Diffical 缓存命中: {CacheVersion}-{phrase.hash:x16}");
                            Renderers.ApplyDynamics(phrase, result);
                            progress.Complete(phrase.phones.Length, progressInfo);
                            cancellation.Token.ThrowIfCancellationRequested();
                            return result;
                        }
                    } catch (Exception e) when (e is not OperationCanceledException) {
                        Log.Error(e, "读取缓存失败，重新渲染");
                    }
                } else {
                    // 诊断：记录未命中原因，用户可据此判断是否重复渲染
                    Log.Information(
                        $"Diffical 缓存未命中，重新渲染: {CacheVersion}-{phrase.hash:x16} " +
                        $"preRender={isPreRender} cache={cachePath}");
                }

                // 构建渲染请求
                var request = CreateRenderRequest(phrase, isPreRender, trackNo);
                cancellation.Token.ThrowIfCancellationRequested();
                // Never dump private lyrics, voicebank paths or audio requests by default.
                var client = DifficalClient.Instance;
                var response = await client.Render(request, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();

                // 转换为 float[]
                result.samples = response.samples;

                // Publish only complete cache files; cancellation/failure cannot leave a truncated hit.
                string temporaryCache = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    var pcm16 = new short[result.samples.Length];
                    for (int i = 0; i < pcm16.Length; ++i) {
                        if ((i & 4095) == 0) cancellation.Token.ThrowIfCancellationRequested();
                        float sample = Math.Clamp(result.samples[i], -1f, 1f);
                        pcm16[i] = (short)Math.Round(sample * short.MaxValue);
                    }
                    byte[] bytes = MemoryMarshal.AsBytes(pcm16.AsSpan()).ToArray();
                    using (var writer = new WaveFileWriter(temporaryCache, new WaveFormat(44100, 16, 1))) {
                        writer.Write(bytes, 0, bytes.Length);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    File.Move(temporaryCache, cachePath, overwrite: true);
                } catch (Exception e) when (e is not OperationCanceledException) {
                    Log.Error(e, "保存缓存失败");
                } finally {
                    if (File.Exists(temporaryCache)) File.Delete(temporaryCache);
                }

                // 应用力度曲线
                if (result.samples != null) {
                    Renderers.ApplyDynamics(phrase, result);
                }

                progress.Complete(phrase.phones.Length, progressInfo);
                cancellation.Token.ThrowIfCancellationRequested();
                return result;
            }, cancellation.Token);
        }

        /// <summary>
        /// 返回用户输入的音高曲线（不修改）
        /// </summary>
        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            // 不回写音高，完全尊重用户输入
            return null;
        }

        /// <summary>
        /// 返回用户输入的表达式曲线（不修改）
        /// </summary>
        public List<RenderRealCurveResult> LoadRenderedRealCurves(RenderPhrase phrase) {
            // 不回写曲线，完全尊重用户输入
            return new List<RenderRealCurveResult>();
        }

        /// <summary>
        /// 建议表达式
        /// </summary>
        public UExpressionDescriptor[] GetSuggestedExpressions(
            USinger singer,
            URenderSettings renderSettings) {
            return new UExpressionDescriptor[] {
                // 咆哮强度
                new UExpressionDescriptor("Growl", "GWL", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 动态低切
                new UExpressionDescriptor("Low Cut", "LOWC", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 低频气声
                new UExpressionDescriptor("Breath Low", "BREL", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 高频气声
                new UExpressionDescriptor("Breath High", "BREH", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 温暖度
                new UExpressionDescriptor("Warmth", "WARM", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 谐波压缩
                new UExpressionDescriptor("Harmonic Compressor", "HCMP", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 音色融合
                new UExpressionDescriptor("Timbre Fusion", "TBRF", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 呼吸音
                new UExpressionDescriptor("Breath", "BRTH", 0, 100, 0) {
                    type = UExpressionType.Curve, isFlag = false,
                },
                // 流畅度
                new UExpressionDescriptor("Fluency", "DFL", 0, 100, 100) {
                    type = UExpressionType.Numerical, isFlag = false,
                },
                // 真人化
                new UExpressionDescriptor("Humanization", "HUM", 0, 100, 80) {
                    type = UExpressionType.Numerical, isFlag = false,
                },
            };
        }

        /// <summary>
        /// 构建 Python 后端的渲染请求
        /// </summary>
        public static DifficalRenderRequest CreateRenderRequest(
            RenderPhrase phrase,
            bool isPreRender,
            int trackNo) {

            // 从 phrase.curves 提取扩展曲线
            var curveMap = new Dictionary<string, float[]>();
            if (phrase.curves != null) {
                foreach (var c in phrase.curves) {
                    curveMap[c.Item1] = c.Item2;
                }
            }

            // 从 singer 获取语言信息
            string singerLanguage = "";
            string targetLanguage = "";
            if (phrase.singer != null) {
                // 从 DefaultPhonemizer 推断声库语言
                var defaultPhonemizer = phrase.singer.DefaultPhonemizer ?? "";
                if (!string.IsNullOrEmpty(defaultPhonemizer)) {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        defaultPhonemizer, @"\b(ZH|EN|JA|KO)\b");
                    if (match.Success) {
                        singerLanguage = match.Groups[1].Value;
                    }
                }
            }

            var request = new DifficalRenderRequest {
                // 整句位置
                position = phrase.position,
                duration = phrase.duration,
                end = phrase.end,
                leading = phrase.leading,
                positionMs = phrase.positionMs,
                durationMs = phrase.durationMs,
                endMs = phrase.endMs,
                leadingMs = phrase.leadingMs,

                // 声库信息
                singerPath = phrase.singer?.Location ?? "",
                singerType = phrase.singer?.SingerType.ToString() ?? "utau",
                singerLanguage = singerLanguage,
                language = targetLanguage,

                // 缓存
                hash = (long)phrase.hash,

                // 渲染选项
                isPreRender = isPreRender,
                trackNo = trackNo,
            };

            // 音符
            request.notes = phrase.notes.Select(n => new DifficalRenderNote {
                lyric = n.lyric,
                tone = n.tone,
                position = n.position,
                duration = n.duration,
                end = n.end,
                positionMs = n.positionMs,
                durationMs = n.durationMs,
                endMs = n.endMs,
            }).ToList();

            // 音素
            request.phones = phrase.phones.Select(p => new DifficalRenderPhone {
                position = p.position,
                duration = p.duration,
                end = p.end,
                leading = p.leading,
                positionMs = p.positionMs,
                durationMs = p.durationMs,
                endMs = p.endMs,
                leadingMs = p.leadingMs,
                phoneme = p.phoneme,
                tone = p.tone,
                noteIndex = p.noteIndex,
                tempo = p.tempo,
                preutterMs = p.preutterMs,
                overlapMs = p.overlapMs,
                suffix = p.suffix,
                otoFile = p.oto?.File ?? "",
                otoOffset = p.oto?.Offset ?? 0,
                otoConsonant = p.oto?.Consonant ?? 0,
                otoCutoff = p.oto?.Cutoff ?? 0,
                otoPreutter = p.oto?.Preutter ?? 0,
                otoOverlap = p.oto?.Overlap ?? 0,
                volume = p.volume,
                velocity = p.velocity,
                modulation = p.modulation,
                direct = p.direct,
                flags = p.flags?.Select(f => new object[] { f.Item1, (object?)f.Item2, f.Item3 }).ToList()
                        ?? new List<object[]>(),
                envelope = p.envelope?.Select(pt => new DifficalEnvelopePoint {
                    x = pt.X,
                    y = pt.Y / 100f,
                }).ToList() ?? new List<DifficalEnvelopePoint>(),
            }).ToList();

            // 标准 7 条曲线
            request.pitches = phrase.pitches?.ToList() ?? new List<float>();
            request.dynamics = phrase.dynamics?.ToList() ?? new List<float>();
            request.gender = phrase.gender?.ToList() ?? new List<float>();
            request.breathiness = phrase.breathiness?.ToList() ?? new List<float>();
            request.toneShift = phrase.toneShift?.ToList() ?? new List<float>();
            request.tension = phrase.tension?.ToList() ?? new List<float>();
            request.voicing = phrase.voicing?.ToList() ?? new List<float>();

            // 6 条扩展曲线（从 custom curves 提取）
            request.growl = curveMap.TryGetValue("GWL", out var gwl) ? gwl.ToList() : new List<float>();
            request.lowcut = curveMap.TryGetValue("LOWC", out var lowc) ? lowc.ToList() : new List<float>();
            request.brel = curveMap.TryGetValue("BREL", out var brel) ? brel.ToList() : new List<float>();
            request.breh = curveMap.TryGetValue("BREH", out var breh) ? breh.ToList() : new List<float>();
            request.warm = curveMap.TryGetValue("WARM", out var warm) ? warm.ToList() : new List<float>();
            request.hcmp = curveMap.TryGetValue("HCMP", out var hcmp) ? hcmp.ToList() : new List<float>();

            // 透传剩余自定义曲线（排除已提取的）
            var extractedAbbrs = new HashSet<string> { "GWL", "LOWC", "BREL", "BREH", "WARM", "HCMP" };
            request.curves = phrase.curves?
                .Where(c => !extractedAbbrs.Contains(c.Item1))
                .Select(c => new DifficalCurve {
                    abbr = c.Item1,
                    values = c.Item2?.ToList() ?? new List<float>(),
                }).ToList() ?? new List<DifficalCurve>();

            return request;
        }
    }
}
