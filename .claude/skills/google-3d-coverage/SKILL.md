---
name: google-3d-coverage
description: Evidence that Google has no 3D reconstruction of the synagogue site, and how to re-run the test at any address — Street View depth-map plane analysis with the Python streetlevel package, plus the pano-column-to-bearing maths. Load before investigating occlusion, ghosting, Streetscape Geometry coverage, or 3D tile detail at a site.
---

# Does Google have building geometry here?

Measured at `synagogue-01` (32.083688, 34.815228) on 2026-08-20. The conclusion is summarised
in CLAUDE.md; this is the evidence and the method.

### Confirmed: Google has no building geometry at this site

Checked 2026-08-20 with the Python `streetlevel` package against the Street View panorama
standing right outside the synagogue — `pW3oIq-OzNNDlks5mYZClg`, at 32.083688, 34.815228,
dated 2022-07, 13.9 m north of the surveyed facade midpoint. Its depth map has **56 planes
and not one of them is the synagogue**:

| planes | pixels | what |
|---|---|---|
| plane 0 | 49.7% | `INFINITELY_FAR` — literally everything above the horizon |
| 1–8, 10–55 | 50.1% | all near-horizontal (`\|n_z\| ≈ 1`), d = 2.4–20 m: road, pavement, the raised plaza |
| plane 9 | 0.24% | the **only** vertical plane — 89.4 m away at bearing 347°–1°, i.e. the hillside to the north |

In the building's own column band (cols 339–442, spanning cornerA→cornerB) 13180 of 13184
above-horizon pixels are plane 0. Below the horizon it is ground planes 1–2 (d ≈ 2.4–2.5 m,
the road) and 6–8 (d ≈ 2.9–3.4 m, the podium ~0.5–0.9 m up). Google models the raised plaza
the building stands on, and stops there.

Not a one-off capture — every historical pano at the same spot has exactly one wall plane and
it is always that same distant one: `Na7Kitj22j9zOWd9HNlELg` 2019-01 @ 87.2 m,
`RD5IvfXg0YOW8o2YgE79mQ` 2017-12 @ 82.4 m, `VTOiSl88U0TrQp9dQPRKew` 2015-02 @ 84.3 m,
`6bbvLjrbYIhLlBsx4Cm_jg` 2011-11 @ 87.1 m. Panos 50–95 m away on the same street *do* carry
near-field walls at 10–50 m, so the reconstruction pipeline works fine in the neighbourhood;
it just has nothing for this junction.

**Caveat**: ARCore Streetscape Geometry is served from Google's 3D building tiles, not from
Street View depth maps, so this is not formally the same dataset. But it is the same
reconstruction problem at the same address and it matches what the device already showed. Do
not spend more time on ghosting here — **the cutout is the only occlusion mechanism that will
ever work at this site**.

Reproducing it (installing `streetlevel` is the fiddly part — `pyfrpc`, a Mapy.cz dep, needs a
C toolchain, and pip's SSL trust is broken on this machine):

```bash
pip install --trusted-host pypi.org --trusted-host files.pythonhosted.org --no-deps streetlevel
pip install --trusted-host pypi.org --trusted-host files.pythonhosted.org \
    pyproj requests aiohttp pillow numpy bd09convertor CoordinatesConverter pyexiv2
```

`p.depth` only exposes the resolved per-pixel distance; the planes come from the internals:

```python
from streetlevel.streetview import api, depth as dpt
resp = api.find_panorama_by_id(PANOID, download_depth=True)
b64  = resp[1][0][5][0][5][1][2]          # longest base64 blob in the response
raw  = dpt.decode_b64(b64); h = dpt.parse_header(raw)
pl   = dpt.parse_planes(h, raw)           # {"planes": [{"n","d"}], "indices": [512*256]}
# a plane is a WALL when abs(n[2]) < 0.4, ground when abs(n[2]) > 0.85
```

Depth-map column ↔ true bearing, needed to aim at a surveyed coordinate. Column 0 is
`pano.heading + 180`, i.e. the **image centre is the heading**:

```python
bearing = (degrees(pano.heading) + 180 + (col + 0.5) / 512 * 360) % 360
```

