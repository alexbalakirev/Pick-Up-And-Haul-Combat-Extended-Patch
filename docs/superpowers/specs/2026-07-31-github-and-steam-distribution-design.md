# GitHub and Steam Distribution Design

## Objective

Turn `PickUpAndHaulCECarryFix` into a clear, maintainable GitHub repository that can produce verified GitHub release archives and is ready for its first Steam Workshop publication.

The repository remains the source of truth. Full mod compilation stays local because it depends on purchased RimWorld assemblies and installed Workshop mod assemblies. Public CI runs only checks that do not redistribute those dependencies.

## Compatibility Requirements

- Preserve the current `About/About.xml` author and the permanent package ID `local.pickupandhaul.cecarryfix`.
- Preserve RimWorld 1.6 support and the existing Harmony, Pick Up And Haul, and Combat Extended dependencies and load order.
- Preserve the current in-place development workflow from the local RimWorld `Mods` folder.
- Keep this existing command working without additional setup on the current machine:

  ```powershell
  dotnet build .\Source\PickUpAndHaulCECarryFix.csproj -c Release
  ```

- Continue writing the release DLL and PDB directly to `Assemblies/`.
- Treat configurable build paths as additive portability features. The default paths must resolve the current standard Steam installation, while command-line properties and an ignored local properties file support other installations.
- Never copy, commit, package, or upload RimWorld, Harmony, Pick Up And Haul, or Combat Extended assemblies.
- Never modify Workshop dependency directories.

## Repository Structure

The repository will contain:

- `README.md` for player-facing and contributor-facing documentation.
- `LICENSE` containing the MIT License.
- `About/Preview.png` as the Workshop and repository preview image.
- `docs/development/` containing the four historical `Run_*.md` execution documents without rewriting their historical paths or decisions.
- `docs/RELEASING.md` containing exact GitHub and Steam release procedures.
- `docs/workshop-description.bbcode` containing copy-ready Steam Workshop listing text.
- `scripts/Test.ps1` for the dependency-free regression harness.
- `scripts/Build.ps1` for tests followed by the local Release build.
- `scripts/Validate-Mod.ps1` for metadata, dependency, assembly, preview, and folder checks.
- `scripts/Package.ps1` for creating a clean installable ZIP.
- `Source/Directory.Build.props` for standard default Steam paths.
- `Source/Local.Build.props.example` showing optional local path overrides.
- `.github/workflows/ci.yml` for public continuous integration.
- `.github/workflows/release.yml` for tag-driven GitHub release packaging.

The generated `artifacts/` directory and `Source/Local.Build.props` will be ignored by Git.

## Build Design

`Source/Directory.Build.props` will define:

- `RimWorldDir`, defaulting to the standard Steam RimWorld directory under `MSBuildProgramFiles32`.
- `RimWorldWorkshopDir`, defaulting to Workshop application directory `294100` under the same Steam installation.
- An optional import of `Source/Local.Build.props`.

Command-line MSBuild properties remain supported:

```powershell
dotnet build .\Source\PickUpAndHaulCECarryFix.csproj -c Release `
  -p:RimWorldDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:RimWorldWorkshopDir='D:\SteamLibrary\steamapps\workshop\content\294100'
```

The main project will construct every `HintPath` from these properties. A pre-build validation target will fail with a specific missing-file message when a required reference cannot be found.

`scripts/Build.ps1` will accept optional path parameters, run the regression harness, build the mod in Release configuration, and leave outputs in the existing `Assemblies/` directory.

## Validation and Packaging

`scripts/Validate-Mod.ps1` will fail when:

- `About/About.xml` is absent or malformed.
- The package ID differs from `local.pickupandhaul.cecarryfix`.
- RimWorld 1.6 is not declared.
- Harmony, Pick Up And Haul, or Combat Extended is absent from the declared dependencies or load order.
- `Assemblies/PickUpAndHaulCECarryFix.dll` is absent.
- `About/Preview.png` is absent, is not a PNG, has dimensions other than 1280 by 720, or exceeds 1 MB.
- A packaged release contains build intermediates, local path overrides, Git metadata, development documents, or external dependency DLLs.

`scripts/Package.ps1` will run validation and create:

```text
artifacts/PickUpAndHaulCECarryFix-<version>.zip
└── PickUpAndHaulCECarryFix/
    ├── About/
    ├── Assemblies/
    ├── LICENSE
    └── README.md
```

The release archive contains the committed, locally built mod assembly. GitHub Actions will not rebuild that assembly.

## Continuous Integration

`.github/workflows/ci.yml` will run on pushes and pull requests. It will:

1. Check out the repository.
2. Install the required .NET SDK.
3. Run `Source/RouteOrdering.Tests/RouteOrdering.Tests.csproj` in Release configuration.
4. Run structural validation in a CI mode that verifies metadata and repository assets but does not require proprietary references or rebuild the mod.

`.github/workflows/release.yml` will run for `v*` tags. It will:

1. Repeat tests and release validation.
2. Package the committed mod files.
3. Create a GitHub Release with the ZIP attached.

The workflow will stop on any failed test or validation error.

## Documentation

`README.md` will explain:

- The player-visible compatibility problem the mod fixes.
- RimWorld 1.6 support.
- Required mods and load order.
- Installation from Steam Workshop and GitHub Releases.
- Available settings and their defaults.
- Portable build setup and exact test/build commands.
- The distinction between public dependency-free CI and local full builds.
- Issue reporting requirements, including a RimWorld log and active mod list.
- The MIT license and source availability.

`docs/RELEASING.md` will separate:

- Local verification and in-game smoke testing.
- GitHub tag and release publication.
- First-time Steam Workshop publication.
- Later Steam Workshop updates.

`docs/workshop-description.bbcode` will provide Steam-compatible listing text describing the fix, dependencies, load order, configuration, source repository, issue reporting, and compatibility scope.

## Preview Image

`About/Preview.png` will be an original 1280 by 720 raster image under 1 MB. It will use a readable hauling and inventory visual, a restrained RimWorld-compatible color palette, and the title “Pick Up And Haul CE Carry Fix.” It will not copy RimWorld, Pick Up And Haul, or Combat Extended artwork or logos.

## Steam Workshop Publication

The initial Workshop upload is manual and performed through RimWorld while logged into the owning Steam account. The release procedure will require:

1. Run tests, build, validate, and package.
2. Launch RimWorld with Harmony, Pick Up And Haul, Combat Extended, and this local mod enabled.
3. Smoke-test startup, patched hauling behavior, settings, and the RimWorld log.
4. Upload the local mod as a new unlisted Workshop item.
5. Configure the listing title, BBCode description, dependency relationships, tags, and preview.
6. Accept the Steam Workshop legal agreement when prompted.
7. Subscribe to the uploaded item and smoke-test the downloaded Workshop copy.
8. Change visibility to public only after the subscribed-copy test passes.
9. Commit the generated `About/PublishedFileId.txt` so later uploads update the same item.

No script will store Steam credentials or automate an authenticated Workshop upload.

## Release Acceptance Criteria

A release is ready when:

1. Dependency-free regression tests pass.
2. The current root-level build command succeeds without additional configuration on the current machine.
3. The Release build updates the DLL and PDB in `Assemblies/`.
4. Structural and packaging validation passes.
5. The mod starts with all declared dependencies enabled.
6. The RimWorld log contains no Harmony patch errors from this mod.
7. Opportunistic hauling respects Combat Extended weight and bulk capacity.
8. The GitHub release ZIP installs and loads successfully.
9. For the first Steam release, the subscribed Workshop copy installs and loads successfully before public visibility is enabled.

## Non-Goals

- Building the full mod on public GitHub runners.
- Redistributing game or dependency assemblies.
- Automating Steam authentication or Workshop upload.
- Changing gameplay behavior, settings, package ID, author, supported RimWorld version, or dependency declarations.
- Rewriting historical development documents.
