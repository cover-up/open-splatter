# Changelog

## 0.1.1

Faster splats, same splats: generating one takes about 45% less time, and every seed still gives exactly the same splat, bit for bit. The web's seed placement checks a flat grid nearest cells first instead of hashing every cell it looks at, the neighbour pass no longer copies its triangle list for every point, and the web's loops stop allocating small arrays.

## 0.1.0

First release: the rule-based splat generator, the Rainbow, Cool and Muted palettes for single splats and whole compositions, the composition layer, the GPU painter, the outer glow as a separate post step, the living-painting API (push, pop, erase), an editor preview and the Living Backdrop sample.
