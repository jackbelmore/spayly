# Parity screenshots: web vs Godot

`web/` holds the web build's reference shots (run `godot/tools/web-parity-shots.sh`), `godot/` the Godot build's
(`godot/tools/run-capture.sh godot/Spayly.Game --frames 1800 --out godot/parity/godot -- --actions "<see below>"`).
Both are gitignored; regenerate them. Compare with `godot/tools/shotcheck.py <godot.png> --ref <web.png>`.

Action string used for the Godot side (2026-09-24):
```
viewTabletop;setDiorama:1;wait:150;shot:tabletop;viewInner;wait:150;shot:inner;viewOuter;wait:150;shot:outer;
flyAbove:earth;wait:240;shot:earth;flyAbove:saturn;wait:180;shot:saturn;flyAbove:moon;wait:180;shot:moon;
select:io;flyAbove:io;wait:180;shot:jupiter-io;flyAbove:sun;wait:150;shot:sun;viewTabletop;setWarp:1000000;
wait:150;shot:warp;setWarp:1;setMode:asteroid;wait:20;shot:asteroid-mode;setMode:erase;wait:20;shot:erase-mode
```

| scenario | web non-black / lum | godot non-black / lum | PSNR |
| --- | --- | --- | --- |
| tabletop | 0.301 / 8.3 | 0.293 / 8.2 | 20.6 dB |
| inner | 0.376 / 14.3 | 0.335 / 13.8 | 20.3 |
| outer | 0.308 / 8.5 | 0.296 / 8.3 | 20.5 |
| earth | 0.429 / 19.8 | 0.421 / 21.5 | 15.6 (Earth rotated 47 min between captures) |
| saturn | 0.473 / 30.0 | 0.467 / 33.4 | 20.5 |
| moon | 0.418 / 18.7 | 0.410 / 20.6 | 20.6 |
| jupiter-io | 0.430 / 20.2 | 0.426 / 22.5 | 20.1 |
| sun | 0.729 / 56.2 | 0.710 / 60.6 | 20.8 |
| warp | 0.509 / 14.6 | 0.508 / 15.6 | 20.3 |
| asteroid-mode | 0.543 / 15.1 | 0.542 / 15.7 | 18.6 |
| erase-mode | 0.529 / 15.0 | 0.528 / 16.0 | 19.1 |

PSNR around 20 dB is the floor for two renderers with different fonts, star seeds and body rotation phases; the
non-black and luminance columns are the parity signal. A visual side-by-side of Earth and tabletop was checked by eye.
