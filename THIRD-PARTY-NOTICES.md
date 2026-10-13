# Third-party notices

Nothing third-party is committed to this repository except the font subsets embedded in the test PDFs. Packages are
restored from nuget.org (or PyPI) at build time under their own licences:

| Component | Version | Licence | Use |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Headless.XUnit | 12.1.3 | MIT | UI framework (build-time restore) |
| Avalonia.Controls.WebView | 12.1.0 | MIT | Embedded web view (build-time restore) |
| SkiaSharp, HarfBuzzSharp (via Avalonia) | 3.119.x / 8.3.x | MIT | Rendering (build-time restore) |
| .NET runtime (self-contained app) | 10.0 | MIT | Runtime (build-time restore) |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | Compile-only dependency of a copied TableForge file |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 3.2.2 / 3.1.5 / 18.10.0 | Apache-2.0 / MIT | Tests only |
| Pillow (Python) | 12.3.0 | MIT-CMU | Icon conversion in packaging (CI only) |
| DejaVu fonts (subsets embedded in corpus PDFs) | 2.37 | Bitstream Vera / Arev licence (permits redistribution); see corpus/LICENSE_DEJAVU.txt | Test PDFs |
| dddice JavaScript SDK and dice | loaded at run time from cdn.dddice.com | dddice terms | Not distributed here |
