# Open Splatter

A seed based procedural paint splatter for Unity. A composition layer lays a whole painting from them, and a GPU painter puts it on a texture with an optional outer glow.

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

The *Living Backdrop* sample under the package's Samples adds one component to a scene and paints a full-screen backdrop that keeps changing. *Window > Open Splatter > Render Preview* writes PNGs of a composition in each palette, a strip of single splats and a living painting after a few changes, without entering play mode.

## Use

A whole painting, painted in one go:

```csharp
using CoverUp.Splatter;

var composition = SplatComposition.Generate(seed: 7, SplatPalette.Rainbow, 1920, 1080);
var canvas = new SplatCanvas(1920, 1080);
canvas.Begin(composition);
canvas.PaintAll();
canvas.Present();
rawImage.texture = canvas.Texture;   // dispose the canvas when you are done with the texture
```

Painted one splat per frame instead, so it builds up in front of the player:

```csharp
void Update()
{
    if (!canvas.Done) canvas.PaintNext();
    if (canvas.Dirty) canvas.Present();
}
```

One splat on its own transparent texture, for an effect or a sprite:

```csharp
var recipe = SplatRules.Generate(seed: 3, rcFramePx: 40f, scale: 1.5f, hue: 200f);
var single = SplatCanvas.RenderSingle(recipe, out Vector2 centre);   // straight alpha, ready for a RawImage
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

`SplatPalette` sets what hues a painting draws, how saturated and bright they are, and whether one splat is white; `Rainbow`, `Cool` and `Muted` are presets and the fields are yours to change.

`SplatRuleOverrides` changes any rule's range without touching the table, for a composition and every splat it adds:

```csharp
var rules = new SplatRuleOverrides()
    .Scale(SplatRules.DropsN, 1.5f)          // half again as many drops
    .Set(SplatRules.WebReach, 1.8f, 2.2f)    // a tighter web
    .Set(SplatRules.SprayN, 0f);             // no spray at all
var composition = SplatComposition.Generate(seed, SplatPalette.Cool, 1920, 1080, rules);
```

`SplatCanvas.Gain` multiplies the paint's colour on the way in; above 1 the paint exceeds white in the half-float canvas, which a bloom post-process thresholded at white picks up while ordinary UI does not. `SplatCanvas.Glow` and `GlowRadius` add an outer glow of each splat's own colour, only in the black gaps, so the paint itself is untouched.

## Layout

- `Runtime/SplatRules.cs`: the rule table, the recipe, the generator, the random stream, value noise and a small Voronoi. Pure C#.
- `Runtime/SplatComposition.cs`: palettes, the composition rules, the layer order, pushes, pops and holes. Pure C#.
- `Runtime/SplatCanvas.cs` and `Runtime/Shaders/`: the painter and its passes (primitives, polar outlines, rounding, compositing with the colour field, the glow).
- `Editor/SplatterPreview.cs`: the preview renderer.
- `Samples~/Backdrop/`: the living backdrop.

## Licence

MIT. See [LICENSE](LICENSE).
