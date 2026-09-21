# Open Splatter

A seed based procedural paint splatter for Unity. A composition layer lays a whole painting from many splats, a GPU painter puts one splat or a whole painting on a texture, and an outer glow can go on top.

![Six splats from six seeds](Documentation~/images/splats.jpg)

We couldn't find a good generator that fit our needs when we create the game, so here it is if anyone else needs it =)

## Install

Unity 2022.3 or later. In the Package Manager choose *Add package from git URL* and paste:

```
https://github.com/cover-up/open-splatter.git
```

Or add it to `Packages/manifest.json`:

```json
"com.coverup.splatter": "https://github.com/cover-up/open-splatter.git"
```

The *Living Backdrop* sample under the package's Samples adds one component to a scene and paints a full-screen backdrop that keeps changing. *Window > Open Splatter > Render Preview* writes PNGs of a composition in each palette, one of them with the glow, a strip of single splats and a living painting after a few changes, without entering play mode.

## Use

A whole painting, painted in one go:

```csharp
using CoverUp.Splatter;

var composition = SplatComposition.Generate(seed: 7, SplatPalette.Rainbow, 1920, 1080);
var canvas = new SplatCanvas(1920, 1080);
canvas.Begin(composition);
canvas.PaintAll();
rawImage.texture = canvas.Texture;   // dispose the canvas when you are done with the texture
```

Painted one splat per frame instead, so it builds up in front of the player:

```csharp
void Update() { if (!canvas.Done) canvas.PaintNext(); }
```

Splats on their own, with no composition. A recipe comes from a seed, a core radius (in the rules' frame px, a painting 1672 px wide) and either a hue or a palette that picks the colour:

```csharp
var recipe = SplatRules.Generate(seed: 3, rcFramePx: 40f, scale: 1.5f, hue: 200f);
var themed = SplatRules.Generate(seed: 4, rcFramePx: 40f, scale: 1.5f, SplatPalette.Cool);
canvas.Paint(recipe, x: 300f, y: 200f);                             // onto any canvas, at a point in canvas px
var single = SplatCanvas.RenderSingle(themed, out Vector2 centre);  // or on its own transparent texture, straight alpha, ready for a RawImage
```

`Generate` is safe to call from a worker thread; only the canvas touches the GPU.

## The model

Every splat is a recipe of primitives (lumpy discs and wavering capsules) plus the parameters of a colour field, described in units of its own core radius, Rc. Sixty-odd rules give each part its numbers: how far the web reaches, how many drops there are and how their sizes fall off, how long the chains run and where they thin to a line, how dense the spray is and where it peaks. Each rule carries a measured value and a range; a seed draws its own value from every range once, and that draw is the splat's character, so two seeds differ in kind rather than in noise. The rule table is in [SplatRules.cs](Runtime/SplatRules.cs), with what each number is next to it.

The rules were measured on a set of reference paintings, hand-checked against them part by part, and kept in those units so the same splat reads right at any size.

A composition is the same idea one level up: a bed of large, dark, heavily mottled violet splats goes down first, then thirteen to twenty front cores placed by a separation rule within a band of the frame, given hues by area quota from the palette so every painting lands on the same proportions, never two touching splats of one hue. Throws radiate outward from the frame's centre, small spatter drops fill the halo, spray landing on paint already down mostly bounces off, and everything is laid down in one order, as paint thrown at a canvas one colour at a time.

![The Rainbow, Cool and Muted palettes on one seed](Documentation~/images/palettes.jpg)

![A composition, then the same one after three arrivals and two departures](Documentation~/images/living.jpg)

## Living paintings

A composition is a list of items in paint order, grouped by what arrived together. The list is meant to be edited:

- `PreparePush(rng)` chooses one more core, placed and coloured by the same rules against what is already there, with its throws; it reads only the cores, so it can run on a worker while the main thread paints. `ApplyPush` appends it, and `PaintNext` paints just the new items.
- `PopOldest()` removes the oldest front group (the bed never goes). The canvas cannot unpaint, so rebuild a second canvas from the trimmed list and crossfade; the sample does exactly this.
- `AddSplat(recipe, x, y, hole: true)` erases a splat's shape to the background instead of painting it. Erased paint reads as bare canvas to the rules that follow, and the glow leaves it dark.

## Tuning

`SplatPalette` is the colour theme: which hues a painting draws, how saturated and bright they are, and whether one splat is white. `Rainbow`, `Cool` and `Muted` are presets, each a fresh copy whose fields are yours to change, and `Presets` and `ByName` reach them for a dropdown or a console. A composition draws its whole hue set from it; `Pick` gives one colour for a splat on its own, which is what the palette overload of `Generate` uses.

`SplatRuleOverrides` changes any rule's range without touching the table, for a composition and every splat it adds:

```csharp
var rules = new SplatRuleOverrides()
    .Scale(SplatRules.DropsN, 1.5f)          // half again as many drops
    .Set(SplatRules.WebReach, 1.8f, 2.2f)    // a tighter web
    .Set(SplatRules.SprayN, 0f);             // no spray at all
var composition = SplatComposition.Generate(seed, SplatPalette.Cool, 1920, 1080, rules);
```

`SplatCanvas.Gain` multiplies the paint's colour on the way in; above 1 the paint exceeds white in the half-float canvas, which a bloom post-process thresholded at white picks up while ordinary UI does not. `SprayOnPaint` and `ChainOnPaint` are the odds that spray landing on paint survives and the opacity left to a chain over paint; a composition brings its own drawn values.

`SplatGlow` adds an outer glow of each splat's own colour, only in the black gaps, so the paint itself is untouched. It renders into a texture of its own, so show that one:

```csharp
var glow = new SplatGlow(1920, 1080) { Strength = 0.5f, Radius = 40f * composition.Scale };
rawImage.texture = glow.Texture;
void LateUpdate() { glow.Render(canvas); }   // reruns only on frames the paint changed
```

![The same paint without and with the glow](Documentation~/images/glow.jpg)

## Layout

- `Runtime/SplatRules.cs`: the rule table, the recipe, the generator, the random stream, value noise and a small Voronoi. Pure C#.
- `Runtime/SplatPalette.cs`: the colour themes, for one splat or a whole painting. Pure C#.
- `Runtime/SplatComposition.cs`: the composition rules, the layer order, pushes, pops and holes. Pure C#.
- `Runtime/SplatCanvas.cs` and `Runtime/Shaders/SplatPaint.shader`: the painter and its passes (primitives, polar outlines, rounding, compositing with the colour field).
- `Runtime/SplatGlow.cs` and `Runtime/Shaders/SplatGlow.shader`: the outer glow, a post step over any painted texture.
- `Editor/SplatterPreview.cs`: the preview renderer.
- `Samples~/Backdrop/`: the living backdrop.

## Licence

MIT. See [LICENSE](LICENSE).
