# Third-party notices

ScreenAI.Net's original code is licensed under the repository's MIT license.

## clv-locro

The native-call sequence and Windows x64 `SkBitmap`/synthetic `SkPixelRef` layout were adapted from
[clv-locro](https://github.com/sergiocorreia/clv-locro), copyright (c) 2026 Sergio Correia,
licensed under the MIT License. Its license notice is available in that project's repository.

## Chromium source descriptions

The exported-function declarations and `VisualAnnotation` protobuf schema were derived from Chromium source files:

- `services/screen_ai/screen_ai_library_wrapper_impl.h`
- `services/screen_ai/proto/chrome_screen_ai.proto`

Chromium source is distributed under a BSD-style license. See the
[Chromium license](https://chromium.googlesource.com/chromium/src/+/main/LICENSE).

## Google.Protobuf

The NuGet library uses Google.Protobuf to decode the native `VisualAnnotation` wire result.
Google.Protobuf is licensed under the BSD 3-Clause License. See the package and its upstream repository for its full notice.

## Chrome Screen AI component

`chrome_screen_ai.dll`, its models, configurations, and other component assets are not part of ScreenAI.Net and are not covered by
the ScreenAI.Net license. This project neither grants permission to obtain or redistribute those assets nor represents that a given
use complies with Google's terms. Callers must provide a trusted, matching component and independently review the applicable terms
and the component's bundled `THIRD_PARTY_LICENSES` file.
