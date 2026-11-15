using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenUtau.Core.Diffical {

    /// <summary>
    /// 整句渲染请求（C# → Python）
    /// 对应 Python 后端的 RenderPhraseRequest
    /// </summary>
    public class DifficalRenderRequest {

        // 整句位置（tick）
        public int position;
        public int duration;
        public int end;
        public int leading;

        // 整句位置（毫秒）
        public double positionMs;
        public double durationMs;
        public double endMs;
        public double leadingMs;

        // 音符和音素
        public List<DifficalRenderNote> notes;
        public List<DifficalRenderPhone> phones;

        // 整句曲线（5 tick 间隔采样）
        [JsonPropertyName("pitches")]
        public List<float> pitches;          // cent
        [JsonPropertyName("dynamics")]
        public List<float> dynamics;         // 0-200
        [JsonPropertyName("gender")]
        public List<float> gender;           // 0-100, 50=默认
        [JsonPropertyName("breathiness")]
        public List<float> breathiness;      // 0-100, 50=默认
        [JsonPropertyName("toneShift")]
        public List<float> toneShift;        // cent
        [JsonPropertyName("tension")]
        public List<float> tension;          // 0-100, 50=默认
        [JsonPropertyName("voicing")]
        public List<float> voicing;          // 0-100, 100=默认

        // Diffical 扩展曲线（5 tick 间隔采样）
        [JsonPropertyName("growl")]
        public List<float> growl;            // 0-100, 0=无
        [JsonPropertyName("lowcut")]
        public List<float> lowcut;           // 0-100, 0=无
        [JsonPropertyName("brel")]
        public List<float> brel;             // 0-100, 0=无（低频气声）
        [JsonPropertyName("breh")]
        public List<float> breh;             // 0-100, 0=无（高频气声）
        [JsonPropertyName("warm")]
        public List<float> warm;             // 0-100, 0=无（温暖度）
        [JsonPropertyName("hcmp")]
        public List<float> hcmp;             // 0-100, 0=无（谐波压缩）

        // 自定义曲线
        public List<DifficalCurve> curves;

        // 声库信息
        [JsonPropertyName("singer_path")]
        public string singerPath;
        [JsonPropertyName("singer_type")]
        public string singerType;
        [JsonPropertyName("singer_language")]
        public string singerLanguage;        // 声库语言（ZH/EN/JA/KO）
        [JsonPropertyName("language")]
        public string language;              // 目标语言（用于跨语种映射）

        // 缓存
        public long hash;

        // 渲染选项
        [JsonPropertyName("is_pre_render")]
        public bool isPreRender;
        [JsonPropertyName("track_no")]
        public int trackNo;
    }

    /// <summary>
    /// 音符数据
    /// </summary>
    public class DifficalRenderNote {
        public string lyric;
        public int tone;
        public int position;
        public int duration;
        public int end;
        public double positionMs;
        public double durationMs;
        public double endMs;
    }

    /// <summary>
    /// 音素数据
    /// </summary>
    public class DifficalRenderPhone {
        // tick 位置
        public int position;
        public int duration;
        public int end;
        public int leading;

        // 毫秒位置
        public double positionMs;
        public double durationMs;
        public double endMs;
        public double leadingMs;

        public string phoneme;
        public int tone;
        public int noteIndex;
        public double tempo;

        // Classic 专属
        [JsonPropertyName("preutter_ms")]
        public double preutterMs;
        [JsonPropertyName("overlap_ms")]
        public double overlapMs;
        public string suffix;

        // 音素文件信息
        [JsonPropertyName("oto_file")]
        public string otoFile;
        [JsonPropertyName("oto_offset")]
        public double otoOffset;
        [JsonPropertyName("oto_consonant")]
        public double otoConsonant;
        [JsonPropertyName("oto_cutoff")]
        public double otoCutoff;
        [JsonPropertyName("oto_preutter")]
        public double otoPreutter;
        [JsonPropertyName("oto_overlap")]
        public double otoOverlap;

        // 表达式参数
        public float volume;
        public float velocity;
        public float modulation;
        public bool direct;

        // UTAU flags（flag, value, abbr）— 与 OpenUtau RenderPhone.flags 一致
        // 例: ["Hb", 100, "brec"], ["g", 50, "gen"]
        public List<object[]> flags;

        // UTAU 包络（p0-p4，x=ms 相对音素起点，y=0-1 增益）
        public List<DifficalEnvelopePoint> envelope;
    }

    public class DifficalEnvelopePoint {
        public float x;
        public float y;
    }

    /// <summary>
    /// 自定义曲线
    /// </summary>
    public class DifficalCurve {
        public string abbr;
        public List<float> values;
    }

    /// <summary>
    /// 渲染结果（Python → C#）
    /// 对应 Python 后端的 RenderResultResponse
    /// </summary>
    public class DifficalRenderResponse {
        public float[] samples;

        [JsonPropertyName("leading_ms")]
        public double leadingMs;

        [JsonPropertyName("position_ms")]
        public double positionMs;

        [JsonPropertyName("estimated_length_ms")]
        public double estimatedLengthMs;

        [JsonPropertyName("pitch_ticks")]
        public float[] pitchTicks;

        [JsonPropertyName("pitch_tones")]
        public float[] pitchTones;

        [JsonPropertyName("real_curves")]
        public List<DifficalRealCurve> realCurves;
    }

    /// <summary>
    /// 渲染后的实际曲线
    /// </summary>
    public class DifficalRealCurve {
        public string abbr;
        public float[] ticks;
        public float[] values;
    }
}
