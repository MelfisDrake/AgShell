# Third-party notices

License texts below accompany AgShell installer and portable distributions. They are retained in English as supplied by their authors.

## Scope and provenance

This manifest records the checked components and license sources. It does not assert that a NuGet package source revision is known where the package does not provide one.

The Windows ConPTY package is **1.24.261001001**; both bundled binary resources report FileVersion **1.24.2610.01001** and ProductVersion **1.24.261001001**. Its nuspec declares MIT and Microsoft Corporation. [Terminal release v1.24.12741.0](https://github.com/microsoft/terminal/releases/tag/v1.24.12741.0), source commit `7ff11ee107c984e236960ec948b450ccbc10187b`, includes `Microsoft.Windows.Console.ConPTY.1.24.261001001.nupkg`. The two shipped x64 binaries from that asset were compared byte-for-byte with the restored NuGet package: `conpty.dll` SHA-256 `5afea6c9480e2c7dfd195376dd10996b0ae25cdc6f014e6247fd8aedc8c34852`; `OpenConsole.exe` SHA-256 `6f8e68dec4e8e5e15a54ecc8afa127b0e62139edc004ffbb4892125ff01d5737`. The container hash differs between GitHub and NuGet; the binary payloads match. Its source LICENSE and applicable sections of NOTICE.md are retained. Source build references and shared headers confirm Chromium numerics, fmt, interval_tree, wyhash, stb, Oklab, ColorBrewer and WIL in the host graph; GSL 3.1.0 is pinned by that source's vcpkg.json. Terminal-only JSON/cmark/fzf/UI XAML/VirtualDesktopUtils notices are not copied.

The native VT uses the Ghostty Zig module with SIMD and Kitty graphics enabled by default. Its uucode, Highway, simdutf, and Wuffs dependencies are included below. Highway is distributed under its BSD-3-Clause alternative; Wuffs under its MIT alternative. simdutf MIT and Apache texts are retained together: the vendored scalar UTF-8 validator explicitly credits Apache-licensed Google Fuchsia code (simdutf.h:3565), and the embedded BSD isadetection notice is retained separately. The uucode root license links its two bundled notices, retained at their relative paths. Unlinked platform/rendering dependencies downloaded by Zig are not included just because they were downloaded.

The frontend production dependencies are bundled into the web assets; build-only TypeScript/Vite/type packages are not redistributed as libraries. Bundled theme attribution is also retained.

The root MIT license covers MelfisDrake's own contributions; it does not replace upstream copyrights or grant trademark rights.

The application icon (`src/AgShell.App/Assets/agshell.ico`) is a redraw/adaptation of agterm's four-pane icon, Copyright (c) 2026 Umputun, with AgShell modifications by MelfisDrake. The pinned repository includes the original at `agterm/AppIcon.icon/Assets/21E595C3-2B37-478F-8762-21397D63C722.png` and its site favicon under `site/assets/`, with the root MIT license and no separate asset exception found. Retain `licenses/agterm.txt`. This attribution does not claim Microsoft trademark rights or endorsement by Microsoft or agterm.

## Redistributed managed runtime and SDK libraries

The self-contained Windows application includes Microsoft.NETCore.App.Runtime.win-x64 and Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.14 (NuGet source commit `0427b9abbead3fa64c6655eb4290c537397131fa`). Their MIT license and the runtime's original third-party notice file are retained. These are redistribution notices for bundled runtime code, not licenses for a prerequisite SDK installation.

Microsoft.Web.WebView2 1.0.4258.31 contributes the managed control and native loader to the self-contained application; its package LICENSE.txt requires binary distributions to reproduce its copyright, terms and disclaimer. Its original NOTICE.txt accompanies it. The separately installed Microsoft Edge WebView2 browser runtime is not bundled by AgShell.

CommunityToolkit.WinUI.Notifications 7.1.2 (source `72205c9add7c3fc1ed63bb77e6fc101e39f1ac33`), System.CommandLine 2.0.13 and Tomlyn 2.10.1 are direct redistributed dependencies. Their MIT/BSD notices are retained below, including when their code is part of a single-file or Native AOT executable. This is an addressed check of these runtime and direct package dependencies, not an audit of all build SDK components.

The self-contained application also bundles `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` from Microsoft.Windows.SDK.NET.Ref **10.0.19041.57** (`lib/net8.0`). The projection reports assembly/file versions **10.0.19041.38 / 10.0.19041.55**; WinRT.Runtime reports **2.2.0.0 / 2.2.0.48161**. The package's nuspec declares Copyright Microsoft Corporation and https://aka.ms/WinSDKLicenseURL, with no license file or source commit in the package. The original Microsoft Windows SDK license document and its plain-text rendering are retained below; AgShell's own MIT does not replace these terms. The official [CsWinRT 2.2.0 release](https://github.com/microsoft/CsWinRT/releases/tag/2.2.0.241111.1), source `8649ee3eeb2445ca2a36d80d878ef60b96a6c65d`, identifies the .57 SDK projections; its MIT license for CsWinRT is retained separately.

## Files

### `licenses/ghostty.txt`

Ghostty 8af6897c0afc63037a8a3efee4162a380e3a4572; https://github.com/ghostty-org/ghostty/blob/8af6897c0afc63037a8a3efee4162a380e3a4572/LICENSE

SHA-256: `386211873e5b7a02f663ae4d7adf96285999f91608f8f9f31fecfd0f4095e6f1`

### `licenses/zmx.txt`

zmx copied VT routines: 8bab1f0173b07e79835ea372d749af3dbf0d0842, with fixes from 186393f / 770eb5f / 2626f17 (native/agshell-vt/src/lib.zig); https://github.com/neurosnap/zmx

SHA-256: `56bab989df218f62a6deb746b43903340291dafbc7a811e3fa0c8ea889f8f5ea`

### `licenses/agterm.txt`

agterm e0b48445ad0a59c7e767b3eb54be1a31d258c3e2: Ghostty/zmx patches and native routines, AgentStatus behavior/tests, agent hook and shell integration scripts, and adapted CLI/skill documentation; https://github.com/umputun/agterm/blob/e0b48445ad0a59c7e767b3eb54be1a31d258c3e2/LICENSE

SHA-256: `155d97114b2d9b7f66181d0ba258aca57f97b8b957dd1662745bf57218c41ef3`

### `licenses/themes.txt`

Bundled themes; source license retained in src/themes/LICENSE-iTerm2-Color-Schemes

SHA-256: `2373168614b527494dff99eb393024608216db79f687d58bad420f6c1e0ad192`

### `licenses/markdig.txt`

Markdig 1.5.0; NuGet repository commit 9900e46eacd9e17ba32f1b441a349bcab61777af; https://raw.githubusercontent.com/xoofx/markdig/9900e46eacd9e17ba32f1b441a349bcab61777af/license.txt

SHA-256: `7423242b4ae72bccdf19a06cd3c20790df8519a164a08f37b31cb4a40034d827`

### `licenses/conpty.txt`

Microsoft.Windows.Console.ConPTY 1.24.261001001 declares MIT and copyright Microsoft Corporation in its nuspec. https://raw.githubusercontent.com/microsoft/terminal/7ff11ee107c984e236960ec948b450ccbc10187b/LICENSE; linked release package binary payloads verified above.

SHA-256: `ad0cf28f3381ca9bb0bf101d127402d44c17bfa0991e1a00bff7ae6679e9dada`

### `licenses/uucode/LICENSE.md`

Ghostty pinned dependency archive uucode-2826a37a4562284fdacd8fa029d49509cc9bffcd.tar.gz; original LICENSE.md

SHA-256: `312e901e142be2477b4ca859e9311f9e3f80d33372991759b7921c1893605f33`

### `licenses/uucode/licenses/LICENSE_Bjoern_Hoehrmann`

Ghostty pinned dependency archive uucode-2826a37a4562284fdacd8fa029d49509cc9bffcd.tar.gz; original licenses/LICENSE_Bjoern_Hoehrmann

SHA-256: `de219cece932aad5a817bf763393d8d149d378a15d2ad5320e3331eac07626dd`

### `licenses/uucode/licenses/LICENSE_unicode`

Ghostty pinned dependency archive uucode-2826a37a4562284fdacd8fa029d49509cc9bffcd.tar.gz; original licenses/LICENSE_unicode

SHA-256: `1eda5a3b026870c737b22e8bcd4954338612c790db688242e003f41a4fa95175`

### `licenses/highway-BSD3.txt`

Ghostty pinned dependency archive highway-66486a10623fa0d72fe91260f96c892e41aceb06.tar.gz; original LICENSE-BSD3

SHA-256: `d25e82e26acd42ca3ccc9993622631163425b869b9e16284226d534cff6470f2`

### `licenses/wuffs-MIT.txt`

Ghostty pinned dependency archive wuffs-7411f488fe2e2c205c3d3b3d28638b7356522930.tar.gz; original LICENSE-MIT

SHA-256: `bb811a4b485eac3caeb8bdc58bbafb355fbcb6116010a7ea9179e5c70b0bc878`

### `licenses/simdutf-MIT.txt`

Vendored Ghostty pkg/simdutf/vendor/simdutf.h declares SIMDUTF_VERSION 9.0.0 (package metadata 5.2.8 is stale); https://raw.githubusercontent.com/simdutf/simdutf/v9.0.0/LICENSE-MIT

SHA-256: `fc8dbc04e03ad4efc08a647ffe7f995b811a95bc04c0e85a56d5277c6593fa5f`

### `licenses/simdutf-isadetection-BSD3.txt`

Verbatim embedded BSD notice in Ghostty 8af6897 pkg/simdutf/vendor/simdutf.h, include/simdutf/internal/isadetection.h

SHA-256: `204bbed08ddeeecb362b4da36d3479a0167b7356a9d6024a24f9833affbe228a`

### `licenses/web/xterm-xterm.txt`

@xterm/xterm 6.0.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `b569f629d00f2626a8100df2a1798210535621e42164dfd426a6fe5aac7b0ccd`

### `licenses/web/xterm-addon-fit.txt`

@xterm/addon-fit 0.11.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `e256f01188af527e4d06d21d06fbf785ae9c50d4b328bf03cbe0ba7f0aa4228f`

### `licenses/web/xterm-addon-search.txt`

@xterm/addon-search 0.16.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `689ceb10716bd7a87a804c14951d7c2b818cbd60aab83d540902470ce3b13444`

### `licenses/web/xterm-addon-web-links.txt`

@xterm/addon-web-links 0.12.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `689ceb10716bd7a87a804c14951d7c2b818cbd60aab83d540902470ce3b13444`

### `licenses/web/xterm-addon-webgl.txt`

@xterm/addon-webgl 0.19.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `21b975c39532001d431dc3b2c29fbff691d83f370833a66b54698bda290fde0e`

### `licenses/web/react.txt`

react 19.3.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `da6d3703ed11cbe42bd212c725957c98da23cbff1998c05fa4b3d976d1a58e93`

### `licenses/web/react-dom.txt`

react-dom 19.3.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `da6d3703ed11cbe42bd212c725957c98da23cbff1998c05fa4b3d976d1a58e93`

### `licenses/web/scheduler.txt`

scheduler 0.28.0; original npm package LICENSE; integrity pinned in src/web/package-lock.json

SHA-256: `da6d3703ed11cbe42bd212c725957c98da23cbff1998c05fa4b3d976d1a58e93`

### `licenses/conpty-third-party.md`

Applicable sections of https://github.com/microsoft/terminal/blob/7ff11ee107c984e236960ec948b450ccbc10187b/NOTICE.md (text retained verbatim).

SHA-256: `984fed19bf7415ace01a4c75f4f9933b5cfb42b40e9e4d08a7b18cbebf31c20b`

### `licenses/conpty-GSL.txt`

GSL v3.1.0, pinned by Terminal vcpkg.json; https://raw.githubusercontent.com/microsoft/GSL/v3.1.0/LICENSE

SHA-256: `4c21dc82a1c7186ea754fd59d504c00947953a3401fe8e24eaa44109ef8e2555`

### `licenses/simdutf-APACHE.txt`

https://raw.githubusercontent.com/simdutf/simdutf/v9.0.0/LICENSE-APACHE; accompanying Apache text for the Fuchsia-derived validator credit in the vendored 9.0.0 header.

SHA-256: `3d34610fc6b5e1b0bfe4e2f36171c2d62c28ef05cb8d704f5a0073be41a43b3d`

### `licenses/system-commandline-MIT.txt`

System.CommandLine 2.0.13; nuspec source repository commit 4099f83977f4d29604a9aeb971fb0a7bc1d5f699; https://raw.githubusercontent.com/dotnet/dotnet/4099f83977f4d29604a9aeb971fb0a7bc1d5f699/src/command-line-api/LICENSE.md

SHA-256: `58fd5ebf25d75b5bb5f4cd407a9fef3befd3641e1af90a1d0716618e6430fc19`

### `licenses/tomlyn-BSD2.txt`

Tomlyn 2.10.1; nuspec source commit 6cc82dd23a2c0aefb858f92a51c1b184e2812103; https://raw.githubusercontent.com/xoofx/Tomlyn/6cc82dd23a2c0aefb858f92a51c1b184e2812103/license.txt

SHA-256: `48466291c29a20294cbc70944ded1c6c623640bef82c563026e6e86f620ddddb`

### `licenses/dotnet-runtime-MIT.txt`

microsoft.netcore.app.runtime.win-x64 10.0.14, original NuGet package `LICENSE.TXT`; https://www.nuget.org/packages/microsoft.netcore.app.runtime.win-x64/10.0.14.

SHA-256: `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b`

### `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`

microsoft.netcore.app.runtime.win-x64 10.0.14, original NuGet package `THIRD-PARTY-NOTICES.TXT`; https://www.nuget.org/packages/microsoft.netcore.app.runtime.win-x64/10.0.14.

SHA-256: `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21`

### `licenses/dotnet-windowsdesktop-MIT.txt`

microsoft.windowsdesktop.app.runtime.win-x64 10.0.14, original NuGet package `LICENSE`; https://www.nuget.org/packages/microsoft.windowsdesktop.app.runtime.win-x64/10.0.14.

SHA-256: `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab`

### `licenses/webview2-LICENSE.txt`

microsoft.web.webview2 1.0.4258.31, original NuGet package `LICENSE.txt`; https://www.nuget.org/packages/microsoft.web.webview2/1.0.4258.31.

SHA-256: `0af8f1b807512aae39c2ac1aa4d0cae65cabecb6fd554b8439a5162a0d6eca55`

### `licenses/webview2-NOTICE.txt`

microsoft.web.webview2 1.0.4258.31, original NuGet package `NOTICE.txt`; https://www.nuget.org/packages/microsoft.web.webview2/1.0.4258.31.

SHA-256: `106423785c5b7eba0a8e61d1837f2132e9c828e20ad530f565d981c1df60dd90`

### `licenses/communitytoolkit-notifications-MIT.md`

communitytoolkit.winui.notifications 7.1.2, original NuGet package `License.md`; https://www.nuget.org/packages/communitytoolkit.winui.notifications/7.1.2.

SHA-256: `134aa3b1d2c509dcf57196ccaa7b69db0eb9ae34fa9189c3d683c02d9923175d`

### `licenses/windows-sdk-LICENSE.rtf`

Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 nuspec license URL https://aka.ms/WinSDKLicenseURL, resolved 2026-11-04 to https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf; original RTF bytes.

SHA-256: `dd07eb178e00c6bba4148457fc00ff77cd4887eb521d504186fe59c9ec8bbe62`

### `licenses/windows-sdk-LICENSE.txt`

Full plain-text rendering of windows-sdk-LICENSE.rtf via Windows RichTextBox.Text, with LF line endings; original RTF retained alongside it.

SHA-256: `5238259f6446dcaa7128b806d9b07fd28320e4b6bf05109704a8356f7449e476`

### `licenses/cswinrt-MIT.txt`

CsWinRT 2.2.0.241111.1, source 8649ee3eeb2445ca2a36d80d878ef60b96a6c65d; https://raw.githubusercontent.com/microsoft/CsWinRT/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE; original bytes.

SHA-256: `9906940f61b1f0b533fa7d99baf55178b2808fbe113ea51dfbfad8572ccd5f2b`
