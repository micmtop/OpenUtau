"""Copy Diffical into an OpenUtau source tree and patch the two integration points."""
import pathlib, shutil, sys

root = pathlib.Path(sys.argv[1]).resolve()
shutil.copytree(pathlib.Path(__file__).parent / "Diffical", root / "OpenUtau.Core" / "Diffical", dirs_exist_ok=True)

p = root / "OpenUtau.Core/Render/Renderers.cs"
s = p.read_text()
s = s.replace('public const string VOICEVOX = "VOICEVOX";',
              'public const string VOICEVOX = "VOICEVOX";\n        public const string DIFFICAL = "DIFFICAL";')
s = s.replace('static readonly string[] classicRenderers = new[] { WORLDLINE_R, WORLDLINE_R11, CLASSIC };',
              'static readonly string[] classicRenderers = new[] { WORLDLINE_R, WORLDLINE_R11, CLASSIC, DIFFICAL };')
s = s.replace('return new List<string> { WORLDLINE_R, WORLDLINE_R11, CLASSIC };',
              'return new List<string> { WORLDLINE_R, WORLDLINE_R11, CLASSIC, DIFFICAL };')
s = s.replace('''            } else if (renderer == VOICEVOX) {
                return new Voicevox.VoicevoxRenderer();''','''            } else if (renderer == VOICEVOX) {
                return new Voicevox.VoicevoxRenderer();
            } else if (renderer == DIFFICAL) {
                return new Diffical.DifficalRenderer();''')
p.write_text(s)

p = root / "OpenUtau.Core/Render/RenderEngine.cs"
s = p.read_text()
s = s.replace('''                    var task = phrase.renderer.Render(phrase, progress, request.trackNo, cancellation, true, renderEvents);
                    task.Wait();''','''                    var task = phrase.renderer.Render(phrase, progress, request.trackNo, cancellation, true, renderEvents);
                    try {
                        task.Wait();
                    } catch (Exception ex) when (cancellation.IsCancellationRequested &&
                            (ex is OperationCanceledException ||
                             ex is AggregateException agg && agg.InnerExceptions.All(e => e is OperationCanceledException))) {
                        break;
                    }''')
p.write_text(s)

# 渲染被取消（用户在渲染途中编辑/播放）是正常流程，不应弹“渲染失败”。
p = root / "OpenUtau.Core/Render/RenderEngine.cs"
s = p.read_text()
s = s.replace("""            if (wait) {
                task.Wait();
            }""", """            if (wait) {
                try {
                    task.Wait();
                } catch (Exception ex) when (ex is OperationCanceledException ||
                        ex is AggregateException agg && agg.InnerExceptions.All(e => e is OperationCanceledException)) {
                    Serilog.Log.Information("render mixdown cancelled");
                }
            }""")
p.write_text(s)
print("patched", root)
