# Third-party software and models

Tracedrop is MIT licensed. Everything it bundles is open source under permissive licences; nothing is GPL
or AGPL (which is why it has its own tracer instead of Potrace, which is GPL).

## Libraries

| Component | Licence | Used for |
|---|---|---|
| [.NET 8 runtime and Windows Forms](https://github.com/dotnet/runtime) | MIT | The app itself |
| [OpenCV](https://opencv.org) via [OpenCvSharp4](https://github.com/shimat/opencvsharp) | Apache-2.0 | Image filters, thresholds, morphology, thinning |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) with DirectML | MIT | Running the AI models on any DirectX 12 GPU or the CPU |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) (ImageMagick, libheif, libde265 and other codecs) | Apache-2.0 / ImageMagick licence; codecs under their own open-source licences | Reading HEIC/HEIF, AVIF, GIF, PSD and other formats |
| [netDxf](https://github.com/haplokuon/netDxf) | MIT | Writing DXF files |

HEIC pictures use the HEVC codec, which is covered by patents in some countries; Tracedrop only decodes.

## AI models

| Model | Licence | Source |
|---|---|---|
| TEED, Tiny and Efficient Edge Detector (Soria et al., ICCV-W 2023) | MIT | https://github.com/xavysp/TEED, BIPED checkpoint, exported to ONNX by `tools/models/export_teed.py` |
| BiRefNet-lite, Bilateral Reference for dichotomous image segmentation (Zheng et al., 2024) | MIT | https://github.com/ZhengPeng7/BiRefNet, ONNX export by onnx-community |
| Informative Drawings, learning to generate line drawings (Chan, Durand, Isola, CVPR 2022) | MIT | https://github.com/carolineec/informative-drawings, ONNX export by rocca |

## Algorithms

- Zhang & Suen (1984), fast parallel thinning.
- Winnemöller et al. (2012), XDoG stylised line drawing (built-in line art without a model).
- Schneider (1990), "An Algorithm for Automatically Fitting Digitized Curves", Graphics Gems.
- Kåsa (1976), algebraic circle fit.
- The Taildrop-style window, cleanup watcher and drag-out are adapted from Taildrop (same author).
