using System;
using System.IO;
using System.Text.Json;

namespace OpenUtau.Core.Diffical {
    /// <summary>Bundle config: Diffical/config.json, all fields optional.</summary>
    public class DifficalConfig {
        public ServerSection server = new();
        public EngineSection engine = new();
        public ModelsSection models = new();

        public class ServerSection {
            public string host = "127.0.0.1";
            public int port = 8573;
        }
        public class EngineSection {
            public bool auto_start = true;
            /// <summary>window (default, fast) or whole (reference parity, slower).</summary>
            public string hnsep_mode = "window";
            /// <summary>cpu (default, stable) or auto (CoreML/ANE, faster, may crash).</summary>
            public string execution_provider = "cpu";
            public bool dump_stages = false;
            /// <summary>Active-RMS target of the phrase normalization (default 0.13).</summary>
            public double rms_target = 0.13;
        }
        public class ModelsSection {
            public string assembler = "models/assembler.onnx";
            public string vocoder = "models/vocoder.onnx";
            public string hnsep = "models/hnsep.onnx";
        }

        public static string Root { get; private set; }

        public static DifficalConfig Load() {
            var cfg = new DifficalConfig();
            Root = FindRoot();
            if (Root == null) return cfg;
            string path = Path.Combine(Root, "config.json");
            if (!File.Exists(path)) return cfg;
            try {
                var loaded = JsonSerializer.Deserialize<DifficalConfig>(File.ReadAllText(path), new JsonSerializerOptions {
                    IncludeFields = true,
                    PropertyNameCaseInsensitive = true,
                });
                if (loaded != null) {
                    loaded.server ??= new ServerSection();
                    loaded.engine ??= new EngineSection();
                    loaded.models ??= new ModelsSection();
                    return loaded;
                }
            } catch (Exception e) {
                Serilog.Log.Warning(e, "Diffical config.json 读取失败，使用默认值");
            }
            return cfg;
        }

        /// <summary>
        /// Resolve a model path: absolute, then relative to the app bundle, then
        /// inside the installed `diffical-models` .oudep package (Dependencies).
        /// </summary>
        public static string ResolveModel(string root, string path) {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(root)) {
                candidates.Add(Path.GetFullPath(Path.Combine(root, path)));
            }
            try {
                string dep = PathManager.Inst.DependencyPath;
                candidates.Add(Path.GetFullPath(Path.Combine(dep, "diffical-models", path)));
            } catch { }
            foreach (var c in candidates) {
                if (File.Exists(c)) return c;
            }
            return candidates.Count > 0 ? candidates[0] : path;
        }

        public static string Resolve(string root, string path) {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(root ?? ".", path));
        }

        static string FindRoot() {
            string baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[] {
                Path.Combine(baseDir, "Diffical"),
                Path.Combine(baseDir, "..", "Resources", "Diffical"),
                Path.Combine(baseDir, "..", "..", "Resources", "Diffical"),
            }) {
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
    }
}
