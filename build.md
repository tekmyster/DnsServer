# Build Tectanium DDI Server

Install the .NET 10 SDK and Node.js. Check out this repository and its pinned library dependency as sibling directories:

```sh
git clone https://github.com/tekmyster/Tectanium-DDI-Server.git
cd Tectanium-DDI-Server
git checkout ddi-foundation
git clone https://github.com/TechnitiumSoftware/TechnitiumLibrary.git ../TechnitiumLibrary
git -C ../TechnitiumLibrary checkout 6715bf04c0a39938792cfee4d2654c5f368a1034
```

With PowerShell 7:

```powershell
./scripts/build-ddi.ps1
./scripts/test-ddi.ps1
./scripts/package-tectanium.ps1 -Version 0.1.0-alpha.2 -Runtime portable
```

Other package targets are win-x64, linux-x64 and linux-arm64. The Windows service is built from Tectanium.WindowsService. The pinned GitHub Actions workflow builds and tests distributable packages on disposable hosts. See [installation](packaging/INSTALL.md).

The library reference and engine assembly identity are compatibility dependencies. Their technical identifiers and required legal notices remain intact while product branding/versioning use Tectanium.
