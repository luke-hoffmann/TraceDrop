# Models

`teed.onnx` (255 KB, MIT) is committed: TEED edge detector, exported from the BIPED checkpoint of
https://github.com/xavysp/TEED by `tools/models/export_teed.py`.

The larger models are downloaded into this folder at build time by `tools/models/fetch_models.py`
(or `build.ps1`) and embedded into Tracedrop.exe; see `tools/models/models.json` for sources and licences.
Any model that is missing is simply left out and Tracedrop uses its classic method for that step.
