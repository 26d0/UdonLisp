# AGENTS.md

This project implements a Lisp interpreter that runs on VRChat Worlds
(UdonSharp / U#).

All documentation must be written in English.

You have little built-in knowledge of Unity and VRChat World development.
Always consult `References/` before making assumptions about APIs or
constraints specific to Udon, UdonSharp, or the VRChat SDK.

The `References/` directory is gitignored, so you may freely clone reference documents into it.

## Referencing `References/` with subagents

When you need to look up information across multiple reference repos, launch
parallel `explore` subagents — one per repo — in a **single message** so they
run concurrently. Each subagent should be scoped to its repo's directory:

| Subagent scope (path)                          | Content                  |
| ---------------------------------------------- | ------------------------ |
| `References/vrchat-community-creator-docs/`    | VRChat Creator Docs      |
| `References/vrchat-community-UdonSharp/`       | UdonSharp (community)    |
| `References/MerlinVR-UdonSharp/`               | UdonSharp (MerlinVR)     |
| `References/madjin-awesome-vrchat/`            | Awesome VRChat resources |
| `References/SICP/`                             | SICP (2nd ed) full text  |

Always tell each subagent to **return only the relevant excerpts and file
paths** so the caller can synthesize the results without redundant context.

---

## Project overview

A Lisp interpreter (Tokenizer → Parser → Evaluator → Printer) running on
UdonSharp inside a VRChat World. The user types Lisp into a TMP InputField,
presses a Run button, and the result appears in a TMP output label.

### Current status (2026-03-05)

**Working.** 357/357 test cases pass in ClientSim. The full R5RS/SICP
compatibility plan (Phases 0–4) is complete, plus the SICP Section 2.2.4
Picture Language is implemented. The interpreter supports recursive
functions, proper tail calls, continuations, internal definitions,
quasiquote, vectors, characters, strings, `read`, and more.

The Picture Language adds vector/frame/segment types, 18 new builtins,
a Scheme prelude with all SICP combiners (`beside`, `below`, `flip-vert`,
`flip-horiz`, `rotate90`, `right-split`, `up-split`, `corner-split`,
`square-limit`, etc.), and a rendering pipeline that draws to a 512x512
Texture2D displayed on a Quad in the VRChat world.

**Image downloading works in ClientSim** after patching the bundled
UniTask with missing `cancelImmediately` overloads (see "UniTask
ClientSim patches" below). The `rogers` painter is defined
automatically when the image loads.

**Networking is implemented and verified in Build & Test (2 clients).**
REPL state, environment (via code replay), and picture language canvas
all sync correctly across players. See "Networking" section below and
`Networking.md` for the full spec.

## Directory layout

```
Assets/
  Scenes/
    UdonLispWorld.unity/          # directory (Unity quirk)
      UdonLispWorld.unity         # the actual scene file
  UdonLisp/
    Scripts/
      LispRunner.cs               # UdonSharpBehaviour entry point (UI + test suite)
      LispRunner.asset            # UdonSharpProgramAsset for LispRunner
      LispTypes.cs                # Lv static class — value factories & accessors
      LispTokenizer.cs            # Static tokenizer → DataList of token dicts
      LispParser.cs               # Static parser → DataList AST
      LispEnvironment.cs          # Static LispEnv — DataDictionary scope chain
      LispEvaluator.cs            # Static LispEval — eval + special forms + builtins
      LispPrinter.cs              # Static LispPrinter — value → string
    Materials/
      PictureCanvas.mat           # Unlit/Texture material for picture language canvas
Packages/
  com.vrchat.worlds/              # VRChat Worlds SDK (read-only)
References/                       # gitignored reference repos
```

## Architecture

All interpreter modules (LispTypes, LispTokenizer, LispParser,
LispEnvironment, LispEvaluator, LispPrinter) are **plain static classes**
— not UdonSharpBehaviours. Only `LispRunner` is an UdonSharpBehaviour.

Static-only utility classes do NOT need `.asset` files.
Only `UdonSharpBehaviour` subclasses need a paired `.asset`.

### LispValue encoding (DataList-based tagged tuples)

Every Lisp value is a `DataList` whose element 0 is a type-tag string:

```
["nil"]                               nil
["bool", true/false]                  boolean
["int", (double)n]                    integer (stored as double in DataToken)
["float", (double)f]                  float
["sym", "name"]                       symbol
["str", "text"]                       string
["list", elem0, elem1, ...]          list (elements are nested DataLists)
["fn", paramsDataList, body, envRef]  closure
["builtin", "name"]                   built-in function
["err", "message"]                    error
["vect", (double)x, (double)y]       2D vector (picture language)
["frame", origin, edge1, edge2]      coordinate frame (picture language)
["segment", start, end]              line segment (picture language)
```

Tag constants and factory/accessor methods live in `Lv` (LispTypes.cs).

### Token encoding

`DataDictionary { "t": tokenType, "v": rawValue }`
Token types: `lp`, `rp`, `qt`, `int`, `float`, `str`, `bool`, `sym`

### Environment encoding

`DataDictionary { "__parent__": parentEnvOrNull, ...bindings... }`
Each binding: key = variable name (string), value = DataToken wrapping a LispValue DataList.

## Implemented language features

### Special forms
`quote`, `quasiquote` (with `unquote`/`unquote-splicing`),
`if`, `cond` (with `else`), `case`, `when`, `unless`,
`define` (value + function shorthand + internal definitions),
`set!`, `lambda`, `begin`, `let`, `let*`, `letrec`, `named let`,
`and`, `or`, `do`, `delay`, `define-record-type` (not yet)

### Built-in functions
**Arithmetic**: `+`, `-`, `*`, `/`, `mod`, `quotient`, `remainder`, `modulo`,
`gcd`, `lcm`, `abs`, `max`, `min`, `floor`, `ceiling`, `truncate`, `round`,
`sqrt`, `expt`, `exact->inexact`, `inexact->exact`

**Comparison**: `=`, `<`, `>`, `<=`, `>=`, `not`

**Type predicates**: `number?`, `integer?`, `zero?`, `positive?`, `negative?`,
`odd?`, `even?`, `exact?`, `inexact?`, `string?`, `symbol?`, `list?`,
`pair?`, `bool?`, `char?`, `vector?`, `procedure?`, `promise?`, `eof-object?`

**Lists**: `list`, `car`, `cdr`, `cons`, `length`, `null?`, `set-car!`,
`set-cdr!`, `append`, `reverse`, `list-tail`, `list-ref`, `memq`, `member`,
`assq`, `assoc`, `cXXXr` (all ad-compositions up to 4 deep)

**Higher-order**: `apply`, `map`, `for-each`

**Strings**: `string-append`, `string-length`, `number->string`,
`string->number`, `make-string`, `string`, `string-ref`, `string-set!`,
`string-copy`, `substring`, `string->list`, `list->string`,
`string=?`, `string<?`, `string>?`, `string<=?`, `string>=?`,
`string-ci=?`, `string-ci<?`, `symbol->string`, `string->symbol`

**Characters**: `char=?`, `char<?`, `char>?`, `char<=?`, `char>=?`,
`char-ci=?`, `char-ci<?`, `char-alphabetic?`, `char-numeric?`,
`char-whitespace?`, `char-upcase`, `char-downcase`,
`char->integer`, `integer->char`

**Vectors**: `vector`, `make-vector`, `vector-ref`, `vector-set!`,
`vector-length`, `vector->list`, `list->vector`, `vector-fill!`

**I/O**: `display`, `write`, `newline`, `read`

**Control**: `eval`, `scheme-report-environment`, `interaction-environment`,
`values`, `call-with-values`, `call/cc`, `call-with-current-continuation`,
`dynamic-wind`, `force`, `make-promise`

**Equality**: `eq?`, `eqv?`, `equal?`

**Picture Language (SICP 2.2.4)**:
*Vectors*: `make-vect`, `xcor-vect`, `ycor-vect`, `add-vect`, `sub-vect`, `scale-vect`
*Frames*: `make-frame`, `origin-frame`, `edge1-frame`, `edge2-frame`
*Segments*: `make-segment`, `start-segment`, `end-segment`
*Rendering*: `draw-line`, `image-painter`, `render-painter`
*Scheme prelude* (loaded automatically): `frame-coord-map`, `transform-painter`,
`flip-vert`, `flip-horiz`, `rotate90`, `rotate180`, `rotate270`,
`beside`, `below`, `identity`, `segments->painter`, `square-of-four`,
`flipped-pairs`, `right-split`, `up-split`, `corner-split`, `square-limit`

## Test suite

`LispRunner` has a built-in test suite (357 cases) callable via:
- **Inspector**: set `runTestsOnStart = true` (default), enter Play
- **MCP/SendCustomEvent**: call `RunTestSuite` on the LispRunner UdonBehaviour

Tests use a **fresh environment** for deterministic results.
Output in console: `[Test PASS]` / `[Test FAIL]` per case, summary at end.

To run tests via MCP: enter Play mode and check console for
`[TestSuite] N/N passed`.

## Scene structure (UdonLispWorld)

```
VRCWorld              — VRCSceneDescriptor + PipelineManager
Environment           — Ground plane + skybox
SpawnPoint
Canvas                — World Space, scale 0.001, layer Default
  ├─ InputField (TMP) — TMP_InputField
  ├─ OutputText       — TextMeshProUGUI
  ├─ LispRunner       — LispRunner (UdonSharpBehaviour) + UdonBehaviour
  └─ RunButton        — Button → UdonBehaviour.SendCustomEvent("OnRunButtonClicked")
PictureCanvas         — Quad at (1.5, 1.5, 2.0), rotation (0, 180, 0), PictureCanvas.mat
EventSystem
```

### Key scene wiring rules
- **VRCUiShape** + **BoxCollider** must be on the Canvas GameObject
- **Canvas layer = Default** (layer 0), NOT "UI"
- **Navigation = None** on interactive UI elements (prevents WASD capture)
- UI button OnClick targets the **UdonBehaviour component** (not the GameObject)
  via `SendCustomEvent` with string = exact public method name
- MCP `set_property` CANNOT correctly set UI event `m_Target`
  — edit scene YAML directly if re-wiring is needed

## UdonSharp constraints (verified)

### What works
- Static classes with static methods (the core pattern used here)
- `[RecursiveMethod]` attribute for recursive methods
- Enums, switch statements (int / string / enum)
- `string` concatenation and `$"..."` interpolation
- VRC DataContainers: `DataList`, `DataDictionary`, `DataToken` (`using VRC.SDK3.Data;`)
- `DataToken` stores numbers as `double`; ints round-trip via `(double)n` / `(int)tok.Number`
- `int[]` arrays, `DataList[]` arrays
- `new DataList()` + `.Add()` inside methods

### What does NOT work
- **Non-behaviour class instance methods** → `System.NotImplementedException`
  at `BoundInvocationExpression.cs:465`. The MerlinVR source has test stubs
  but the production SDK throws. **Do not use `new SomeClass().Method()`.**
- `List<T>`, `Dictionary<K,V>` on UdonSharpBehaviours (use DataContainers)
- `DataList` / `DataDictionary` initializer syntax inside methods
  (only works in field declarations)
- `ref` / `out` parameters on user methods (use `int[]` wrapper pattern)

## After changing UdonSharp scripts

**Always** run `VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets`
(menu path: `VRChat SDK/Udon Sharp/Refresh All UdonSharp Assets`)
or via MCP `execute_menu_item`. Failing to do so causes
"The referenced script (Unknown)" errors on UdonBehaviours.

A full refresh cycle: save script → `refresh_unity(compile=request,
mode=force, scope=all)` → execute the menu item → verify zero errors
in console.

## Key GUIDs

| Asset                  | GUID                               |
| ---------------------- | ---------------------------------- |
| LispRunner.cs          | `11dbf9d4e73f77e4491f316607b91861` |
| LispRunner.asset       | `9b659babff6dd084bb30033c7acfd26c` |
| UdonSharpProgramAsset  | `c333ccfdd0cbdbc4ca30cef2dd6e6b9b` (m_Script) |
| UdonBehaviour m_Script | `45115577ef41a5b4ca741ed302693907` |
| TMP font asset         | `8f586378b4e144a9851e7b34d9b748ee` |

## UniTask ClientSim patches

The bundled UniTask in `com.vrchat.base` is older than the version that
`VRCSDK3.dll` was compiled against. The DLL calls overloads with a
trailing `bool cancelImmediately` parameter that don't exist in source.
This causes `MissingMethodException` at runtime in ClientSim when
`VRCImageDownloader.DownloadImage()` is called.

**Patched files** (add overloads that forward to existing implementations):

1. `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/UniTask/Runtime/UniTask.WaitUntil.cs`
   - Added `WaitUntil(Func<bool>, PlayerLoopTiming, CancellationToken, bool)`
   - Added `WaitWhile(Func<bool>, PlayerLoopTiming, CancellationToken, bool)`

2. `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/UniTask/Runtime/UnityAsyncExtensions.cs`
   - Added `ToUniTask(UnityWebRequestAsyncOperation, IProgress<float>, PlayerLoopTiming, CancellationToken, bool)`

These patches are local-only (Packages is not committed). If the VRChat
SDK updates, check whether these overloads have been added upstream and
remove the patches if so.

## Image download notes

- VRChat limits downloaded images to **2048×2048 pixels** max
- ClientSim may enforce a stricter limit (the original 1699×2059
  Wikipedia image failed with `MaximumDimensionExceeded`)
- Use Wikipedia thumbnail URLs for reliable sizing:
  `.../thumb/.../512px-Filename.jpg`
- Wikipedia may return **429 Too Many Requests** if polled rapidly
  (transient; retry after a few seconds)

## MCP play-mode limitations

- `manage_components.set_property` does **not** work during Play mode
- To test programmatically, use `runTestsOnStart = true` or add
  temporary calls in `Start()`, then read console logs after Play
- UI text fields cannot be set via MCP during Play; manual interaction
  or the self-test pattern is required

## Next steps (ideas)

- Multi-line output / REPL history in the UI
- Error line/column reporting from tokenizer
- `load` / prelude with standard library definitions
- `define-record-type` (R5RS records)
- `syntax-rules` / `define-syntax` (hygienic macros — complex)
- Persistent environment across multiple Run presses (already works via `globalEnv`)
