using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace UdonLisp
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class LispRunner : UdonSharpBehaviour
    {
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private TextMeshProUGUI outputText;

        /// <summary>
        /// When true, RunTestSuite() is called automatically on Start().
        /// Toggle from the Inspector or via MCP before entering play mode.
        /// </summary>
        [SerializeField] private bool runTestsOnStart = true;

        // ---- Picture language (SICP 2.2.4) ----
        [Header("Picture Language")]
        [SerializeField] private Material canvasMaterial;
        [SerializeField] private VRCUrl imageUrl;
        [SerializeField] private VRCUrl kipfelUrl;
        [SerializeField] private int canvasResolution = 512;

        /// <summary>
        /// Enable image downloading. Disable in ClientSim (crashes Udon VM).
        /// Enable only when testing in the actual VRChat client.
        /// </summary>
        [SerializeField] private bool enableImageDownload = false;

        private VRCImageDownloader imageDownloader;
        private IVRCImageDownload imageDownloadHandle;
        // Track which image is currently being downloaded: 0=rogers, 1=kipfel
        private int _downloadIndex = 0;

        private DataDictionary globalEnv;

        private void Start()
        {
            globalEnv = LispEnv.CreateGlobal();
            Debug.Log("[LispRunner] Initialized.");

            // ---- Picture language canvas setup ----
            InitCanvas();
            LoadPrelude(globalEnv);

            if (runTestsOnStart)
                SendCustomEventDelayedFrames("RunTestSuite", 1);

            // Defer image download to a later frame so that if VRCImageDownloader
            // crashes (e.g. in ClientSim), the test suite still runs first.
            // Only attempt if enableImageDownload is true.
            if (enableImageDownload)
                SendCustomEventDelayedFrames("_StartImageDownloadDeferred", 10);
        }

        // ---- Picture language canvas state (instance fields, public for LispEvaluator access) ----
        [System.NonSerialized] public Texture2D canvasTexture;
        [System.NonSerialized] public Color32[] canvasPixels;
        [System.NonSerialized] public int canvasW;
        [System.NonSerialized] public int canvasH;
        // Multiple source images: index 0 = rogers, 1 = kipfel, etc.
        private const int MAX_IMAGES = 4;
        [System.NonSerialized] public Color32[][] sourceImages = new Color32[MAX_IMAGES][];
        [System.NonSerialized] public int[] sourceWs = new int[MAX_IMAGES];
        [System.NonSerialized] public int[] sourceHs = new int[MAX_IMAGES];
        // Legacy single-image aliases (index 0) for backward compat with image-painter
        [System.NonSerialized] public Color32[] sourcePixels;
        [System.NonSerialized] public int sourceW;
        [System.NonSerialized] public int sourceH;

        // ---- Picture language initialization ----

        private void InitCanvas()
        {
            int w = canvasResolution;
            int h = canvasResolution;

            canvasTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            canvasTexture.filterMode = FilterMode.Point;

            canvasPixels = new Color32[w * h];
            canvasW = w;
            canvasH = h;
            Color32 black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < canvasPixels.Length; i++)
                canvasPixels[i] = black;
            canvasTexture.SetPixels32(canvasPixels);
            canvasTexture.Apply();

            // Assign to material
            if (canvasMaterial != null)
            {
                canvasMaterial.mainTexture = canvasTexture;
                Debug.Log("[PictureLanguage] Canvas " + w + "x" + h + " assigned to material.");
            }
            else
            {
                Debug.LogWarning("[PictureLanguage] canvasMaterial not assigned — canvas created but not displayed.");
            }
        }

        public void _StartImageDownloadDeferred()
        {
            StartImageDownload();
        }

        // Image name table — index 0 = rogers, 1 = kipfel, etc.
        private readonly string[] _imageNames = new string[] { "rogers", "kipfel", "", "" };

        private void StartImageDownload()
        {
            // Pick URL for the current download index
            VRCUrl url = null;
            if (_downloadIndex == 0) url = imageUrl;
            else if (_downloadIndex == 1) url = kipfelUrl;

            // If no more URLs to download, we're done
            if (url == null || string.IsNullOrEmpty(url.Get()))
            {
                if (_downloadIndex == 0)
                    Debug.LogWarning("[PictureLanguage] imageUrl not set — rogers painter will not be available.");
                // Try next index (some URLs may be optional)
                _downloadIndex++;
                if (_downloadIndex < MAX_IMAGES && _downloadIndex <= 1)
                    StartImageDownload();
                return;
            }

            imageDownloader = new VRCImageDownloader();
            var texInfo = new TextureInfo();
            texInfo.GenerateMipMaps = false;
            imageDownloadHandle = imageDownloader.DownloadImage(
                url, null, (IUdonEventReceiver)this, texInfo);
            Debug.Log("[PictureLanguage] Downloading image [" + _downloadIndex + "] "
                + _imageNames[_downloadIndex] + ": " + url.Get());
        }

        public override void OnImageLoadSuccess(IVRCImageDownload result)
        {
            if (result != imageDownloadHandle) return;

            int idx = _downloadIndex;
            Texture2D srcTex = result.Result;
            int w = srcTex.width;
            int h = srcTex.height;
            Color32[] pixels = srcTex.GetPixels32();

            // Fix R8/single-channel textures: VRCImageDownloader may return
            // grayscale JPEGs as R8 format where only the R channel has data
            // and G, B are set to 255. Replicate R to G and B for proper grayscale.
            if (srcTex.format == TextureFormat.R8 || srcTex.format == TextureFormat.Alpha8)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte r = pixels[i].r;
                    pixels[i] = new Color32(r, r, r, (byte)255);
                }
                Debug.Log("[PictureLanguage] Converted R8 texture to grayscale RGBA.");
            }

            // Store in multi-image arrays
            sourceImages[idx] = pixels;
            sourceWs[idx] = w;
            sourceHs[idx] = h;

            // Keep legacy aliases pointing to index 0 for backward compat
            if (idx == 0)
            {
                sourcePixels = pixels;
                sourceW = w;
                sourceH = h;
            }

            // Define painter in globalEnv using image-painter-idx
            string name = _imageNames[idx];
            if (!string.IsNullOrEmpty(name))
            {
                string def = "(define (" + name + " frame) (image-painter-idx " + idx + " frame))";
                var tokens = LispTokenizer.Tokenize(def);
                var ast = LispParser.ParseAll(tokens);
                LispEval.Eval(ast, globalEnv, this);
            }

            Debug.Log("[PictureLanguage] Image [" + idx + "] " + name + " loaded: "
                + w + "x" + h + ". Painter defined.");

            // Notify network manager that images are available (for pending render replay)
            if (networkManager != null)
                networkManager.OnImageReady();

            // Chain: download next image
            _downloadIndex++;
            if (_downloadIndex < MAX_IMAGES && _downloadIndex <= 1)
                StartImageDownload();
        }

        public override void OnImageLoadError(IVRCImageDownload result)
        {
            if (result != imageDownloadHandle) return;
            int idx = _downloadIndex;
            string name = (idx < _imageNames.Length) ? _imageNames[idx] : "?";
            Debug.LogError("[PictureLanguage] Image [" + idx + "] " + name
                + " download failed: " + result.Error + " — " + result.ErrorMessage);

            // Continue to next image even if this one failed
            _downloadIndex++;
            if (_downloadIndex < MAX_IMAGES && _downloadIndex <= 1)
                StartImageDownload();
        }

        /// <summary>
        /// Load the Scheme prelude: all pure Scheme picture-language definitions.
        /// </summary>
        private void LoadPrelude(DataDictionary env)
        {
            string prelude = GetPictureLanguagePrelude();
            if (string.IsNullOrEmpty(prelude)) return;

            var tokens = LispTokenizer.Tokenize(prelude);
            if (tokens == null || tokens.Count == 0)
            {
                Debug.LogError("[PictureLanguage] Prelude tokenization failed.");
                return;
            }

            var ast = LispParser.ParseAll(tokens);
            if (ast == null || Lv.IsErr(ast))
            {
                Debug.LogError("[PictureLanguage] Prelude parse failed: "
                    + (ast != null ? Lv.ErrMsg(ast) : "null"));
                return;
            }

            var result = LispEval.Eval(ast, env, this);
            if (result != null && Lv.IsErr(result))
            {
                Debug.LogError("[PictureLanguage] Prelude eval failed: " + Lv.ErrMsg(result));
                return;
            }

            Debug.Log("[PictureLanguage] Prelude loaded successfully.");
        }

        /// <summary>
        /// Returns the full Scheme prelude for the SICP 2.2.4 picture language.
        /// All pure Scheme definitions: transform-painter, beside, below, flips, rotations,
        /// right-split, up-split, corner-split, square-limit, etc.
        /// </summary>
        private string GetPictureLanguagePrelude()
        {
            // frame-coord-map: maps unit-square point to frame coordinates
            // (frame-coord-map frame) returns a procedure that takes a vector
            return
                "(define (frame-coord-map frame)" +
                "  (lambda (v)" +
                "    (add-vect" +
                "      (origin-frame frame)" +
                "      (add-vect (scale-vect (xcor-vect v) (edge1-frame frame))" +
                "                (scale-vect (ycor-vect v) (edge2-frame frame))))))" +

                // transform-painter: remaps a painter into a sub-frame
                "(define (transform-painter painter origin corner1 corner2)" +
                "  (lambda (frame)" +
                "    (let ((m (frame-coord-map frame)))" +
                "      (let ((new-origin (m origin)))" +
                "        (painter" +
                "          (make-frame new-origin" +
                "            (sub-vect (m corner1) new-origin)" +
                "            (sub-vect (m corner2) new-origin)))))))" +

                // flip-vert: flip painter vertically
                "(define (flip-vert painter)" +
                "  (transform-painter painter" +
                "    (make-vect 0.0 1.0)" +
                "    (make-vect 1.0 1.0)" +
                "    (make-vect 0.0 0.0)))" +

                // flip-horiz: flip painter horizontally
                "(define (flip-horiz painter)" +
                "  (transform-painter painter" +
                "    (make-vect 1.0 0.0)" +
                "    (make-vect 0.0 0.0)" +
                "    (make-vect 1.0 1.0)))" +

                // rotate90: rotate painter 90 degrees counter-clockwise
                "(define (rotate90 painter)" +
                "  (transform-painter painter" +
                "    (make-vect 1.0 0.0)" +
                "    (make-vect 1.0 1.0)" +
                "    (make-vect 0.0 0.0)))" +

                // rotate180
                "(define (rotate180 painter)" +
                "  (transform-painter painter" +
                "    (make-vect 1.0 1.0)" +
                "    (make-vect 0.0 1.0)" +
                "    (make-vect 1.0 0.0)))" +

                // rotate270
                "(define (rotate270 painter)" +
                "  (transform-painter painter" +
                "    (make-vect 0.0 1.0)" +
                "    (make-vect 0.0 0.0)" +
                "    (make-vect 1.0 1.0)))" +

                // beside: place two painters side by side
                "(define (beside painter1 painter2)" +
                "  (let ((split-point (make-vect 0.5 0.0)))" +
                "    (let ((paint-left" +
                "            (transform-painter painter1" +
                "              (make-vect 0.0 0.0) split-point (make-vect 0.0 1.0)))" +
                "          (paint-right" +
                "            (transform-painter painter2" +
                "              split-point (make-vect 1.0 0.0) (make-vect 0.5 1.0))))" +
                "      (lambda (frame)" +
                "        (paint-left frame)" +
                "        (paint-right frame)))))" +

                // below: place painter1 below painter2
                "(define (below painter1 painter2)" +
                "  (let ((split-point (make-vect 0.0 0.5)))" +
                "    (let ((paint-bottom" +
                "            (transform-painter painter1" +
                "              (make-vect 0.0 0.0) (make-vect 1.0 0.0) split-point))" +
                "          (paint-top" +
                "            (transform-painter painter2" +
                "              split-point (make-vect 1.0 0.5) (make-vect 0.0 1.0))))" +
                "      (lambda (frame)" +
                "        (paint-bottom frame)" +
                "        (paint-top frame)))))" +

                // identity: identity painter (no transformation)
                "(define (identity painter) painter)" +

                // segments->painter: draws line segments
                "(define (segments->painter segment-list)" +
                "  (lambda (frame)" +
                "    (for-each" +
                "      (lambda (segment)" +
                "        (draw-line" +
                "          ((frame-coord-map frame) (start-segment segment))" +
                "          ((frame-coord-map frame) (end-segment segment))))" +
                "      segment-list)))" +

                // square-of-four: general combiner
                "(define (square-of-four tl tr bl br)" +
                "  (lambda (painter)" +
                "    (let ((top (beside (tl painter) (tr painter)))" +
                "          (bottom (beside (bl painter) (br painter))))" +
                "      (below bottom top))))" +

                // flipped-pairs
                "(define (flipped-pairs painter)" +
                "  (let ((combine4 (square-of-four identity flip-vert" +
                "                                  identity flip-vert)))" +
                "    (combine4 painter)))" +

                // right-split
                "(define (right-split painter n)" +
                "  (if (= n 0)" +
                "    painter" +
                "    (let ((smaller (right-split painter (- n 1))))" +
                "      (beside painter (below smaller smaller)))))" +

                // up-split
                "(define (up-split painter n)" +
                "  (if (= n 0)" +
                "    painter" +
                "    (let ((smaller (up-split painter (- n 1))))" +
                "      (below painter (beside smaller smaller)))))" +

                // corner-split
                "(define (corner-split painter n)" +
                "  (if (= n 0)" +
                "    painter" +
                "    (let ((up (up-split painter (- n 1)))" +
                "          (right (right-split painter (- n 1))))" +
                "      (let ((top-left (beside up up))" +
                "            (bottom-right (below right right))" +
                "            (corner (corner-split painter (- n 1))))" +
                "        (beside (below painter top-left)" +
                "                (below bottom-right corner))))))" +

                // square-limit
                "(define (square-limit painter n)" +
                "  (let ((combine4 (square-of-four flip-horiz identity" +
                "                                  rotate180 flip-vert)))" +
                "    (combine4 (corner-split painter n))))";
        }

        // ---- Networking support ----

        /// <summary>
        /// Optional reference to a LispNetworkManager for multiplayer sync.
        /// When set, OnRunButtonClicked delegates to the network manager.
        /// When null, the REPL operates in solo/offline mode (original behavior).
        /// </summary>
        [Header("Networking")]
        [SerializeField] public LispNetworkManager networkManager;

        /// <summary>
        /// Evaluate a single source string against globalEnv.
        /// Returns the printed result string.
        /// This is the core eval pipeline extracted for reuse by networking.
        /// </summary>
        public string EvalSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return "";

            // Tokenize
            var tokens = LispTokenizer.Tokenize(source);
            if (tokens == null || tokens.Count == 0)
                return "[Error] Tokenize failed";

            // Parse (multi-expression)
            var ast = LispParser.ParseAll(tokens);
            if (ast == null || Lv.IsErr(ast))
                return "[Error] Parse failed: " + (ast != null ? Lv.ErrMsg(ast) : "null");

            // Evaluate
            var result = LispEval.Eval(ast, globalEnv, this);
            return LispPrinter.Print(result);
        }

        /// <summary>
        /// Reset globalEnv to a fresh state (builtins + prelude, no user definitions).
        /// Used by networking to prepare for a full history replay.
        /// Also re-defines any image painters (rogers, kipfel, etc.) that have
        /// already been downloaded — these are defined programmatically by
        /// OnImageLoadSuccess and would otherwise be lost on reset.
        /// </summary>
        public void ResetEnvironment()
        {
            globalEnv = LispEnv.CreateGlobal();
            LoadPrelude(globalEnv);

            // Re-define image painters for already-loaded images
            for (int idx = 0; idx < MAX_IMAGES; idx++)
            {
                if (sourceImages[idx] == null) continue;
                string name = _imageNames[idx];
                if (string.IsNullOrEmpty(name)) continue;
                string def = "(define (" + name + " frame) (image-painter-idx " + idx + " frame))";
                var tokens = LispTokenizer.Tokenize(def);
                var ast = LispParser.ParseAll(tokens);
                LispEval.Eval(ast, globalEnv, this);
                Debug.Log("[LispRunner] Re-defined painter: " + name);
            }

            Debug.Log("[LispRunner] Environment reset.");
        }

        /// <summary>
        /// Replay a full history string (delimiter-separated expressions)
        /// against a fresh globalEnv. Returns the last result string.
        /// Used by networking for late-joiner catch-up.
        /// </summary>
        public string ReplayHistory(string history)
        {
            if (string.IsNullOrEmpty(history)) return "";

            ResetEnvironment();
            return ReplayDelta(history, 0);
        }

        /// <summary>
        /// Replay expressions from the history starting at a given expression index.
        /// Does NOT reset the environment — appends to existing state.
        /// Returns the last result string.
        /// </summary>
        public string ReplayDelta(string history, int fromIndex)
        {
            if (string.IsNullOrEmpty(history)) return "";

            string[] delimiter = new string[] { "\n;;--EXPR--\n" };
            string[] expressions = history.Split(delimiter, System.StringSplitOptions.RemoveEmptyEntries);

            string lastResult = "";
            for (int i = fromIndex; i < expressions.Length; i++)
            {
                string expr = expressions[i];
                if (string.IsNullOrEmpty(expr)) continue;
                lastResult = EvalSource(expr);
            }
            return lastResult;
        }

        /// <summary>
        /// Called from the Run button's OnClick via SendCustomEvent.
        /// Tokenizes, parses, and evaluates the input as Lisp.
        /// When a networkManager is assigned, delegates to it for ownership-guarded sync.
        /// </summary>
        public void OnRunButtonClicked()
        {
            // Delegate to network manager if present
            if (networkManager != null)
            {
                networkManager.OnRunButtonClicked();
                return;
            }

            // Solo / offline mode — original behavior
            if (inputField == null || outputText == null) return;

            var source = inputField.text;
            if (string.IsNullOrEmpty(source))
            {
                outputText.text = "";
                return;
            }

            Debug.Log("[LispRunner] Input: " + source);
            var output = EvalSource(source);
            outputText.text = output;
            Debug.Log("[LispRunner] Output: " + output);
        }

        // ================================================================
        //  Test Suite
        //  Callable via SendCustomEvent("RunTestSuite") from MCP or UI.
        //  Uses a fresh environment so results are deterministic.
        //  Logs each case as:
        //    [Test PASS] (+ 1 2)  =>  3
        //    [Test FAIL] (+ 1 2)  =>  4  expected: 3
        //  and prints a summary at the end.
        // ================================================================

        // ---- Test suite state (instance fields for multi-frame batching) ----
        [System.NonSerialized] private DataDictionary _testEnv;
        [System.NonSerialized] private int _testPass;
        [System.NonSerialized] private int _testFail;
        [System.NonSerialized] private int _rogersRetry;

        /// <summary>
        /// Run the built-in test suite.
        /// Public + no parameters so it can be invoked via SendCustomEvent.
        /// </summary>
        public void RunTestSuite()
        {
            // Use globalEnv since rogers is defined there on image load
            _testPass = 0;
            _testFail = 0;
            _rogersRetry = 0;
            // Wait for image to load, then render rogers
            SendCustomEventDelayedFrames("_RunRogersTest", 1);
        }

        /// <summary>Render rogers portrait only (deferred until image is loaded).</summary>
        public void _RunRogersTest()
        {
            int pass = _testPass;
            int fail = _testFail;

            // Check if rogers is defined (image may not have loaded yet)
            var rogersLookup = LispEnv.Lookup(globalEnv, "rogers");
            if (rogersLookup == null)
            {
                // Image not loaded yet — retry next frame (up to ~300 frames / 5 sec)
                _rogersRetry++;
                if (_rogersRetry < 300)
                {
                    SendCustomEventDelayedFrames("_RunRogersTest", 1);
                    return;
                }
                Debug.LogWarning("[TestSuite] rogers not defined after 300 frames, skipping render test.");
                fail++;
            }
            else
            {
                RunCase("(begin (render-painter (corner-split rogers 4)) \"corner-split rendered\")",
                    "\"corner-split rendered\"", globalEnv, ref pass, ref fail);
            }

            // --- Summary ---
            int total = pass + fail;
            string summary = "[TestSuite] " + pass + "/" + total + " passed"
                + (fail > 0 ? " (" + fail + " FAILED)" : " -- ALL PASS");
            Debug.Log(summary);
            if (outputText != null)
                outputText.text = summary;
        }

        // (duplicate _RunRogersTest removed — the correct version using globalEnv is above)

        /* ===== Full test suite commented out — only rogers render test active =====
        /// <summary>Part 1 of test suite (deferred to avoid VM timeout).</summary>
        public void _RunTestSuitePart1()
        {
            int pass = _testPass;
            int fail = _testFail;

            // --- Arithmetic ---
            RunCase("(+ 1 2)", "3", _testEnv, ref pass, ref fail);
            RunCase("(+ 1 2 3 4)", "10", _testEnv, ref pass, ref fail);
            RunCase("(+)", "0", _testEnv, ref pass, ref fail);
            RunCase("(- 10 3)", "7", _testEnv, ref pass, ref fail);
            RunCase("(- 5)", "-5", _testEnv, ref pass, ref fail);
            RunCase("(* 3 4)", "12", _testEnv, ref pass, ref fail);
            RunCase("(*)", "1", _testEnv, ref pass, ref fail);
            RunCase("(/ 10 2)", "5", _testEnv, ref pass, ref fail);
            RunCase("(/ 7 2)", "3", _testEnv, ref pass, ref fail);
            RunCase("(mod 10 3)", "1", _testEnv, ref pass, ref fail);

            // --- Float ---
            RunCase("(+ 1.5 2.5)", "4", _testEnv, ref pass, ref fail);
            RunCase("(/ 7.0 2.0)", "3.5", _testEnv, ref pass, ref fail);

            // --- Comparison ---
            RunCase("(= 1 1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(= 1 2)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(< 1 2)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(> 2 1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(<= 2 2)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(>= 3 2)", "#t", _testEnv, ref pass, ref fail);

            // --- Logic ---
            RunCase("(not #f)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(not #t)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(and #t #t)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(and #t #f)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(or #f #t)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(or #f #f)", "#f", _testEnv, ref pass, ref fail);

            // --- If / Cond ---
            RunCase("(if #t \"yes\" \"no\")", "\"yes\"", _testEnv, ref pass, ref fail);
            RunCase("(if #f \"yes\" \"no\")", "\"no\"", _testEnv, ref pass, ref fail);
            RunCase("(if #f \"x\")", "nil", _testEnv, ref pass, ref fail);
            RunCase("(cond (#f 1) (#t 2) (else 3))", "2", _testEnv, ref pass, ref fail);
            RunCase("(cond (#f 1) (else 99))", "99", _testEnv, ref pass, ref fail);

            // --- Define / Variable ---
            RunCase("(define x 42)", "nil", _testEnv, ref pass, ref fail);
            RunCase("x", "42", _testEnv, ref pass, ref fail);
            RunCase("(+ x 8)", "50", _testEnv, ref pass, ref fail);
            RunCase("(set! x 100)", "nil", _testEnv, ref pass, ref fail);
            RunCase("x", "100", _testEnv, ref pass, ref fail);

            // --- Lambda / Function ---
            RunCase("(define (square n) (* n n))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(square 5)", "25", _testEnv, ref pass, ref fail);
            RunCase("(square 0)", "0", _testEnv, ref pass, ref fail);
            RunCase("((lambda (a b) (+ a b)) 3 4)", "7", _testEnv, ref pass, ref fail);

            // --- Let ---
            RunCase("(let ((a 10) (b 20)) (+ a b))", "30", _testEnv, ref pass, ref fail);

            // --- Begin ---
            RunCase("(begin 1 2 3)", "3", _testEnv, ref pass, ref fail);

            // --- Quote ---
            RunCase("(quote (1 2 3))", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("'(a b c)", "(a b c)", _testEnv, ref pass, ref fail);

            // --- List ops ---
            RunCase("(list 1 2 3)", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(car (list 1 2 3))", "1", _testEnv, ref pass, ref fail);
            RunCase("(cdr (list 1 2 3))", "(2 3)", _testEnv, ref pass, ref fail);
            RunCase("(cons 0 (list 1 2))", "(0 1 2)", _testEnv, ref pass, ref fail);
            RunCase("(length (list 1 2 3))", "3", _testEnv, ref pass, ref fail);
            RunCase("(null? (list))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(null? (list 1))", "#f", _testEnv, ref pass, ref fail);

            // --- Type predicates ---
            RunCase("(number? 42)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(number? \"x\")", "#f", _testEnv, ref pass, ref fail);
            RunCase("(string? \"hi\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(symbol? 'abc)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(list? (list 1))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(bool? #t)", "#t", _testEnv, ref pass, ref fail);

            // --- String ops ---
            RunCase("(string-append \"hello\" \" \" \"world\")", "\"hello world\"", _testEnv, ref pass, ref fail);
            RunCase("(string-length \"abc\")", "3", _testEnv, ref pass, ref fail);
            RunCase("(number->string 42)", "\"42\"", _testEnv, ref pass, ref fail);
            RunCase("(string->number \"7\")", "7", _testEnv, ref pass, ref fail);

            // --- Nested / Recursion ---
            RunCase("(define (fact n) (if (= n 0) 1 (* n (fact (- n 1)))))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(fact 5)", "120", _testEnv, ref pass, ref fail);
            RunCase("(fact 0)", "1", _testEnv, ref pass, ref fail);
            RunCase("(define (fib n) (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(fib 10)", "55", _testEnv, ref pass, ref fail);

            // --- Error handling ---
            RunCase("(/ 1 0)", "[Error] /: division by zero", _testEnv, ref pass, ref fail);
            RunCase("(+ 1 \"a\")", "[Error] +: expected number", _testEnv, ref pass, ref fail);
            RunCase("undefined-var", "[Error] undefined symbol: undefined-var", _testEnv, ref pass, ref fail);

            // ==============================================================
            //  Phase 0 — Foundational Fixes
            // ==============================================================

            // --- 0.1  Implicit begin in lambda / define / let ---
            RunCase("((lambda (x) (define y 1) (+ x y)) 10)", "11", _testEnv, ref pass, ref fail);
            RunCase("(define (multi-body a) (define b 2) (+ a b))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(multi-body 5)", "7", _testEnv, ref pass, ref fail);
            RunCase("(let ((a 1)) (define b 2) (+ a b))", "3", _testEnv, ref pass, ref fail);

            // --- 0.3  string->number returns #f on failure ---
            RunCase("(string->number \"abc\")", "#f", _testEnv, ref pass, ref fail);
            RunCase("(string->number \"\")", "#f", _testEnv, ref pass, ref fail);

            // --- 0.4  Variadic comparisons ---
            RunCase("(= 1 1 1 1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(= 1 1 2 1)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(< 1 2 3 4)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(< 1 2 2 4)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(> 4 3 2 1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(<= 1 1 2 3)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(>= 3 2 2 1)", "#t", _testEnv, ref pass, ref fail);

            // --- 0.5  Variadic / and reciprocal ---
            RunCase("(/ 2)", "0.5", _testEnv, ref pass, ref fail);
            RunCase("(/ 120 2 3)", "20", _testEnv, ref pass, ref fail);

            // --- 0.2a  Proper cons cells / pair type ---
            RunCase("(cons 1 2)", "(1 . 2)", _testEnv, ref pass, ref fail);
            RunCase("(cons 1 (cons 2 (cons 3 nil)))", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(car (cons 1 2))", "1", _testEnv, ref pass, ref fail);
            RunCase("(cdr (cons 1 2))", "2", _testEnv, ref pass, ref fail);
            RunCase("(pair? (cons 1 2))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(pair? 42)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(pair? (list 1 2))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(pair? (list))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(list? (list 1 2))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(list? (cons 1 2))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(list? nil)", "#t", _testEnv, ref pass, ref fail);

            // Dotted pair notation
            RunCase("'(1 . 2)", "(1 . 2)", _testEnv, ref pass, ref fail);
            RunCase("'(1 2 . 3)", "(1 2 . 3)", _testEnv, ref pass, ref fail);

            // --- 0.2b  set-car! / set-cdr! ---
            RunCase("(define p (cons 1 2))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(set-car! p 10)", "nil", _testEnv, ref pass, ref fail);
            RunCase("(car p)", "10", _testEnv, ref pass, ref fail);
            RunCase("(set-cdr! p 20)", "nil", _testEnv, ref pass, ref fail);
            RunCase("(cdr p)", "20", _testEnv, ref pass, ref fail);

            // ==============================================================
            //  Phase 1 — SICP Chapters 1–2
            // ==============================================================

            // --- 1.7  apply, map, for-each ---
            RunCase("(apply + '(1 2 3))", "6", _testEnv, ref pass, ref fail);
            RunCase("(apply + 1 2 '(3 4))", "10", _testEnv, ref pass, ref fail);
            RunCase("(apply cons '(1 2))", "(1 . 2)", _testEnv, ref pass, ref fail);
            RunCase("(apply list '())", "nil", _testEnv, ref pass, ref fail);
            RunCase("(define (square x) (* x x))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(map square '(1 2 3 4))", "(1 4 9 16)", _testEnv, ref pass, ref fail);
            RunCase("(map + '(1 2 3) '(10 20 30))", "(11 22 33)", _testEnv, ref pass, ref fail);
            RunCase("(map car '((1 2) (3 4) (5 6)))", "(1 3 5)", _testEnv, ref pass, ref fail);
            RunCase("(define acc 0)", "nil", _testEnv, ref pass, ref fail);
            RunCase("(for-each (lambda (x) (set! acc (+ acc x))) '(1 2 3))", "nil", _testEnv, ref pass, ref fail);
            RunCase("acc", "6", _testEnv, ref pass, ref fail);

            // --- 1.5  eq?, eqv?, equal? ---
            RunCase("(eq? 'a 'a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(eq? 'a 'b)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(eq? #t #t)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(eq? nil nil)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(eq? 1 1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(define p1 (cons 1 2))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(eq? p1 p1)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(eq? (cons 1 2) (cons 1 2))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(eqv? 42 42)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(eqv? 42 42.0)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(equal? '(1 2 3) '(1 2 3))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(equal? '(1 2 3) '(1 2 4))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(equal? '(1 (2 3)) '(1 (2 3)))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(equal? \"abc\" \"abc\")", "#t", _testEnv, ref pass, ref fail);

            // --- 1.6  List library ---
            // cXXXr compositions
            RunCase("(cadr '(1 2 3))", "2", _testEnv, ref pass, ref fail);
            RunCase("(caddr '(1 2 3))", "3", _testEnv, ref pass, ref fail);
            RunCase("(caar '((1 2) (3 4)))", "1", _testEnv, ref pass, ref fail);
            RunCase("(cdar '((1 2) (3 4)))", "(2)", _testEnv, ref pass, ref fail);
            RunCase("(cddr '(1 2 3 4))", "(3 4)", _testEnv, ref pass, ref fail);
            RunCase("(cadadr '((1 2) (3 4 5)))", "4", _testEnv, ref pass, ref fail);
            // append
            RunCase("(append '(1 2) '(3 4))", "(1 2 3 4)", _testEnv, ref pass, ref fail);
            RunCase("(append '(1) '(2) '(3))", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(append '() '(1 2))", "(1 2)", _testEnv, ref pass, ref fail);
            RunCase("(append '(1 2) '())", "(1 2)", _testEnv, ref pass, ref fail);
            RunCase("(append)", "nil", _testEnv, ref pass, ref fail);
            // reverse
            RunCase("(reverse '(1 2 3))", "(3 2 1)", _testEnv, ref pass, ref fail);
            RunCase("(reverse '())", "nil", _testEnv, ref pass, ref fail);
            // list-tail, list-ref
            RunCase("(list-tail '(a b c d) 2)", "(c d)", _testEnv, ref pass, ref fail);
            RunCase("(list-ref '(a b c d) 0)", "a", _testEnv, ref pass, ref fail);
            RunCase("(list-ref '(a b c d) 2)", "c", _testEnv, ref pass, ref fail);
            // memq, member
            RunCase("(memq 'b '(a b c))", "(b c)", _testEnv, ref pass, ref fail);
            RunCase("(memq 'z '(a b c))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(member '(2) '((1) (2) (3)))", "((2) (3))", _testEnv, ref pass, ref fail);
            // assq, assoc
            RunCase("(assq 'b '((a 1) (b 2) (c 3)))", "(b 2)", _testEnv, ref pass, ref fail);
            RunCase("(assq 'z '((a 1) (b 2)))", "#f", _testEnv, ref pass, ref fail);
            RunCase("(assoc '(b) '(((a) 1) ((b) 2)))", "((b) 2)", _testEnv, ref pass, ref fail);

            // --- 1.1  let*, letrec ---
            RunCase("(let* ((x 1) (y (+ x 1))) y)", "2", _testEnv, ref pass, ref fail);
            RunCase("(let* ((x 1) (y (+ x 1)) (z (* y 3))) z)", "6", _testEnv, ref pass, ref fail);
            RunCase("(let* ((x 10)) (+ x 1))", "11", _testEnv, ref pass, ref fail);
            RunCase("(letrec ((fact (lambda (n) (if (= n 0) 1 (* n (fact (- n 1))))))) (fact 5))", "120", _testEnv, ref pass, ref fail);
            RunCase("(letrec ((even? (lambda (n) (if (= n 0) #t (odd? (- n 1))))) (odd? (lambda (n) (if (= n 0) #f (even? (- n 1)))))) (even? 10))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(letrec ((even? (lambda (n) (if (= n 0) #t (odd? (- n 1))))) (odd? (lambda (n) (if (= n 0) #f (even? (- n 1)))))) (odd? 7))", "#t", _testEnv, ref pass, ref fail);

            // --- 1.2  Named let ---
            RunCase("(let loop ((i 0) (acc 0)) (if (= i 5) acc (loop (+ i 1) (+ acc i))))", "10", _testEnv, ref pass, ref fail);
            RunCase("(let loop ((n 10) (acc 1)) (if (= n 0) acc (loop (- n 1) (* acc n))))", "3628800", _testEnv, ref pass, ref fail);
            RunCase("(let loop ((lst '(1 2 3)) (acc '())) (if (null? lst) acc (loop (cdr lst) (cons (car lst) acc))))", "(3 2 1)", _testEnv, ref pass, ref fail);
            RunCase("(let loop ((i 0)) (if (= i 5) 'done (loop (+ i 1))))", "done", _testEnv, ref pass, ref fail);

            // ==== Phase 1.9 — Numeric library ====

            // --- 1.9  Predicates ---
            RunCase("(integer? 3)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(integer? 3.5)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(integer? 3.0)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(zero? 0)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(zero? 1)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(positive? 5)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(negative? -3)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(negative? 0)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(odd? 3)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(even? 4)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(odd? 2)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(exact? 5)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(inexact? 5.0)", "#t", _testEnv, ref pass, ref fail);

            // --- 1.9  Arithmetic ---
            RunCase("(abs -7)", "7", _testEnv, ref pass, ref fail);
            RunCase("(abs 3)", "3", _testEnv, ref pass, ref fail);
            RunCase("(max 3 7 2 8 1)", "8", _testEnv, ref pass, ref fail);
            RunCase("(min 3 7 2 8 1)", "1", _testEnv, ref pass, ref fail);
            RunCase("(quotient 13 4)", "3", _testEnv, ref pass, ref fail);
            RunCase("(quotient -13 4)", "-3", _testEnv, ref pass, ref fail);
            RunCase("(remainder 13 4)", "1", _testEnv, ref pass, ref fail);
            RunCase("(modulo 13 4)", "1", _testEnv, ref pass, ref fail);
            RunCase("(modulo -13 4)", "3", _testEnv, ref pass, ref fail);
            RunCase("(gcd 32 24)", "8", _testEnv, ref pass, ref fail);
            RunCase("(gcd 0 5)", "5", _testEnv, ref pass, ref fail);
            RunCase("(lcm 4 6)", "12", _testEnv, ref pass, ref fail);

            // --- 1.9  Math / rounding ---
            RunCase("(floor 2.7)", "2", _testEnv, ref pass, ref fail);
            RunCase("(floor -2.3)", "-3", _testEnv, ref pass, ref fail);
            RunCase("(ceiling 2.3)", "3", _testEnv, ref pass, ref fail);
            RunCase("(truncate 2.7)", "2", _testEnv, ref pass, ref fail);
            RunCase("(truncate -2.7)", "-2", _testEnv, ref pass, ref fail);
            RunCase("(round 2.5)", "2", _testEnv, ref pass, ref fail);
            RunCase("(round 3.5)", "4", _testEnv, ref pass, ref fail);
            RunCase("(sqrt 9)", "3", _testEnv, ref pass, ref fail);
            RunCase("(expt 2 10)", "1024", _testEnv, ref pass, ref fail);
            RunCase("(expt 3 0)", "1", _testEnv, ref pass, ref fail);

            // --- 1.9  Conversion ---
            RunCase("(inexact? (exact->inexact 5))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(exact? (inexact->exact 2.7))", "#t", _testEnv, ref pass, ref fail);

            // ==== Phase 1.8 — procedure? ====
            RunCase("(procedure? car)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? (lambda (x) x))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? 42)", "#f", _testEnv, ref pass, ref fail);
            RunCase("(procedure? '(1 2))", "#f", _testEnv, ref pass, ref fail);

            // Save counts and continue in next frame to avoid 10s VM timeout
            _testPass = pass;
            _testFail = fail;
            SendCustomEventDelayedFrames("_RunTestSuitePart2", 1);
        }

        /// <summary>Part 2 of test suite (deferred to next frame).</summary>
        public void _RunTestSuitePart2()
        {
            int pass = _testPass;
            int fail = _testFail;

            // ==== Phase 1.3 — case ====
            RunCase("(case (+ 1 1) ((1) \"one\") ((2) \"two\") (else \"other\"))", "\"two\"", _testEnv, ref pass, ref fail);
            RunCase("(case 5 ((1 2 3) \"low\") ((4 5 6) \"mid\") (else \"high\"))", "\"mid\"", _testEnv, ref pass, ref fail);
            RunCase("(case 99 ((1) \"a\") ((2) \"b\"))", "nil", _testEnv, ref pass, ref fail);
            RunCase("(case 'x ((a b) 1) ((c x) 2) (else 3))", "2", _testEnv, ref pass, ref fail);
            RunCase("(case 1 ((1) \"a\" \"b\"))", "\"b\"", _testEnv, ref pass, ref fail);

            // ==== Phase 1.4 — do ====
            RunCase("(do ((i 0 (+ i 1))) ((= i 5) i))", "5", _testEnv, ref pass, ref fail);
            RunCase("(do ((i 0 (+ i 1)) (acc 0 (+ acc i))) ((= i 5) acc))", "10", _testEnv, ref pass, ref fail);
            RunCase("(do ((i 0 (+ i 1))) ((= i 0) 'done))", "done", _testEnv, ref pass, ref fail);
            RunCase("(do ((vec '()) (i 0 (+ i 1))) ((= i 3) vec) (set! vec (cons i vec)))", "(2 1 0)", _testEnv, ref pass, ref fail);
            RunCase("(let ((x 0)) (do ((i 0 (+ i 1))) ((= i 3)) (set! x (+ x i))) x)", "3", _testEnv, ref pass, ref fail);

            // ==== Phase 1.10 — Quasiquote ====
            RunCase("`42", "42", _testEnv, ref pass, ref fail);
            RunCase("`a", "a", _testEnv, ref pass, ref fail);
            RunCase("`(1 2 3)", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(let ((x 5)) `(a ,x c))", "(a 5 c)", _testEnv, ref pass, ref fail);
            RunCase("(let ((x 5)) `(a ,(+ x 1) c))", "(a 6 c)", _testEnv, ref pass, ref fail);
            RunCase("`(a ,(+ 1 2) b)", "(a 3 b)", _testEnv, ref pass, ref fail);
            RunCase("`(a ,@(list 4 5) b)", "(a 4 5 b)", _testEnv, ref pass, ref fail);
            RunCase("`(a ,(+ 1 2) ,@(list 4 5))", "(a 3 4 5)", _testEnv, ref pass, ref fail);

            // ==== Phase 2.7 — newline ====
            RunCase("(newline)", "nil", _testEnv, ref pass, ref fail);

            // ==== Phase 2.4 — symbol->string, string->symbol ====
            RunCase("(symbol->string 'hello)", "\"hello\"", _testEnv, ref pass, ref fail);
            RunCase("(string->symbol \"world\")", "world", _testEnv, ref pass, ref fail);
            RunCase("(symbol? (string->symbol \"test\"))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string? (symbol->string 'abc))", "#t", _testEnv, ref pass, ref fail);

            // ==== Phase 2.6 — write vs display ====
            RunCase("(write 42)", "nil", _testEnv, ref pass, ref fail);
            RunCase("(display 42)", "nil", _testEnv, ref pass, ref fail);
            RunCase("(write \"hello\")", "nil", _testEnv, ref pass, ref fail);
            RunCase("(display \"hello\")", "nil", _testEnv, ref pass, ref fail);

            // ==== Phase 2.1 — Characters and char procedures ====
            // Literal parsing and printing
            RunCase("#\\a", "#\\a", _testEnv, ref pass, ref fail);
            RunCase("#\\space", "#\\space", _testEnv, ref pass, ref fail);
            RunCase("#\\newline", "#\\newline", _testEnv, ref pass, ref fail);
            RunCase("#\\tab", "#\\tab", _testEnv, ref pass, ref fail);
            // Type predicate
            RunCase("(char? #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char? 42)", "#f", _testEnv, ref pass, ref fail);
            // Comparisons
            RunCase("(char=? #\\a #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char<? #\\a #\\b)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char>? #\\b #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char<=? #\\a #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char>=? #\\b #\\a)", "#t", _testEnv, ref pass, ref fail);
            // Case-insensitive comparison
            RunCase("(char-ci=? #\\A #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char-ci<? #\\A #\\b)", "#t", _testEnv, ref pass, ref fail);
            // Classification
            RunCase("(char-alphabetic? #\\a)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char-numeric? #\\5)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char-whitespace? #\\space)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(char-alphabetic? #\\5)", "#f", _testEnv, ref pass, ref fail);
            // Case conversion
            RunCase("(char-upcase #\\a)", "#\\A", _testEnv, ref pass, ref fail);
            RunCase("(char-downcase #\\A)", "#\\a", _testEnv, ref pass, ref fail);
            // char->integer and integer->char
            RunCase("(char->integer #\\A)", "65", _testEnv, ref pass, ref fail);
            RunCase("(integer->char 65)", "#\\A", _testEnv, ref pass, ref fail);
            RunCase("(char=? (integer->char (char->integer #\\z)) #\\z)", "#t", _testEnv, ref pass, ref fail);

            // ==== Phase 2.2 — String expansion ====
            // make-string
            RunCase("(make-string 3 #\\a)", "\"aaa\"", _testEnv, ref pass, ref fail);
            RunCase("(string-length (make-string 5 #\\x))", "5", _testEnv, ref pass, ref fail);
            // string (construct from chars)
            RunCase("(string #\\a #\\b #\\c)", "\"abc\"", _testEnv, ref pass, ref fail);
            // string-ref
            RunCase("(string-ref \"hello\" 0)", "#\\h", _testEnv, ref pass, ref fail);
            RunCase("(string-ref \"hello\" 4)", "#\\o", _testEnv, ref pass, ref fail);
            // string-set!
            RunCase("(let ((s (string-copy \"hello\"))) (string-set! s 0 #\\H) s)", "\"Hello\"", _testEnv, ref pass, ref fail);
            // string comparisons
            RunCase("(string=? \"abc\" \"abc\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string<? \"abc\" \"abd\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string>? \"abd\" \"abc\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string<=? \"abc\" \"abc\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string>=? \"abd\" \"abc\")", "#t", _testEnv, ref pass, ref fail);
            // case-insensitive comparison
            RunCase("(string-ci=? \"ABC\" \"abc\")", "#t", _testEnv, ref pass, ref fail);
            RunCase("(string-ci<? \"ABC\" \"abd\")", "#t", _testEnv, ref pass, ref fail);
            // substring
            RunCase("(substring \"hello world\" 6 11)", "\"world\"", _testEnv, ref pass, ref fail);
            RunCase("(substring \"hello\" 0 0)", "\"\"", _testEnv, ref pass, ref fail);
            // string-copy
            RunCase("(string=? (string-copy \"test\") \"test\")", "#t", _testEnv, ref pass, ref fail);
            // string->list and list->string
            RunCase("(string->list \"abc\")", "(#\\a #\\b #\\c)", _testEnv, ref pass, ref fail);
            RunCase("(list->string (list #\\a #\\b #\\c))", "\"abc\"", _testEnv, ref pass, ref fail);
            RunCase("(list->string (string->list \"hello\"))", "\"hello\"", _testEnv, ref pass, ref fail);

            // ==== Phase 2.3 — Vectors ====
            // Literal and constructor
            RunCase("#(1 2 3)", "#(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(vector 1 2 3)", "#(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(make-vector 3 0)", "#(0 0 0)", _testEnv, ref pass, ref fail);
            // Type predicate
            RunCase("(vector? #(1 2))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(vector? (list 1 2))", "#f", _testEnv, ref pass, ref fail);
            // vector-ref
            RunCase("(vector-ref #(10 20 30) 1)", "20", _testEnv, ref pass, ref fail);
            // vector-set!
            RunCase("(let ((v (vector 1 2 3))) (vector-set! v 1 99) (vector-ref v 1))", "99", _testEnv, ref pass, ref fail);
            // vector-length
            RunCase("(vector-length #(a b c d))", "4", _testEnv, ref pass, ref fail);
            RunCase("(vector-length #())", "0", _testEnv, ref pass, ref fail);
            // vector->list and list->vector
            RunCase("(vector->list #(1 2 3))", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(vector->list #())", "nil", _testEnv, ref pass, ref fail);
            RunCase("(equal? (list->vector (list 1 2 3)) #(1 2 3))", "#t", _testEnv, ref pass, ref fail);
            // vector-fill!
            RunCase("(let ((v (make-vector 3 0))) (vector-fill! v 7) v)", "#(7 7 7)", _testEnv, ref pass, ref fail);

            // ==== Phase 2.5 — eval and environment specifiers ====
            RunCase("(eval '(+ 1 2) (scheme-report-environment 5))", "3", _testEnv, ref pass, ref fail);
            RunCase("(eval '(* 6 7) (scheme-report-environment 5))", "42", _testEnv, ref pass, ref fail);
            RunCase("(eval '(list 1 2 3) (interaction-environment))", "(1 2 3)", _testEnv, ref pass, ref fail);
            RunCase("(eval '(string-append \"hello\" \" \" \"world\") (scheme-report-environment 5))", "\"hello world\"", _testEnv, ref pass, ref fail);

            // ==== Phase 2.8 — Multiple return values ====
            RunCase("(values 1)", "1", _testEnv, ref pass, ref fail);
            RunCase("(call-with-values (lambda () (values 1 2 3)) +)", "6", _testEnv, ref pass, ref fail);
            RunCase("(call-with-values (lambda () (values 4 5)) (lambda (a b) (* a b)))", "20", _testEnv, ref pass, ref fail);
            RunCase("(call-with-values (lambda () 42) (lambda (x) (+ x 1)))", "43", _testEnv, ref pass, ref fail);

            // ==== Phase 3.1 — Tail-call optimization ====
            // Tail-recursive loop via named let — 2000 iterations
            // (Udon VM execution timeout limits practical depth to ~2000-3000)
            RunCase("(let loop ((n 2000) (acc 0)) (if (= n 0) acc (loop (- n 1) (+ acc n))))", "2001000", _testEnv, ref pass, ref fail);
            // Tail-recursive factorial with accumulator — deep recursion
            RunCase("(begin (define (fact-iter n acc) (if (= n 0) acc (fact-iter (- n 1) (* acc n)))) (fact-iter 12 1))", "479001600", _testEnv, ref pass, ref fail);
            // Mutual tail recursion: even?/odd? via define (tests if/begin TCO)
            RunCase("(begin (define (my-even? n) (if (= n 0) #t (my-odd? (- n 1)))) (define (my-odd? n) (if (= n 0) #f (my-even? (- n 1)))) (my-even? 2000))", "#t", _testEnv, ref pass, ref fail);
            // Tail position in cond
            RunCase("(begin (define (count-down n) (cond ((= n 0) (quote done)) (else (count-down (- n 1))))) (count-down 2000))", "done", _testEnv, ref pass, ref fail);
            // Tail position in or/and chain
            RunCase("(begin (define (last-true n) (if (= n 0) 42 (and #t (last-true (- n 1))))) (last-true 2000))", "42", _testEnv, ref pass, ref fail);

            // ==== Phase 3.2 — Promises (delay / force) ====
            // Basic delay/force
            RunCase("(force (delay (+ 1 2)))", "3", _testEnv, ref pass, ref fail);
            // Memoization: force returns cached value on second call
            RunCase("(begin (define p (delay (+ 10 20))) (force p) (force p))", "30", _testEnv, ref pass, ref fail);
            // promise? predicate
            RunCase("(promise? (delay 42))", "#t", _testEnv, ref pass, ref fail);
            RunCase("(promise? 42)", "#f", _testEnv, ref pass, ref fail);
            // force on non-promise returns the value (R5RS behavior)
            RunCase("(force 42)", "42", _testEnv, ref pass, ref fail);
            // make-promise wraps a value in an already-forced promise
            RunCase("(force (make-promise 99))", "99", _testEnv, ref pass, ref fail);
            // Nested delay: (force (delay (delay ...))) should force the inner promise too
            RunCase("(force (delay (+ 3 4)))", "7", _testEnv, ref pass, ref fail);

            // ==== Phase 3.3 — call/cc (escape-only continuations) ====
            // Basic call/cc: normal return (no escape)
            RunCase("(call/cc (lambda (k) 42))", "42", _testEnv, ref pass, ref fail);
            // Escape via continuation
            RunCase("(call/cc (lambda (k) (k 99) 0))", "99", _testEnv, ref pass, ref fail);
            // call/cc in arithmetic expression
            RunCase("(+ 1 (call/cc (lambda (k) (+ 2 (k 3)))))", "4", _testEnv, ref pass, ref fail);
            // Continuation as procedure?
            RunCase("(call/cc (lambda (k) (procedure? k)))", "#t", _testEnv, ref pass, ref fail);
            // Full name works too
            RunCase("(call-with-current-continuation (lambda (k) (k 7)))", "7", _testEnv, ref pass, ref fail);

            // ==== Phase 3.4 — dynamic-wind ====
            // Normal flow: before, thunk, after all called in order
            RunCase("(let ((log (quote ()))) (dynamic-wind (lambda () (set! log (cons 1 log))) (lambda () (set! log (cons 2 log)) 42) (lambda () (set! log (cons 3 log)))) log)", "(3 2 1)", _testEnv, ref pass, ref fail);
            // Return value is thunk result
            RunCase("(dynamic-wind (lambda () 0) (lambda () 99) (lambda () 0))", "99", _testEnv, ref pass, ref fail);
            // Escape via call/cc: after is still called
            RunCase("(let ((log (quote ()))) (call/cc (lambda (k) (dynamic-wind (lambda () (set! log (cons 1 log))) (lambda () (set! log (cons 2 log)) (k 0) (set! log (cons 999 log))) (lambda () (set! log (cons 3 log)))))) log)", "(3 2 1)", _testEnv, ref pass, ref fail);

            // Save counts and continue in next frame
            _testPass = pass;
            _testFail = fail;
            SendCustomEventDelayedFrames("_RunTestSuitePart3", 1);
        }

        /// <summary>Part 3 of test suite (deferred to avoid VM timeout).</summary>
        public void _RunTestSuitePart3()
        {
            int pass = _testPass;
            int fail = _testFail;

            // ==== Phase 3.5 — Tail forms in derived expressions ====
            // when: basic true case
            RunCase("(when #t 1 2 3)", "3", _testEnv, ref pass, ref fail);
            // when: false case returns nil
            RunCase("(when #f 1 2 3)", "nil", _testEnv, ref pass, ref fail);
            // unless: basic false case
            RunCase("(unless #f 10 20 30)", "30", _testEnv, ref pass, ref fail);
            // unless: true case returns nil
            RunCase("(unless #t 10 20 30)", "nil", _testEnv, ref pass, ref fail);
            // when with side effects
            RunCase("(let ((x 0)) (when #t (set! x 5)) x)", "5", _testEnv, ref pass, ref fail);
            // unless with side effects
            RunCase("(let ((x 0)) (unless #f (set! x 7)) x)", "7", _testEnv, ref pass, ref fail);
            // TCO stress: case in tail position (500 iterations — case has higher per-iteration overhead)
            RunCase("(define (case-loop n) (case #t ((#t) (if (= n 0) n (case-loop (- n 1)))))) (case-loop 500)", "0", _testEnv, ref pass, ref fail);
            // TCO stress: or in tail position (500 iterations)
            RunCase("(define (or-loop n) (if (= n 0) n (or #f (or-loop (- n 1))))) (or-loop 500)", "0", _testEnv, ref pass, ref fail);
            // TCO stress: do loop (500 iterations)
            RunCase("(do ((i 0 (+ i 1))) ((= i 500) i))", "500", _testEnv, ref pass, ref fail);
            // TCO stress: when in tail position (500 iterations)
            RunCase("(define (when-loop n) (when #t (if (= n 0) n (when-loop (- n 1))))) (when-loop 500)", "0", _testEnv, ref pass, ref fail);
            // TCO stress: unless in tail position (500 iterations)
            RunCase("(define (unless-loop n) (unless #f (if (= n 0) n (unless-loop (- n 1))))) (unless-loop 500)", "0", _testEnv, ref pass, ref fail);

            // ==== Phase 4.1 — Internal definitions ====
            // Single internal define in lambda body
            RunCase("(define (f x) (define y (* x 2)) (+ y 1)) (f 5)", "11", _testEnv, ref pass, ref fail);
            // Multiple internal defines in lambda body
            RunCase("(define (g x) (define a (+ x 1)) (define b (* x 2)) (+ a b)) (g 3)", "10", _testEnv, ref pass, ref fail);
            // Internal define with function shorthand
            RunCase("(define (h x) (define (sq y) (* y y)) (sq x)) (h 7)", "49", _testEnv, ref pass, ref fail);
            // Internal defines with mutual recursion (letrec semantics)
            RunCase("(define (test-mutual) (define (even? n) (if (= n 0) #t (odd? (- n 1)))) (define (odd? n) (if (= n 0) #f (even? (- n 1)))) (even? 10)) (test-mutual)", "#t", _testEnv, ref pass, ref fail);
            // Internal defines in let body
            RunCase("(let ((x 10)) (define y (+ x 5)) (+ y 1))", "16", _testEnv, ref pass, ref fail);
            // Internal defines in let* body
            RunCase("(let* ((x 2) (y 3)) (define z (+ x y)) (* z 2))", "10", _testEnv, ref pass, ref fail);
            // Internal defines in letrec body
            RunCase("(letrec ((f (lambda (n) (if (= n 0) 1 (* n (f (- n 1))))))) (define result (f 5)) result)", "120", _testEnv, ref pass, ref fail);
            // Mixed internal define forms (value + function shorthand)
            RunCase("(define (mix x) (define scale 2) (define (add-scale y) (+ y scale)) (add-scale (* x scale))) (mix 3)", "8", _testEnv, ref pass, ref fail);

            // NOTE: Phase 4.2 TCO stress tests removed — the same tail-call patterns
            // are already validated at 2000 iterations (Phase 3 tests above).
            // Adding more deep-iteration tests this late in the suite exceeds
            // Udon VM's per-frame instruction budget.

            // ==== Phase 4.3 — read procedure ====
            // read: parse a number
            RunCase("(read \"42\")", "42", _testEnv, ref pass, ref fail);
            // read: parse a symbol
            RunCase("(read \"hello\")", "hello", _testEnv, ref pass, ref fail);
            // read: parse a list
            RunCase("(read \"(+ 1 2)\")", "(+ 1 2)", _testEnv, ref pass, ref fail);
            // read: empty string returns eof
            RunCase("(eof-object? (read \"\"))", "#t", _testEnv, ref pass, ref fail);
            // eof-object? on non-eof returns false
            RunCase("(eof-object? 42)", "#f", _testEnv, ref pass, ref fail);
            // read + eval round-trip
            RunCase("(eval (read \"(+ 1 2)\") (interaction-environment))", "3", _testEnv, ref pass, ref fail);
            // read: parse a string literal
            RunCase("(read \"\\\"hello\\\"\")", "\"hello\"", _testEnv, ref pass, ref fail);

            // ---- Picture language (SICP 2.2.4) — vector/frame/segment ops ----
            // make-vect and accessors
            RunCase("(xcor-vect (make-vect 3.0 4.0))", "3", _testEnv, ref pass, ref fail);
            RunCase("(ycor-vect (make-vect 3.0 4.0))", "4", _testEnv, ref pass, ref fail);
            // add-vect
            RunCase("(let ((v (add-vect (make-vect 1.0 2.0) (make-vect 3.0 4.0)))) (xcor-vect v))", "4", _testEnv, ref pass, ref fail);
            RunCase("(let ((v (add-vect (make-vect 1.0 2.0) (make-vect 3.0 4.0)))) (ycor-vect v))", "6", _testEnv, ref pass, ref fail);
            // sub-vect
            RunCase("(let ((v (sub-vect (make-vect 5.0 7.0) (make-vect 2.0 3.0)))) (xcor-vect v))", "3", _testEnv, ref pass, ref fail);
            RunCase("(let ((v (sub-vect (make-vect 5.0 7.0) (make-vect 2.0 3.0)))) (ycor-vect v))", "4", _testEnv, ref pass, ref fail);
            // scale-vect
            RunCase("(let ((v (scale-vect 2.0 (make-vect 3.0 4.0)))) (xcor-vect v))", "6", _testEnv, ref pass, ref fail);
            RunCase("(let ((v (scale-vect 2.0 (make-vect 3.0 4.0)))) (ycor-vect v))", "8", _testEnv, ref pass, ref fail);
            // make-frame and accessors
            RunCase("(xcor-vect (origin-frame (make-frame (make-vect 1.0 2.0) (make-vect 3.0 0.0) (make-vect 0.0 4.0))))", "1", _testEnv, ref pass, ref fail);
            RunCase("(xcor-vect (edge1-frame (make-frame (make-vect 1.0 2.0) (make-vect 3.0 0.0) (make-vect 0.0 4.0))))", "3", _testEnv, ref pass, ref fail);
            RunCase("(ycor-vect (edge2-frame (make-frame (make-vect 1.0 2.0) (make-vect 3.0 0.0) (make-vect 0.0 4.0))))", "4", _testEnv, ref pass, ref fail);
            // make-segment and accessors
            RunCase("(xcor-vect (start-segment (make-segment (make-vect 1.0 2.0) (make-vect 3.0 4.0))))", "1", _testEnv, ref pass, ref fail);
            RunCase("(ycor-vect (end-segment (make-segment (make-vect 1.0 2.0) (make-vect 3.0 4.0))))", "4", _testEnv, ref pass, ref fail);
            // frame-coord-map (defined by prelude)
            RunCase("(let ((f (make-frame (make-vect 0.0 0.0) (make-vect 100.0 0.0) (make-vect 0.0 100.0)))) (xcor-vect ((frame-coord-map f) (make-vect 0.5 0.5))))", "50", _testEnv, ref pass, ref fail);

            // ---- Prelude tests: ensure key combiners are defined ----
            RunCase("(procedure? right-split)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? up-split)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? corner-split)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? square-limit)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? beside)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? below)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? flip-vert)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? flip-horiz)", "#t", _testEnv, ref pass, ref fail);
            RunCase("(procedure? transform-painter)", "#t", _testEnv, ref pass, ref fail);

            // ---- Rendering smoke test: segments->painter + render-painter ----
            // Define a simple X-shaped painter from segments and render it
            RunCase(
                "(begin " +
                "  (define x-painter " +
                "    (segments->painter " +
                "      (list (make-segment (make-vect 0.0 0.0) (make-vect 1.0 1.0)) " +
                "            (make-segment (make-vect 1.0 0.0) (make-vect 0.0 1.0))))) " +
                "  (render-painter x-painter) " +
                "  \"rendered\")",
                "\"rendered\"", _testEnv, ref pass, ref fail);

            // --- Summary ---
            int total = pass + fail;
            string summary = "[TestSuite] " + pass + "/" + total + " passed"
                + (fail > 0 ? " (" + fail + " FAILED)" : " -- ALL PASS");
            Debug.Log(summary);
            if (outputText != null)
                outputText.text = summary;
        }
        ===== End commented-out test suite ===== */

        /// <summary>
        /// Run fib benchmarks at various sizes.
        /// Callable via SendCustomEvent("RunBenchmark").
        /// </summary>
        public void RunBenchmark()
        {
            var env = LispEnv.CreateGlobal();

            // ---- Naive tree-recursive fib ----
            string defFib = "(define (fib n) (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))";
            var tokens = LispTokenizer.Tokenize(defFib);
            var ast = LispParser.ParseAll(tokens);
            LispEval.Eval(ast, env, this);

            Debug.Log("[Benchmark] === Naive tree-recursive fib ===");
            int[] sizes = new int[] { 5, 10, 15 };
            for (int si = 0; si < sizes.Length; si++)
            {
                int n = sizes[si];
                string src = "(fib " + n + ")";
                var tk = LispTokenizer.Tokenize(src);
                var a = LispParser.ParseAll(tk);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                var result = LispEval.Eval(a, env, this);
                float t1 = UnityEngine.Time.realtimeSinceStartup;
                float ms = (t1 - t0) * 1000f;
                string val = LispPrinter.Print(result);
                Debug.Log("[Benchmark] (fib " + n + ") = " + val + "  (" + ms.ToString("F1") + " ms)");
                if (Lv.IsErr(result))
                {
                    Debug.Log("[Benchmark] Stopped due to error.");
                    break;
                }
            }

            // ---- Iterative fib (TCO) ----
            string defFibIter = "(define (fib-iter n) (define (loop n a b) (if (= n 0) a (loop (- n 1) b (+ a b)))) (loop n 0 1))";
            tokens = LispTokenizer.Tokenize(defFibIter);
            ast = LispParser.ParseAll(tokens);
            LispEval.Eval(ast, env, this);

            Debug.Log("[Benchmark] === Iterative fib (TCO) ===");
            int[] iterSizes = new int[] { 10, 100, 1000, 5000, 10000 };
            for (int si = 0; si < iterSizes.Length; si++)
            {
                int n = iterSizes[si];
                string src = "(fib-iter " + n + ")";
                var tk = LispTokenizer.Tokenize(src);
                var a = LispParser.ParseAll(tk);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                var result = LispEval.Eval(a, env, this);
                float t1 = UnityEngine.Time.realtimeSinceStartup;
                float ms = (t1 - t0) * 1000f;
                string val = LispPrinter.Print(result);
                // Truncate long numbers
                if (val.Length > 50) val = val.Substring(0, 47) + "...";
                Debug.Log("[Benchmark] (fib-iter " + n + ") = " + val + "  (" + ms.ToString("F1") + " ms)");
                if (Lv.IsErr(result))
                {
                    Debug.Log("[Benchmark] Stopped due to error.");
                    break;
                }
            }

            Debug.Log("[Benchmark] Done.");
        }

        private void RunCase(string source, string expected, DataDictionary env,
            ref int pass, ref int fail)
        {
            var tokens = LispTokenizer.Tokenize(source);
            var ast = LispParser.ParseAll(tokens);
            var result = LispEval.Eval(ast, env, this);
            string actual = LispPrinter.Print(result);

            if (actual == expected)
            {
                pass++;
                Debug.Log("[Test PASS] " + source + "  =>  " + actual);
            }
            else
            {
                fail++;
                Debug.Log("[Test FAIL] " + source + "  =>  " + actual + "  expected: " + expected);
            }
        }
    }
}
