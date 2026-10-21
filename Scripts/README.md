# Scripts

`build.ps1` (or double-click `build.cmd`) makes the next stable version of DiffusionNexus in
`E:\StableVersion`, the same way CodeSwitchX, ContentAutomatorX and RawCutX do. A zip to hand to
another machine is `publish.ps1` in the repo root, not this. Full help:
`Get-Help .\Scripts\build.ps1 -Full`.

## build.ps1 / build.cmd

The stable build:

1. Checks that you are on `develop`, with no uncommitted or untracked files, level with origin.
2. Builds the solution in Release and runs the tests. A red suite stops everything before anything
   is published.
3. Publishes into `E:\StableVersion\DiffusionNexus-<version>`. It is the same self-contained
   single-file build that `publish.ps1` makes for a release.
4. Links that folder to your shared data in `E:\StableVersion\DiffusionNexus-data` (see below).
5. Points the `E:\StableVersion\DiffusionNexus` junction at the new folder and writes a
   `Diffusion Nexus` Start Menu shortcut through that junction, so a taskbar pin always opens the
   newest build.

Older versions stay beside the new one. Delete them by hand when you like.

### The version

The version is `Major.Minor.Build` from `Directory.Build.props` plus the number of commits on
`develop`: `0.11.0.2483`. It is passed to the build only. Nothing is committed or pushed, so the
repo is exactly as it was afterwards. `develop` takes commits only through PRs, and `release.yml`
stamps the revision on `main` the same way, from the run number.

Each new commit on `develop` gets a new number, so it gets a new folder. Building the same commit
twice is refused: that folder is the version the Start Menu opens. Merge something new first.
To raise `Major.Minor.Build`, change `Directory.Build.props` in a PR.

### What it refuses

- **Another branch.** The stable folder only ever holds merged code. To try a branch's build, give
  it a folder of its own with `-InstallDir` (below).
- **Uncommitted or untracked files.** A build has to match a commit on `develop`.
- **A `develop` that is ahead of or behind origin.** Behind: `git pull`. Ahead: those commits never
  went through a PR, and their count would later belong to another commit.
- **A shallow clone.** Its commit count is not the real one. `git fetch --unshallow`.
- **A folder that is not ours.** Every folder the script publishes into gets a
  `diffusionnexus-install.json` marker. Nothing is ever deleted from a folder without one.

### A running DiffusionNexus

It is not closed, because it may be in the middle of a download or a generation. It keeps running
the version it was started from, and the script says so at the end. Restart it for the new one.
The one exception is a one-off build into a folder DiffusionNexus is running from: that build
refuses to start until you close it.

### Your data

The database and the settings are in `%LocalAppData%\DiffusionNexus`, so every version uses the
same ones. Two things the app writes next to its exe are shared through
`E:\StableVersion\DiffusionNexus-data`:

- **`outputs`**: the images Diffusion Canvas saves. Each version folder has an `outputs` junction
  to `DiffusionNexus-data\outputs`. When a new version becomes current, the older version folders
  lose their link, so deleting an old version can never reach your images. A version that is still
  running keeps its link until the next build.
- **`mappings.xml`**: LoRA sort's custom tag mappings. A file link needs admin rights, so this is a
  hard link: one file under two names. The new version starts with what the current one has, and
  from then on a save in either is seen by both. If you deleted all mappings in the current
  version, the new one starts without them too.

Start the app from the Start Menu or through `E:\StableVersion\DiffusionNexus`. Diffusion Canvas
registers its outputs folder in the Generation Gallery by the path the app was started from, so a
version folder started directly registers a second entry.

### build.cmd and the wait at the end

A `build.cmd` double-clicked in Explorer waits for a key when it ends, so the window stays
readable. Started from PowerShell, Git Bash, an open Command Prompt or a task, it only waits after a
failure. Another file manager is not Explorer, so a double click there closes the window after a
good build. Set `DIFFUSIONNEXUS_BUILD_PAUSE=never` for a caller that must never wait, or `=always`
to force the wait.

### Parameters

| Parameter | What it does |
|---|---|
| `-InstallDir <path>` | A one-off build into a folder of your own, from any branch. Stamped `<props version>-oneoff.g<commit>`. Must be outside the repo and the stable root. No shortcut is written and the current link does not move. It shares the outputs and `mappings.xml` of the stable versions. |
| `-Clean` | With `-InstallDir`: wipe that folder first (only if it carries the marker). |
| `-SkipTests` | Skip the test run (for when you just ran it). |
| `-NoShortcut` | Leave the Start Menu shortcut as it is. |

```powershell
.\Scripts\build.ps1                                                  # the next stable version
.\Scripts\build.ps1 -InstallDir E:\Builds\DiffusionNexus-test -SkipTests   # try this branch
```

### What it does not do

It does not regenerate the third-party notices (CI checks on every PR that the committed ones are
current), stage the GPL corresponding source, or make a zip. Those are for a release you hand out,
which is `publish.ps1`.
